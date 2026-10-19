using System;
using System.Collections.Generic;
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
            public readonly ActiveRagdollBody Active = new ActiveRagdollBody();
            public bool StartDestabilized;
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
                if (EnemyPhysicsSettings.ActiveRagdoll)
                    VrLog.Info("ACTIVE_RAGDOLL capture ready=" + Active.Capture(Agent, Skeleton, StartDestabilized));
                else if (EnemyPhysicsSettings.ActiveBalance)
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
            // Agent.IsPaused only stops AI decisions: the engine kept walking a
            // paused agent towards its last target, 15-28 m away from its body
            // in a battle test. Hold the agent where its body went physical.
            private bool _held;
            private Vec2 _holdPosition;
            public void SuppressMovement()
            {
                if (!_held) { _held = true; _holdPosition = Agent.Position.AsVec2; }
                Agent.MovementFlags = 0;
                Agent.MovementInputVector = Vec2.Zero;
                Agent.EventControlFlags = 0;
                Agent.SetMaximumSpeedLimit(0, false);
                Agent.SetTargetPosition(_holdPosition);
            }
            public void ResumeDecisions()
            {
                _held = false;
                // Same reset Bannerlord applies when control changes hands.
                Agent.SetMaximumSpeedLimit(-1, false);
                Agent.ForceAiBehaviorSelection();
            }
            public void Clear() { _held = false; Balance.Clear(); Active.Clear(); StartDestabilized = false; Agent = null; Skeleton = null; Mission = null; ImpactPending = ReleasePending = false; ImpactForce = Vec3.Zero; }
        }
        private readonly Entry[] _entries = CreateEntries();
        private readonly PhysicsStepCounter _clock = new PhysicsStepCounter();
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
        private float _probeBegan, _probeMaxDrop, _probeMaxRise, _probePeakSupport;
        private bool _probeBalanceCaptured, _probeBalanceFell;
        private int _probeBalanceTicks, _probeBalanceSkipped;
        private long _probeBalanceStepsAt = -1, _probeBalanceStepsEnd = -1;
        private Vec3 _probeRecoveryOrigin;
        private float _probeNextFollow, _probeLocomotionDistance;
        private static bool _warmed;
        // The first push, fall and stand-up each cost several ms of JIT on
        // the mission thread (1.6, 7.3 and 16 ms ticks); compile while the
        // mission loads. Mission behaviours added from OnMissionBehaviorInitialize
        // never receive OnBehaviorInitialize, so this runs at construction.
        public LivingRagdollMissionLogic()
        {
            if (_warmed) return;
            _warmed = true;
            if (EnemyPhysicsSettings.ActiveRagdoll)
                JitWarmup.Prepare(typeof(ActiveRagdollBody), typeof(RagdollPinDrive), typeof(ActiveRagdollCycle),
                    typeof(PushReaction), typeof(RagdollGrabRules), typeof(PenetrationJoint), typeof(BodyPenetration),
                    typeof(PhysicsStepCounter), typeof(PhysicsStepGate), typeof(NativeRagdollBridge), typeof(LivingRagdollLease),
                    typeof(LivingRagdollMissionLogic), typeof(InteractionMath));
        }
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
        private static float LeaseSeconds => EnemyPhysicsSettings.ActiveRagdoll ? 14 : 6;
        public bool KeepHeld(EnemyBody body, bool allowBegin = false, bool destabilized = false)
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
                entry.StartDestabilized = destabilized;
                try
                {
                    if (!NativeRagdollBridge.Preflight(entry.Agent, Mission, entry.Skeleton))
                    { _failed = true; entry.Clear(); return false; }
                    if (entry.Lease.Begin(entry, Mission.CurrentTime, LeaseSeconds)) return true;
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
        public void Impact(EnemyBody body, BodyPoint point, Vec3 velocity, PushEffect push = PushEffect.None, float hitMass = 2)
        {
            if (EnemyPhysicsSettings.ActiveRagdoll) { ActiveImpact(body, point, velocity, push, hitMass); return; }
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
        // B&S: the colliding item's momentum reaches the part through the
        // contact over one simulation step. A destabilizing push hands the
        // living enemy to the physical body; lesser pushes only nudge a body
        // that is already simulated.
        private void ActiveImpact(EnemyBody body, BodyPoint point, Vec3 velocity, PushEffect push, float hitMass)
        {
            if (!Available || body.IsCorpse || !InteractionMath.IsFinite(velocity)) return;
            Vec3 force = velocity * (Math.Max(.5f, Math.Min(8, hitMass)) * 90);
            Entry entry = Find(body.Agent);
            if (entry == null)
            {
                if (push != PushEffect.Destabilize || !KeepHeld(body, true, true)) return;
                entry = Find(body.Agent);
                if (entry == null) return;
            }
            else if (push == PushEffect.Destabilize) entry.Active.Destabilize();
            if (entry.Lease.Phase == LivingRagdollPhase.Recovering) return;
            entry.Active.QueuePush(point.Bone, force);
            entry.Active.LastPusher = Mission.MainAgent;
        }
        public bool HandleActive(EnemyBody body, int hand, BodyPoint point, Vec3 handWorld, MatrixFrame boneWorld, Vec3 contact)
        {
            Entry entry = Find(body.Agent);
            if (entry == null || !entry.Lease.OwnsPhysics) return false;
            entry.Lease.KeepHeld(Mission.CurrentTime);
            return entry.Active.Handle(hand, point.Bone, Classify(body, point), handWorld, boneWorld, contact);
        }
        public void ReleaseActive(EnemyBody body, int hand) => Find(body.Agent)?.Active.ReleaseHandle(hand);
        // Human handle used by B&S for the part the hand closed on.
        private static GrabHandle Classify(EnemyBody body, BodyPoint point)
        {
            string name = body.Skeleton.GetBoneName(point.Bone) ?? "";
            switch (point.Part)
            {
                case BoneBodyPartType.Head: return GrabHandle.Head;
                case BoneBodyPartType.Neck: return GrabHandle.Neck;
                case BoneBodyPartType.Legs: return GrabHandle.Legs;
                case BoneBodyPartType.ShoulderLeft:
                case BoneBodyPartType.ShoulderRight: return GrabHandle.Shoulder;
                case BoneBodyPartType.ArmLeft:
                case BoneBodyPartType.ArmRight: return name.EndsWith("hand") ? GrabHandle.Hand : GrabHandle.Wrist;
                default: return GrabHandle.Torso;
            }
        }
        // BrainModuleMelee parry recoil: recoilMinWeaponSpeed 1 m/s, pushLevel 2
        // against an enemy (full recoil, attack stopped), parryRecoilCooldown
        // 1.5 s (Brain_Human*.json).
        public const float RecoilMinWeaponSpeed = 1, ParryRecoilCooldown = 1.5f;
        private readonly Dictionary<Agent, float> _lastParry = new Dictionary<Agent, float>();
        public bool Parry(EnemyBody body, float weaponSpeed)
        {
            Agent agent = body?.Agent;
            if (agent == null || body.IsCorpse || !agent.IsActive() || !InteractionMath.IsFinite(weaponSpeed) ||
                weaponSpeed <= RecoilMinWeaponSpeed) return false;
            Agent.ActionStage stage = agent.GetCurrentActionStage(1);
            if (stage != Agent.ActionStage.AttackRelease && stage != Agent.ActionStage.AttackReady &&
                stage != Agent.ActionStage.AttackQuickReady) return false;
            float now = Mission.CurrentTime;
            if (_lastParry.TryGetValue(agent, out float last) && now - last <= ParryRecoilCooldown) return false;
            _lastParry[agent] = now;
            // meleeModule.StopAttack, then the full-stagger recoil window.
            agent.SetActionChannel(1, ActionIndexCache.act_none, ignorePriority: true);
            _staggers[agent] = new Stagger { Until = now + .5f + PushReaction.Recoil(PushEffect.StaggerFull), Effect = PushEffect.StaggerFull };
            VrLog.Info(string.Format(CultureInfo.InvariantCulture, "PARRY agent={0} stage={1} weapon_speed={2:F2} time={3:F3}",
                agent.Index, stage, weaponSpeed, now));
            return true;
        }
        public long PhysicsSteps => _clock.Running ? _clock.Steps : -1;
        public float PartMass(Agent agent, sbyte bone) => Find(agent)?.Active.PartMass(bone) ?? 5;
        private Entry Find(Agent agent)
        {
            foreach (Entry entry in _entries) if (entry.Agent == agent && entry.Lease.Occupied) return entry;
            return null;
        }

        // Context for the B&S human push table: idle without a target, attack
        // while an attack is readied or released, guarding otherwise; a recent
        // stagger escalates the next push.
        private readonly Dictionary<Agent, Stagger> _staggers = new Dictionary<Agent, Stagger>();
        private struct Stagger { public float Until; public PushEffect Effect; }
        public PushEffect EvaluatePush(EnemyBody body, BodyPoint point, Vec3 velocity, PushMaterial material)
        {
            Agent agent = body.Agent;
            if (agent == null || !agent.IsActive()) return PushEffect.None;
            Entry held = Find(agent);
            if (held != null && held.Active.Phase == ActiveRagdollPhase.Destabilized) return PushEffect.None;
            float now = Mission.CurrentTime;
            PushContext context;
            if (_staggers.TryGetValue(agent, out Stagger stagger) && stagger.Until > now)
                context = stagger.Effect == PushEffect.StaggerFull ? PushContext.StaggerFull : PushContext.Stagger;
            else
            {
                Agent.ActionStage stage = agent.GetCurrentActionStage(1);
                bool attacking = stage == Agent.ActionStage.AttackReady || stage == Agent.ActionStage.AttackQuickReady ||
                    stage == Agent.ActionStage.AttackRelease;
                if (attacking) context = agent.Velocity.z > 1.5f ? PushContext.AttackJump : PushContext.Attack;
                else context = agent.GetTargetAgent() == null ? PushContext.Idle : PushContext.Combat;
            }
            PushBodyZone zone = point.Part == BoneBodyPartType.Head || point.Part == BoneBodyPartType.Neck ||
                point.Part == BoneBodyPartType.Legs ? PushBodyZone.HeadOrLeg : PushBodyZone.Torso;
            int level = PushReaction.Level(material, velocity.Length);
            PushEffect effect = PushReaction.HitEffect(level, zone, context);
            if (effect == PushEffect.Destabilize && (!Available || !EnemyPhysicsSettings.ActiveRagdoll))
                effect = PushEffect.StaggerFull;
            if (effect != PushEffect.None && effect != PushEffect.Destabilize)
                _staggers[agent] = new Stagger { Until = now + .5f + PushReaction.Recoil(effect), Effect = effect };
            if (EnemyPhysicsSettings.Trace)
                VrLog.Info(string.Format(CultureInfo.InvariantCulture, "PUSH material={0} speed={1:F2} level={2} zone={3} context={4} effect={5}",
                    material, velocity.Length, level, zone, context, effect));
            return effect;
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
        private int _releasesThisTick;
        public override void OnMissionTick(float dt)
        {
            long started = Stopwatch.GetTimestamp();
            int collections = GC.CollectionCount(0);
            _releasesThisTick = 0;
            if ((EnemyPhysicsSettings.RuntimeProbe && !EnemyPhysicsSettings.WoundProbe &&
                (!EnemyPhysicsSettings.CadenceProbe || EnemyPhysicsSettings.CadenceRagdoll)) || EnemyPhysicsSettings.LivingRagdolls)
                NativeRagdollBridge.BindMissionTick(Mission);
            if ((EnemyPhysicsSettings.ActiveBalance || EnemyPhysicsSettings.ActiveRagdoll) && Mission.Mode == MissionMode.Battle)
                _clock.Ensure(Mission);
            if (EnemyPhysicsSettings.RuntimeProbe && !EnemyPhysicsSettings.WoundProbe &&
                (!EnemyPhysicsSettings.CadenceProbe || EnemyPhysicsSettings.CadenceRagdoll))
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
                    if (entry.Lease.OwnsPhysics && entry.NativeActive && EnemyPhysicsSettings.ActiveRagdoll)
                    {
                        entry.Active.Tick(entry.Agent, entry.Skeleton, Mission, _clock);
                        if (entry.Active.ImpactBone >= 0) RagdollCollision(entry);
                        if (entry.Active.WantsHold) entry.Lease.KeepHeld(Mission.CurrentTime);
                        if (entry == _probeEntry && !_probeFinished) ObserveActiveProbe(entry);
                        else if (!_probeFinished) ObserveProbeLoad(entry);
                        if (entry.Active.ReadyToRelease) ReleaseRecovered(entry);
                    }
                    else if (entry.Lease.OwnsPhysics && entry.NativeActive)
                    {
                        entry.Balance.Tick(entry.Agent, entry.Skeleton, Mission, dt, _clock);
                        if (entry == _probeEntry && !_probeFinished && EnemyPhysicsSettings.ActiveBalance)
                        {
                            _probeBalanceTicks = entry.Balance.AppliedTicks;
                            _probeBalanceSkipped = entry.Balance.SkippedTicks;
                            if (_probeBalanceStepsAt < 0 && entry.Balance.AppliedTicks > 0) _probeBalanceStepsAt = _clock.Steps;
                            if (entry.Balance.Phase == BodyBalancePhase.Supporting || entry.Balance.Phase == BodyBalancePhase.Falling)
                                _probeBalanceStepsEnd = _clock.Steps;
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
            if (_probeEntry != null && !_probeFinished && _probeEntry.Active.Configured)
            {
                bool activation = false;
                foreach (Entry entry in _entries) activation |= entry.Lease.Occupied && entry.Active.ConfiguredThisTick;
                if (activation) _probeCpuActivationMax = Math.Max(_probeCpuActivationMax, LastTickCpuMilliseconds);
                else
                {
                    _probeCpuTotal += LastTickCpuMilliseconds; _probeCpuTicks++;
                    if (LastTickCpuMilliseconds > _probeCpuMax && LastTickCpuMilliseconds > 1)
                    {
                        int occupied = 0, phases = 0;
                        foreach (Entry entry in _entries)
                        {
                            if (!entry.Lease.Occupied) continue;
                            occupied++;
                            phases |= 1 << (int)entry.Active.Phase;
                        }
                        VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                            "ACTIVE_PROBE spike ms={0:F3} time={1:F3} leases={2} phases_mask={3} releases_this_tick={4} gc0_during_tick={5}",
                            LastTickCpuMilliseconds, Mission.CurrentTime, occupied, phases, _releasesThisTick, GC.CollectionCount(0) - collections));
                    }
                    _probeCpuMax = Math.Max(_probeCpuMax, LastTickCpuMilliseconds);
                }
            }
        }
        // B&S ends the get-up by handing the body back to the animator where it
        // stands. Bannerlord's animation restarts at the agent position, so the
        // agent is first moved under the hips when the body travelled.
        private void ReleaseRecovered(Entry entry)
        {
            bool moved = entry.Active.Moved;
            Vec3 position = entry.Active.RecoveryPosition;
            bool teleported = false;
            float distance = (position - entry.Agent.Position).Length;
            bool navmesh = InteractionMath.IsFinite(position) && OnNavmesh(position);
            // Agent.CanTeleport only reflects the deployment-phase order gate;
            // SetPosition itself is valid for a dismounted agent in battle.
            // Only onto a walkable navmesh face, so a body that slid down a
            // slope is never placed inside geometry.
            if (moved && entry.Agent.MountAgent == null && InteractionMath.IsFinite(position) &&
                distance < 30 && navmesh)
            { entry.Agent.TeleportToPosition(position); teleported = true; }
            VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                "ACTIVE_RAGDOLL release time={0:F3} pose_error={1:F3} moved={2} teleported={3} distance_m={4:F2} navmesh={5}",
                Mission.CurrentTime, entry.Active.LastPoseError, moved, teleported, distance, navmesh));
            if (entry == _probeEntry) { _probeActiveReleased = true; _probeTeleported = teleported || !moved; _probeReleaseError = entry.Active.LastPoseError; }
            _releasesThisTick++;
            SafeRelease(entry);
        }
        private void RagdollCollision(Entry entry)
        {
            ActiveRagdollBody body = entry.Active;
            float damage = RagdollImpact.Damage(body.ImpactSpeed);
            sbyte bone = body.ImpactBone;
            BoneBodyPartType part = entry.Agent.AgentVisuals.GetBoneTypeData(bone).BodyPartType;
            Agent victim = entry.Agent;
            float lost = PhysicalHit.ApplyRagdollImpact(body.LastPusher, victim, bone, part, body.ImpactPoint, damage);
            if (EnemyPhysicsSettings.Trace)
                VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                    "RAGDOLL_IMPACT agent={0} bone={1} part={2} speed={3:F2} damage={4:F2} health_lost={5:F2} time={6:F3}",
                    victim.Index, bone, part, body.ImpactSpeed, damage, lost, Mission.CurrentTime));
            if (entry == _probeEntry) { _probeImpacts++; _probeImpactLoss += lost; }
        }
        private int _probeImpacts;
        private float _probeImpactLoss;
        private bool OnNavmesh(Vec3 position)
        {
            var face = new PathFaceRecord(-1, -1, -1);
            Mission.Scene.GetNavMeshFaceIndex(ref face, position, true);
            return face.IsValid();
        }
        private bool _probePushed, _probeActiveReleased, _probeTeleported, _probeSawDestabilized, _probeSawStandUp, _probeSawRecovered;
        private float _probeReleaseError, _probeStandingError, _probeMinPelvis = float.MaxValue;
        private int _probeActiveSteps, _probeCpuTicks;
        private double _probeCpuTotal, _probeCpuMax, _probeCpuActivationMax;
        // Extra stationary enemies that run the same push cycle for the CPU
        // measurement; only the main probe entry is judged.
        private readonly HashSet<Entry> _probeLoad = new HashSet<Entry>();
        private readonly HashSet<Entry> _probeLoadPushed = new HashSet<Entry>();
        private void StartProbeLoad(Agent main)
        {
            int wanted = Math.Min(EnemyPhysicsSettings.ActiveProbeBodies, _entries.Length) - 1;
            foreach (Agent agent in Mission.Agents)
            {
                if (wanted <= 0) break;
                if (agent == main || !EnemyBody.Eligible(agent, Mission.MainAgent) || !agent.IsActive() || agent.IsPaused ||
                    !agent.IsAIControlled || agent.Velocity.Length > 3 || (agent.Position - Mission.MainAgent.Position).Length < 20) continue;
                Skeleton skeleton = agent.AgentVisuals?.GetSkeleton();
                if (skeleton == null || skeleton.GetCurrentRagdollState() != RagdollState.Disabled) continue;
                foreach (Entry entry in _entries)
                {
                    if (entry.Lease.Occupied) continue;
                    entry.Agent = agent; entry.Skeleton = skeleton; entry.Mission = Mission;
                    if (!NativeRagdollBridge.Preflight(agent, Mission, skeleton) || !entry.Lease.Begin(entry, Mission.CurrentTime, LeaseSeconds))
                    { entry.Clear(); break; }
                    entry.Lease.KeepHeld(Mission.CurrentTime, .6f);
                    _probeLoad.Add(entry); wanted--;
                    break;
                }
            }
            VrLog.Info("ACTIVE_PROBE load bodies=" + (_probeLoad.Count + 1));
        }
        private void ObserveProbeLoad(Entry entry)
        {
            if (!_probeLoad.Contains(entry) || _probeLoadPushed.Contains(entry) || !entry.Active.Configured ||
                entry.Active.Phase != ActiveRagdollPhase.Standing || Mission.CurrentTime - _probeBegan < 1.5f) return;
            _probeLoadPushed.Add(entry);
            Vec3 back = -entry.Agent.LookDirection; back.z = 0;
            if (back.LengthSquared < 1e-4f) back = Vec3.Forward;
            entry.Active.Destabilize();
            entry.Active.QueuePush(entry.Agent.Monster.SpineUpperBoneIndex, back.NormalizedCopy() * (7 * 2f * 90));
        }
        private void ObserveActiveProbe(Entry entry)
        {
            ActiveRagdollBody body = entry.Active;
            _probeActiveSteps = body.AppliedSteps;
            _probeSawDestabilized |= body.Phase == ActiveRagdollPhase.Destabilized;
            _probeSawStandUp |= body.Phase == ActiveRagdollPhase.StandingUp;
            _probeSawRecovered |= body.Phase == ActiveRagdollPhase.Recovered;
            if (body.Configured) _probeMinPelvis = Math.Min(_probeMinPelvis, ProbePelvis(entry).z - _probeOrigin.z);
            if (EnemyPhysicsSettings.ActiveProbeGrab) { ProbeGrab(entry); return; }
            if (!_probePushed && body.Configured && body.Phase == ActiveRagdollPhase.Standing &&
                Mission.CurrentTime - _probeBegan > 1.5f)
            {
                _probePushed = true;
                _probeStandingError = body.MaxStandingError;
                // A level 2 punch (B&S DamageModifier_Punch: above 6 m/s) from
                // the agent's front, which destabilizes an idle human.
                Vec3 back = -entry.Agent.LookDirection; back.z = 0;
                if (back.LengthSquared < 1e-4f) back = Vec3.Forward;
                back = back.NormalizedCopy() * 7;
                VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                    "ACTIVE_PROBE push time={0:F3} standing_max_error_m={1:F3} effect={2}",
                    Mission.CurrentTime, _probeStandingError,
                    PushReaction.HitEffect(PushReaction.Level(PushMaterial.Punch, back.Length), PushBodyZone.Torso, PushContext.Idle)));
                entry.Active.Destabilize();
                entry.Active.QueuePush(entry.Agent.Monster.SpineUpperBoneIndex, back * (2f * 90));
            }
        }
        // A virtual hand closes on the neck after 1 s of standing, then rises
        // 40 cm over one second. Interactable_HumanHeadNeck lifts the root by
        // more than 5 cm and must make the creature fall (liftBehaviour Fall).
        private Vec3 _probeHand;
        private bool _probeGrabbing, _probeGrabOutcome;
        private void ProbeGrab(Entry entry)
        {
            ActiveRagdollBody body = entry.Active;
            if (!body.Configured) return;
            float t = Mission.CurrentTime - _probeBegan;
            sbyte neck = -1;
            for (sbyte b = 0; b < entry.Skeleton.GetBoneCount() && neck < 0; b++)
                if ((entry.Skeleton.GetBoneName(b) ?? "").Contains("neck")) neck = b;
            if (neck < 0) return;
            MatrixFrame frame = entry.Agent.AgentVisuals.GetEntity().GetGlobalFrame();
            MatrixFrame boneWorld = frame.TransformToParent(entry.Skeleton.GetBoneEntitialFrameWithIndex(neck));
            if (!_probeGrabbing && !_probePushed && t > 1)
            {
                _probeGrabbing = _probePushed = true;
                _probeStandingError = body.MaxStandingError;
                _probeHand = boneWorld.origin;
                VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                    "ACTIVE_PROBE grab neck time={0:F3} standing_max_error_m={1:F3}", Mission.CurrentTime, _probeStandingError));
            }
            if (!_probeGrabbing) return;
            Vec3 hand = _probeHand + Vec3.Up * Math.Min(.4f, Math.Max(0, t - 1) * .4f);
            if (body.LastGrabOutcome != GrabOutcome.Hold) _probeGrabOutcome = true;
            if (body.Phase == ActiveRagdollPhase.Destabilized || t > 3)
            {
                _probeGrabbing = false;
                body.ReleaseHandle(0);
                VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                    "ACTIVE_PROBE grab release time={0:F3} outcome={1} phase={2}", Mission.CurrentTime, body.LastGrabOutcome, body.Phase));
                return;
            }
            body.Handle(0, neck, GrabHandle.Neck, hand, boneWorld, boneWorld.origin);
        }
        private Vec3 ProbePelvis(Entry entry) => entry.Agent.AgentVisuals.GetEntity().GetGlobalFrame()
            .TransformToParent(entry.Skeleton.GetBoneEntitialFrameWithIndex(entry.Agent.Monster.PelvisBoneIndex)).origin;
        private void StartProbe()
        {
            if (_probeStarted || Mission.Mode != MissionMode.Battle || Mission.MainAgent == null) return;
            if (_probeBattleBegan < 0) _probeBattleBegan = Mission.CurrentTime;
            float elapsed = Mission.CurrentTime - _probeBattleBegan;
            if (elapsed < (EnemyPhysicsSettings.ActiveBalance || EnemyPhysicsSettings.ActiveRagdoll ? .05f : 3)) return;
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
                if ((EnemyPhysicsSettings.ActiveBalance || EnemyPhysicsSettings.ActiveRagdoll) && agent.Velocity.Length > .3f) continue;
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
                    if (!entry.Lease.Begin(entry, Mission.CurrentTime, LeaseSeconds))
                    {
                        _probeFinished = true; entry.Clear();
                        VrLog.Info("PHYSICS_PROBE result=INCONCLUSIVE reason=activation_rejected");
                    }
                    else
                    {
                        entry.Lease.KeepHeld(Mission.CurrentTime, .6f);
                        _probeBalanceCaptured = entry.Balance.Captured;
                        // Only once the main lease owns its slot.
                        if (EnemyPhysicsSettings.ActiveRagdoll && EnemyPhysicsSettings.ActiveProbeBodies > 1) StartProbeLoad(agent);
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
                _probeMaxRise = Math.Max(_probeMaxRise, ProbePelvis(entry).z - _probeOrigin.z);
            }
        }
        private void FinishProbe(Entry entry)
        {
            _probeFinished = true;
            bool alive = entry.Alive;
            bool disabled = alive && entry.NativeDisabled;
            bool paused = alive && entry.Agent.IsPaused;
            // B&S ragdoll collisions may legitimately cost a few points on the fall.
            bool sameHealth = alive && Math.Abs(entry.Agent.Health - (_probeHealth - _probeImpactLoss)) < .01f;
            bool passed = _probeSawActive && disabled && !paused && sameHealth && !entry.Lease.Faulted &&
                (_probeMaxDisplacement > .02f || (EnemyPhysicsSettings.ActiveBalance && _probeBalanceTicks >= 10));
            VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                "PHYSICS_PROBE result={0} active_seen={1} native_disabled={2} alive={3} paused={4} same_health={5} max_pelvis_displacement_m={6:F3}",
                passed ? "PASS" : "FAIL", _probeSawActive, disabled, alive, paused, sameHealth, _probeMaxDisplacement));
            if (EnemyPhysicsSettings.ActiveRagdoll)
                VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                    "ACTIVE_PROBE result={0} standing_max_error_m={1:F3} pushed={2} destabilized={3} stood_up={4} recovered={5} released_by_cycle={6} release_pose_error_m={7:F3} teleported={8} lowest_pelvis_m={9:F3} force_steps={10} alive={11} same_health={12} unpaused={13} native_disabled={14} cpu_ms_mean={15:F4} cpu_ms_max_steady={16:F4} cpu_ms_activation_tick={18:F4} agents={17} impacts={19} impact_health_lost={20:F1}",
                    passed && _probePushed && (!EnemyPhysicsSettings.ActiveProbeGrab || _probeGrabOutcome) && _probeSawDestabilized && _probeSawStandUp && _probeSawRecovered && _probeActiveReleased && _probeTeleported &&
                        _probeStandingError < .15f && _probeMinPelvis < -.4f ? "PASS" : "FAIL",
                    _probeStandingError, _probePushed, _probeSawDestabilized, _probeSawStandUp, _probeSawRecovered,
                    _probeActiveReleased, _probeReleaseError, _probeTeleported, _probeMinPelvis, _probeActiveSteps,
                    alive, sameHealth, !paused, disabled, _probeCpuTicks == 0 ? 0 : _probeCpuTotal / _probeCpuTicks, _probeCpuMax,
                    Mission.Agents.Count, _probeCpuActivationMax, _probeImpacts, _probeImpactLoss));
            else if (EnemyPhysicsSettings.ActiveBalance)
                VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                    "BALANCE_PROBE result={0} captured={1} force_ticks={2} support_lost={3} max_drop_m={4:F3} peak_force={5:F1} skipped_ticks={6} physx_steps_while_supported={7} max_rise_m={8:F3}",
                    passed && _probeBalanceCaptured && _probeBalanceTicks >= 10 && !_probeBalanceFell && _probeMaxDrop < .25f && _probeMaxRise < .1f ? "PASS" : "FAIL",
                    _probeBalanceCaptured, _probeBalanceTicks, _probeBalanceFell, _probeMaxDrop, _probePeakSupport,
                    _probeBalanceSkipped, _probeBalanceStepsAt < 0 ? 0 : _probeBalanceStepsEnd - _probeBalanceStepsAt, _probeMaxRise));
            if (alive)
            {
                _probeAgent = entry.Agent; _probeSkeleton = entry.Skeleton;
                _probeHealth = entry.Agent.Health; // follow-up watches for later loss only
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
            try { _clock.Shutdown(Mission); }
            catch (Exception ex) { VrLog.Error("PhysX step clock shutdown failed.", ex); }
        }
    }
}
