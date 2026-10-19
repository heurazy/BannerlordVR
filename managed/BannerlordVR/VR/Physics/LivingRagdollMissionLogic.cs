using System;
using System.Diagnostics;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR.Physics
{
    public sealed class LivingRagdollMissionLogic : MissionLogic
    {
        private sealed class Entry : ILivingRagdollHost
        {
            public Agent Agent;
            public Skeleton Skeleton;
            public Mission Mission;
            public readonly LivingRagdollLease Lease = new LivingRagdollLease();
            public bool ImpactPending;
            public sbyte ImpactBone;
            public Vec3 ImpactForce;
            public float ImpactDeadline;
            public bool Exists => Agent != null && Agent.State != AgentState.Deleted;
            public bool Alive => Exists && Agent.IsActive();
            private bool SkeletonMatches => Skeleton != null &&
                Agent?.AgentVisuals?.GetSkeleton()?.Pointer == Skeleton.Pointer;
            public bool Safe => Alive && Mission.MainAgent != null &&
                SkeletonMatches && EnemyBody.Eligible(Agent, Mission.MainAgent, Lease.Occupied);
            public bool Paused { get => Agent.IsPaused; set => Agent.IsPaused = value; }
            public bool NativeActive => SkeletonMatches && Skeleton.GetCurrentRagdollState() == RagdollState.Active;
            public bool NativeDisabled => !SkeletonMatches || Skeleton.GetCurrentRagdollState() == RagdollState.Disabled;
            public void BeginNative() => Agent.StartRagdollAsCorpse();
            public void EndNative() => Agent.EndRagdollAsCorpse();
            public void SuppressMovement()
            {
                Agent.MovementFlags = 0;
                Agent.MovementInputVector = Vec2.Zero;
                Agent.EventControlFlags = 0;
            }
            public void ResumeDecisions() => Agent.ForceAiBehaviorSelection();
            public void Clear() { Agent = null; Skeleton = null; Mission = null; ImpactPending = false; ImpactForce = Vec3.Zero; }
        }
        private readonly Entry[] _entries = CreateEntries();
        private bool _failed;
        public bool Available => EnemyPhysicsSettings.LivingRagdolls && !_failed;
        public double LastTickCpuMilliseconds { get; private set; }
        private static Entry[] CreateEntries()
        {
            var result = new Entry[12];
            for (int i = 0; i < result.Length; i++) result[i] = new Entry();
            return result;
        }
        public bool HasLease(Agent agent)
        {
            foreach (Entry entry in _entries) if (entry.Agent == agent && entry.Lease.Occupied) return true;
            return false;
        }
        public bool Simulating(Agent agent)
        {
            foreach (Entry entry in _entries)
                if (entry.Agent == agent && entry.Lease.OwnsPhysics) return true;
            return false;
        }
        public bool KeepHeld(EnemyBody body, bool allowBegin = false)
        {
            if (_failed || !EnemyPhysicsSettings.LivingRagdolls || body.IsCorpse) return false;
            foreach (Entry entry in _entries)
            {
                if (entry.Agent != body.Agent || !entry.Lease.Occupied) continue;
                if (entry.Lease.Phase == LivingRagdollPhase.Recovering) return false;
                entry.Lease.KeepHeld(Mission.CurrentTime);
                return true;
            }
            if (!allowBegin) return false;
            foreach (Entry entry in _entries)
            {
                if (entry.Lease.Occupied || entry.Lease.Faulted) continue;
                entry.Agent = body.Agent; entry.Skeleton = body.Skeleton; entry.Mission = Mission;
                try
                {
                    if (entry.Lease.Begin(entry, Mission.CurrentTime)) return true;
                    entry.Clear(); return false;
                }
                catch (Exception ex)
                {
                    _failed = true; entry.Clear();
                    VrLog.Error("Living ragdoll activation failed; backend stopped for this mission.", ex);
                    return false;
                }
            }
            return false;
        }
        public void Release(Agent agent)
        {
            foreach (Entry entry in _entries)
                if (entry.Agent == agent && entry.Lease.Occupied) SafeRelease(entry);
        }
        public void Impact(EnemyBody body, BodyPoint point, Vec3 velocity)
        {
            if (!KeepHeld(body, true)) return;
            foreach (Entry entry in _entries)
            {
                if (entry.Agent != body.Agent || !entry.Lease.Occupied ||
                    entry.Lease.Phase == LivingRagdollPhase.Recovering) continue;
                entry.Lease.KeepHeld(Mission.CurrentTime, .6f);
                entry.ImpactPending = true; entry.ImpactBone = point.Bone;
                entry.ImpactForce = InteractionMath.ClampMagnitude(velocity * 25, EnemyPhysicsSettings.MaxForce);
                entry.ImpactDeadline = Mission.CurrentTime + .35f;
                return;
            }
        }
        public void ReleaseAll()
        {
            foreach (Entry entry in _entries)
                if (entry.Lease.Occupied) SafeRelease(entry);
        }
        private void SafeRelease(Entry entry)
        {
            try { entry.Lease.RequestRelease(Mission.CurrentTime); }
            catch (Exception ex)
            {
                _failed = true;
                try { entry.Lease.EmergencyStop(); }
                catch (Exception cleanup) { VrLog.Error("Living ragdoll emergency cleanup failed.", cleanup); }
                finally { entry.Clear(); }
                VrLog.Error("Living ragdoll release failed; backend stopped for this mission.", ex);
            }
        }
        public override void OnMissionTick(float dt)
        {
            long started = Stopwatch.GetTimestamp();
            foreach (Entry entry in _entries)
            {
                if (!entry.Lease.Occupied) continue;
                try
                {
                    entry.Lease.Tick(Mission.CurrentTime);
                    if (entry.ImpactPending)
                    {
                        if (Mission.CurrentTime > entry.ImpactDeadline || !entry.Lease.Occupied) entry.ImpactPending = false;
                        else if (entry.Lease.OwnsPhysics && entry.NativeActive)
                        {
                            entry.Agent.ApplyForceOnRagdoll(entry.ImpactBone, in entry.ImpactForce);
                            entry.ImpactPending = false;
                        }
                    }
                    if (entry.Lease.Faulted)
                    {
                        _failed = true;
                        VrLog.Info("Living ragdoll recovery did not finish in time; backend stopped for this mission.");
                    }
                    if (!entry.Lease.Occupied) entry.Clear();
                }
                catch (Exception ex)
                {
                    _failed = true;
                    try { entry.Lease.EmergencyStop(); }
                    catch (Exception cleanup) { VrLog.Error("Living ragdoll emergency cleanup failed.", cleanup); }
                    finally { entry.Clear(); }
                    VrLog.Error("Living ragdoll recovery failed; backend stopped for this mission.", ex);
                }
            }
            LastTickCpuMilliseconds = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
        }
        public override void OnAgentDeleted(Agent affectedAgent)
        {
            foreach (Entry entry in _entries) if (entry.Agent == affectedAgent)
            { entry.Lease.AbandonDeleted(); entry.Clear(); }
        }
        public override void OnAgentRemoved(Agent affectedAgent, Agent affectorAgent, AgentState agentState, KillingBlow blow)
        {
            // Mission sets State and invokes this before Agent.OnRemove tears
            // down its components. Transfer a real death before that teardown.
            foreach (Entry entry in _entries)
            {
                if (entry.Agent != affectedAgent || !entry.Lease.Occupied) continue;
                try { entry.Lease.Tick(Mission.CurrentTime); }
                catch (Exception ex) { _failed = true; VrLog.Error("Living ragdoll death handoff failed.", ex); }
                finally { entry.Lease.AbandonDeleted(); entry.Clear(); }
            }
        }
        public override void OnAgentMount(Agent agent) => Release(agent);
        protected override void OnEndMission() => Shutdown();
        public override void OnRemoveBehavior() => Shutdown();
        private void Shutdown()
        {
            // Mission calls OnEndMission before Agent.OnRemove and scene teardown.
            foreach (Entry entry in _entries)
            {
                try { entry.Lease.EmergencyStop(); }
                catch (Exception ex) { VrLog.Error("Living ragdoll shutdown failed.", ex); }
                finally { entry.Clear(); }
            }
        }
    }
}
