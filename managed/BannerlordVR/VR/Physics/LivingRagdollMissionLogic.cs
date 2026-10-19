using System;
using System.Diagnostics;
using System.Globalization;
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
            public readonly LivingBodyBalance Balance = new LivingBodyBalance();
            public bool ImpactPending;
            public sbyte ImpactBone;
            public Vec3 ImpactForce;
            public float ImpactDeadline;
            public bool ReleasePending;
            public bool Exists => Agent != null && Agent.State != AgentState.Deleted;
            public bool Alive => Exists && Agent.IsActive();
            private bool SkeletonMatches => Skeleton != null &&
                Agent?.AgentVisuals?.GetSkeleton()?.Pointer == Skeleton.Pointer;
            public bool Safe => Alive && Mission.MainAgent != null &&
                SkeletonMatches && EnemyBody.Eligible(Agent, Mission.MainAgent, Lease.Occupied);
            public bool Paused { get => Agent.IsPaused; set => Agent.IsPaused = value; }
            public bool NativeActive => SkeletonMatches && Skeleton.GetCurrentRagdollState() == RagdollState.Active;
            public bool NativeDisabled => !SkeletonMatches || Skeleton.GetCurrentRagdollState() == RagdollState.Disabled;
            public void BeginNative()
            {
                if (!NativeRagdollBridge.Preflight(Agent, Mission, Skeleton))
                    throw new InvalidOperationException("Living ragdoll activation refused by recovery preflight.");
                if (EnemyPhysicsSettings.ActiveBalance)
                    VrLog.Info("BALANCE capture ready=" + Balance.Capture(Agent, Skeleton, Mission));
                Agent.StartRagdollAsCorpse();
            }
            public void EndNative()
            {
                if (!SkeletonMatches || NativeDisabled) { ReleasePending = false; return; }
                RagdollState state = Skeleton.GetCurrentRagdollState();
                if (state == RagdollState.NeedsActivation) { ReleasePending = true; return; }
                NativeRagdollBridge.Release(Agent, Mission, Skeleton);
                ReleasePending = false;
            }
            public void SuppressMovement()
            {
                Agent.MovementFlags = 0;
                Agent.MovementInputVector = Vec2.Zero;
                Agent.EventControlFlags = 0;
            }
            public void ResumeDecisions() => Agent.ForceAiBehaviorSelection();
            public void Clear() { Balance.Clear(); Agent = null; Skeleton = null; Mission = null; ImpactPending = ReleasePending = false; ImpactForce = Vec3.Zero; }
        }
        private readonly Entry[] _entries = CreateEntries();
        private bool _failed;
        private Entry _probeEntry;
        private bool _probeStarted, _probeFinished, _probeSawActive;
        private bool _probeInspectedForceApi;
        private float _probeHealth, _probeMaxDisplacement;
        private Vec3 _probeOrigin;
        private RagdollState _probeLastState;
        private Agent _probeAgent;
        private Skeleton _probeSkeleton;
        private float _probeFollowUntil;
        private float _probeBattleBegan = -1;
        private float _probeBegan, _probeMaxDrop, _probePeakSupport;
        private bool _probeBalanceCaptured, _probeBalanceFell;
        private int _probeBalanceTicks;
        private Vec3 _probeRecoveryOrigin;
        private float _probeNextFollow, _probeLocomotionDistance;
        public bool Available => EnemyPhysicsSettings.LivingRagdolls && NativeRagdollBridge.Ready && !_failed;
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
            if (!Available || body.IsCorpse) return false;
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
                    if (!NativeRagdollBridge.Preflight(entry.Agent, Mission, entry.Skeleton))
                    { _failed = true; entry.Clear(); return false; }
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
            if ((EnemyPhysicsSettings.RuntimeProbe && !EnemyPhysicsSettings.WoundProbe) || EnemyPhysicsSettings.LivingRagdolls)
                NativeRagdollBridge.BindMissionTick(Mission);
            if (EnemyPhysicsSettings.RuntimeProbe && !EnemyPhysicsSettings.WoundProbe)
            {
                try { StartProbe(); FollowProbe(); }
                catch (Exception ex)
                {
                    _probeStarted = _probeFinished = _failed = true;
                    if (_probeEntry != null) SafeRelease(_probeEntry);
                    VrLog.Error("PHYSICS_PROBE result=FAIL reason=managed_exception", ex);
                }
            }
            foreach (Entry entry in _entries)
            {
                if (!entry.Lease.Occupied) continue;
                try
                {
                    if (entry == _probeEntry && !_probeFinished) ObserveProbe(entry);
                    if (entry.ReleasePending && entry.Alive && entry.NativeActive) entry.EndNative();
                    entry.Lease.Tick(Mission.CurrentTime);
                    if (entry.Lease.OwnsPhysics && entry.NativeActive)
                    {
                        entry.Balance.Tick(entry.Agent, entry.Skeleton, Mission, dt);
                        if (entry == _probeEntry && !_probeFinished && EnemyPhysicsSettings.ActiveBalance)
                        {
                            _probeBalanceTicks = entry.Balance.AppliedTicks;
                            _probePeakSupport = Math.Max(_probePeakSupport, entry.Balance.PeakForce);
                            _probeBalanceFell |= _probeBalanceCaptured && entry.Balance.Phase != BodyBalancePhase.Supporting;
                        }
                    }
                    else if (entry.Lease.Phase != LivingRagdollPhase.Activating) entry.Balance.Clear();
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
                    if (!entry.Lease.Occupied)
                    {
                        if (entry == _probeEntry && !_probeFinished) FinishProbe(entry);
                        entry.Clear();
                    }
                }
                catch (Exception ex)
                {
                    _failed = true;
                    if (entry == _probeEntry && !_probeFinished)
                    {
                        _probeFinished = true;
                        VrLog.Info("PHYSICS_PROBE result=FAIL reason=managed_native_transition_exception");
                    }
                    try { entry.Lease.EmergencyStop(); }
                    catch (Exception cleanup) { VrLog.Error("Living ragdoll emergency cleanup failed.", cleanup); }
                    finally { entry.Clear(); }
                    VrLog.Error("Living ragdoll recovery failed; backend stopped for this mission.", ex);
                }
            }
            LastTickCpuMilliseconds = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
        }
        private Vec3 ProbePelvis(Entry entry) => entry.Agent.AgentVisuals.GetEntity().GetGlobalFrame()
            .TransformToParent(entry.Skeleton.GetBoneEntitialFrameWithIndex(entry.Agent.Monster.PelvisBoneIndex)).origin;
        private void StartProbe()
        {
            if (_probeStarted || Mission.Mode != MissionMode.Battle || Mission.MainAgent == null) return;
            if (_probeBattleBegan < 0) _probeBattleBegan = Mission.CurrentTime;
            float elapsed = Mission.CurrentTime - _probeBattleBegan;
            if (elapsed < (EnemyPhysicsSettings.ActiveBalance ? .05f : 3)) return;
            if (GameNetwork.IsMultiplayer || Game.Current?.GameType?.GetType().FullName != "TaleWorlds.MountAndBlade.CustomBattle.CustomGame")
            {
                _probeStarted = _probeFinished = true;
                VrLog.Info("PHYSICS_PROBE result=INCONCLUSIVE reason=not_terrestrial_custom_battle");
                return;
            }
            if (!NativeRagdollBridge.Ready)
            {
                _probeStarted = _probeFinished = true;
                VrLog.Info("PHYSICS_PROBE result=INCONCLUSIVE reason=native_recovery_bridge_unavailable");
                return;
            }
            if (elapsed > 60)
            {
                _probeStarted = _probeFinished = true;
                VrLog.Info("PHYSICS_PROBE result=INCONCLUSIVE reason=no_isolated_eligible_enemy");
                return;
            }
            foreach (Agent agent in Mission.Agents)
            {
                if (!EnemyBody.Eligible(agent, Mission.MainAgent) || !agent.IsActive() || agent.IsPaused ||
                    !agent.IsAIControlled || (agent.Position - Mission.MainAgent.Position).Length < 20) continue;
                if (EnemyPhysicsSettings.ActiveBalance && agent.Velocity.Length > .3f) continue;
                Skeleton skeleton = agent.AgentVisuals?.GetSkeleton();
                if (skeleton == null || skeleton.GetCurrentRagdollState() != RagdollState.Disabled ||
                    agent.Monster.PelvisBoneIndex < 0 || agent.Monster.PelvisBoneIndex >= skeleton.GetBoneCount()) continue;
                foreach (Entry entry in _entries)
                {
                    if (entry.Lease.Occupied) continue;
                    entry.Agent = agent; entry.Skeleton = skeleton; entry.Mission = Mission;
                    if (!NativeRagdollBridge.Preflight(agent, Mission, skeleton))
                    {
                        _probeStarted = _probeFinished = true; entry.Clear();
                        VrLog.Info("PHYSICS_PROBE result=INCONCLUSIVE reason=native_recovery_preflight_refused");
                        return;
                    }
                    _probeStarted = true; _probeEntry = entry; _probeHealth = agent.Health;
                    _probeBegan = Mission.CurrentTime;
                    _probeOrigin = ProbePelvis(entry); _probeLastState = RagdollState.Disabled;
                    VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                        "PHYSICS_PROBE begin agent={0} time={1:F3} health={2:F1} state={3} paused={4}",
                        agent.Index, Mission.CurrentTime, agent.Health, agent.State, agent.IsPaused));
                    if (!entry.Lease.Begin(entry, Mission.CurrentTime))
                    {
                        _probeFinished = true; entry.Clear();
                        VrLog.Info("PHYSICS_PROBE result=INCONCLUSIVE reason=activation_rejected");
                    }
                    else
                    {
                        entry.Lease.KeepHeld(Mission.CurrentTime, .6f);
                        _probeBalanceCaptured = entry.Balance.Captured;
                    }
                    return;
                }
            }
        }
        private void ObserveProbe(Entry entry)
        {
            if (!entry.Exists || !entry.Alive || !entry.Safe) return;
            RagdollState state = entry.Skeleton.GetCurrentRagdollState();
            if (state != _probeLastState)
            {
                VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                    "PHYSICS_PROBE transition time={0:F3} native={1} lease={2} paused={3}",
                    Mission.CurrentTime, state, entry.Lease.Phase, entry.Agent.IsPaused));
                _probeLastState = state;
            }
            if (state == RagdollState.Active)
            {
                if (EnemyPhysicsSettings.ActiveBalance && Mission.CurrentTime - _probeBegan < 1.2f)
                    entry.Lease.KeepHeld(Mission.CurrentTime);
                _probeSawActive = true;
                if (!_probeInspectedForceApi)
                {
                    _probeInspectedForceApi = true;
                    NativeRagdollBridge.LogForceApi(entry.Agent, Mission, entry.Skeleton, entry.Agent.Monster.PelvisBoneIndex);
                }
                _probeMaxDisplacement = Math.Max(_probeMaxDisplacement, (ProbePelvis(entry) - _probeOrigin).Length);
                _probeMaxDrop = Math.Max(_probeMaxDrop, _probeOrigin.z - ProbePelvis(entry).z);
            }
        }
        private void FinishProbe(Entry entry)
        {
            _probeFinished = true;
            bool alive = entry.Alive;
            bool disabled = alive && entry.NativeDisabled;
            bool paused = alive && entry.Agent.IsPaused;
            bool sameHealth = alive && Math.Abs(entry.Agent.Health - _probeHealth) < .01f;
            bool passed = _probeSawActive && disabled && !paused && sameHealth && !entry.Lease.Faulted &&
                (_probeMaxDisplacement > .02f || (EnemyPhysicsSettings.ActiveBalance && _probeBalanceTicks >= 10));
            VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                "PHYSICS_PROBE result={0} active_seen={1} native_disabled={2} alive={3} paused={4} same_health={5} max_pelvis_displacement_m={6:F3}",
                passed ? "PASS" : "FAIL", _probeSawActive, disabled, alive, paused, sameHealth, _probeMaxDisplacement));
            if (EnemyPhysicsSettings.ActiveBalance)
                VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                    "BALANCE_PROBE result={0} captured={1} force_ticks={2} support_lost={3} max_drop_m={4:F3} peak_force={5:F1}",
                    passed && _probeBalanceCaptured && _probeBalanceTicks >= 10 && !_probeBalanceFell && _probeMaxDrop < .25f ? "PASS" : "FAIL",
                    _probeBalanceCaptured, _probeBalanceTicks, _probeBalanceFell, _probeMaxDrop, _probePeakSupport));
            if (alive)
            {
                _probeAgent = entry.Agent; _probeSkeleton = entry.Skeleton;
                _probeFollowUntil = Mission.CurrentTime + 10;
                _probeNextFollow = Mission.CurrentTime;
                _probeRecoveryOrigin = entry.Agent.Position;
            }
        }
        private void FollowProbe()
        {
            if (_probeAgent == null) return;
            bool alive = _probeAgent.State != AgentState.Deleted && _probeAgent.IsActive();
            bool matches = alive && _probeAgent.AgentVisuals?.GetSkeleton()?.Pointer == _probeSkeleton.Pointer;
            if (!matches)
            {
                VrLog.Info("PHYSICS_PROBE followup=STOP reason=agent_removed_or_skeleton_replaced");
                _probeAgent = null; _probeSkeleton = null;
                return;
            }
            RagdollState state = _probeSkeleton.GetCurrentRagdollState();
            bool done = Mission.CurrentTime >= _probeFollowUntil;
            _probeLocomotionDistance = Math.Max(_probeLocomotionDistance, (_probeAgent.Position - _probeRecoveryOrigin).Length);
            if (state != _probeLastState || done || Mission.CurrentTime >= _probeNextFollow)
            {
                VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                    "PHYSICS_PROBE followup time={0:F3} native={1} alive={2} paused={3} same_health={4} observation_done={5} root_distance_m={6:F3} speed={7:F3} action={8}",
                    Mission.CurrentTime, state, alive, _probeAgent.IsPaused,
                    Math.Abs(_probeAgent.Health - _probeHealth) < .01f, done, _probeLocomotionDistance,
                    _probeAgent.Velocity.Length, _probeAgent.GetCurrentActionType(0)));
                _probeLastState = state;
                _probeNextFollow = Mission.CurrentTime + .5f;
            }
            if (done) { _probeAgent = null; _probeSkeleton = null; }
        }
        public override void OnAgentDeleted(Agent affectedAgent)
        {
            if (_probeAgent == affectedAgent) { _probeAgent = null; _probeSkeleton = null; }
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
            _probeAgent = null; _probeSkeleton = null;
            if (_probeStarted && !_probeFinished)
            {
                _probeFinished = true;
                VrLog.Info("PHYSICS_PROBE result=INCONCLUSIVE reason=mission_ended_before_observation");
            }
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
