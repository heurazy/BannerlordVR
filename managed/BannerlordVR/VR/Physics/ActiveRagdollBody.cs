using System;
using System.Globalization;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR.Physics
{
    // Bannerlord adapter for the Blade & Sorcery pin drive. One physical body
    // per ragdoll body (its first mapped bone); targets are the entity-local
    // animated bone frames captured just before the native ragdoll starts,
    // placed in a root frame that the stand-up re-anchors under the hips.
    internal sealed class ActiveRagdollBody
    {
        private const int MaxBodies = 32;
        private readonly RagdollPinDrive _drive = new RagdollPinDrive();
        private readonly ActiveRagdollCycle _cycle = new ActiveRagdollCycle();
        // Bone hierarchies per skeleton layout. Walking parents through the
        // engine cost one native call per step, thousands per activation.
        private static readonly System.Collections.Generic.Dictionary<Monster, sbyte[]> Parents =
            new System.Collections.Generic.Dictionary<Monster, sbyte[]>();
        private sbyte[] _parentOf = new sbyte[0];
        private readonly PhysicsStepGate _gate = new PhysicsStepGate();
        private readonly MatrixFrame[] _captured = new MatrixFrame[128];
        private readonly sbyte[] _bone = new sbyte[MaxBodies];
        private readonly Vec3[] _position = new Vec3[MaxBodies], _target = new Vec3[MaxBodies], _force = new Vec3[MaxBodies];
        private MatrixFrame _root, _capturedRoot;
        private int _boneCount, _pelvisBody;
        private bool _captureValid, _configured, _destabilizeOnStart;
        private float _lastApplied = -1, _lastRootSample = -1, _nextGround;
        private bool _nearGround;
        private Vec3 _lastPelvis, _groundPoint;
        private Vec3 _pendingImpulse; private sbyte _pendingBone = -1;
        // Ragdoll.physicEnabledDuration: a standing body untouched for 2 s
        // returns to animation.
        public const float PhysicEnabledDuration = 2;
        private float _lastInteraction;
        private int[] _bodyOfBone = new int[0];
        private int _headBody = -1;
        private struct GrabSlot { public bool Active; public sbyte Bone; public GrabHandle Kind; public Vec3 Local; }
        private readonly GrabSlot[] _grabs = new GrabSlot[RagdollPinDrive.MaxHandles];
        private readonly bool[] _ungrab = new bool[RagdollPinDrive.MaxHandles];
        private readonly Vec3[] _hand = new Vec3[RagdollPinDrive.MaxHandles];
        public GrabOutcome LastGrabOutcome { get; private set; }
        public bool Configured => _configured;
        // True on the tick that bound the bodies (native mass read, tree build).
        public bool ConfiguredThisTick { get; private set; }
        public double NativeMassMilliseconds { get; private set; }
        public ActiveRagdollPhase Phase => _cycle.Phase;
        public FallState Fall => _cycle.Fall;
        public int AppliedSteps { get; private set; }
        public int SkippedTicks { get; private set; }
        public float MaxStandingError { get; private set; }
        public float LastPoseError { get; private set; }
        public int Bodies => _drive.Count;
        public float RootSpeed { get; private set; }
        public bool Moved => _configured && (_root.origin - _capturedRoot.origin).AsVec2.Length > .3f;
        public Vec3 RecoveryPosition => _root.origin;

        public void Clear()
        {
            _cycle.Stop(); _gate.Reset(); _drive.ResetVelocity();
            _captureValid = _configured = _destabilizeOnStart = false;
            _lastApplied = _lastRootSample = -1; _nextGround = 0; _nearGround = false;
            AppliedSteps = SkippedTicks = 0; MaxStandingError = LastPoseError = RootSpeed = 0;
            _pendingBone = -1; _pendingImpulse = Vec3.Zero;
            _lastInteraction = 0; _headBody = -1; LastGrabOutcome = GrabOutcome.Hold;
            for (int h = 0; h < _grabs.Length; h++) { _grabs[h] = default(GrabSlot); _ungrab[h] = false; }
        }

        // Before StartRagdollAsCorpse, while the skeleton is still animated.
        public bool Capture(Agent agent, Skeleton skeleton, bool destabilized)
        {
            Clear();
            _boneCount = skeleton.GetBoneCount();
            if (_boneCount < 2 || _boneCount > _captured.Length) return false;
            for (sbyte b = 0; b < _boneCount; b++)
            {
                _captured[b] = skeleton.GetBoneEntitialFrameWithIndex(b);
                if (!InteractionMath.IsFinite(_captured[b].origin)) return false;
            }
            _capturedRoot = _root = agent.AgentVisuals.GetEntity().GetGlobalFrame();
            _destabilizeOnStart = destabilized;
            _lastInteraction = agent.Mission?.CurrentTime ?? 0;
            _captureValid = InteractionMath.IsFinite(_root.origin);
            return _captureValid;
        }

        // First tick with an active native ragdoll: bind bodies and their tree.
        private bool Configure(Agent agent, Mission mission, Skeleton skeleton)
        {
            var boneMass = new float[_boneCount];
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            bool read = NativeRagdollBridge.ReadBoneMasses(agent, mission, skeleton, boneMass);
            NativeMassMilliseconds = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (!read) return false;
            var bodyOfBone = new int[_boneCount];
            int count = 0;
            for (int b = 0; b < _boneCount; b++)
            {
                bodyOfBone[b] = -1;
                if (boneMass[b] <= 0) continue;
                if (count >= MaxBodies) return false;
                bodyOfBone[b] = count; _bone[count] = (sbyte)b; count++;
            }
            if (count < 2) return false;
            _parentOf = ParentsOf(agent.Monster, skeleton);
            var mass = new float[count];
            var parent = new int[count];
            int pelvis = agent.Monster.PelvisBoneIndex;
            _pelvisBody = -1;
            for (int i = 0; i < count; i++)
            {
                mass[i] = boneMass[_bone[i]];
                parent[i] = -1;
                for (sbyte p = _parentOf[_bone[i]]; p >= 0; p = _parentOf[p])
                    if (bodyOfBone[p] >= 0) { parent[i] = bodyOfBone[p]; break; }
                if (_bone[i] == pelvis) _pelvisBody = i;
            }
            _bodyOfBone = new int[_boneCount];
            for (int b = 0; b < _boneCount; b++)
            {
                _bodyOfBone[b] = bodyOfBone[b];
                for (sbyte p = _parentOf[b]; _bodyOfBone[b] < 0 && p >= 0; p = _parentOf[p])
                    _bodyOfBone[b] = bodyOfBone[p];
                if (_bodyOfBone[b] < 0) _bodyOfBone[b] = 0;
            }
            sbyte head = agent.Monster.HeadLookDirectionBoneIndex;
            _headBody = head >= 0 && head < _boneCount ? _bodyOfBone[head] : -1;
            // Several unparented bodies would be separate trees: hang them on
            // the first one so each still has a joint pin.
            for (int i = 1; i < count; i++) if (parent[i] < 0) parent[i] = 0;
            // The root body carries the hips when the pelvis bone is merged into it.
            if (_pelvisBody < 0) _pelvisBody = 0;
            if (!_drive.Configure(mass, parent))
            {
                VrLog.Info("ACTIVE_RAGDOLL refused=body_tree; bodies=" + count);
                return false;
            }
            _cycle.Begin(_destabilizeOnStart);
            _configured = true;
            // Same order of magnitude as B&S Rigidbody.maxDepenetration and
            // part velocity caps: a solver spike cannot launch a soldier.
            agent.SetVelocityLimitsOnRagdoll(20, 40);
            VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                "ACTIVE_RAGDOLL configured bodies={0} total_mass={1:F1} pelvis_body_bone={2} start={3} native_mass_ms={4:F3}",
                count, _drive.TotalMass, _bone[_pelvisBody], _cycle.Phase, NativeMassMilliseconds));
            return true;
        }

        private sbyte[] ParentsOf(Monster monster, Skeleton skeleton)
        {
            if (monster != null && Parents.TryGetValue(monster, out sbyte[] cached) && cached.Length == _boneCount) return cached;
            var parents = new sbyte[_boneCount];
            for (sbyte b = 0; b < _boneCount; b++)
            {
                sbyte p = skeleton.GetParentBoneIndex(b);
                // A malformed hierarchy must not loop: parents precede children.
                parents[b] = p < b ? p : (sbyte)-1;
            }
            if (monster != null) Parents[monster] = parents;
            return parents;
        }

        public void Destabilize()
        {
            _lastInteraction = Math.Max(_lastInteraction, _lastRootSample);
            if (_configured) _cycle.Destabilize(); else _destabilizeOnStart = true;
        }

        // HandleRagdoll.OnGrab: the contact point is anchored in its bone and
        // pulled to the player's grip. Returns false once the handle let go.
        public bool Handle(int hand, sbyte bone, GrabHandle kind, Vec3 handWorld, MatrixFrame boneWorld, Vec3 contactWorld)
        {
            if (hand < 0 || hand >= _grabs.Length || !InteractionMath.IsFinite(handWorld)) return false;
            if (_ungrab[hand]) return false;
            if (!_grabs[hand].Active || _grabs[hand].Bone != bone)
                _grabs[hand] = new GrabSlot { Active = true, Bone = bone, Kind = kind,
                    Local = boneWorld.TransformToLocalNonOrthogonal(contactWorld) };
            _hand[hand] = handWorld;
            return true;
        }
        public void ReleaseHandle(int hand)
        {
            if (hand < 0 || hand >= _grabs.Length) return;
            _grabs[hand] = default(GrabSlot); _ungrab[hand] = false;
            _drive.ClearHandle(hand);
        }
        public bool Grabbed => _grabs[0].Active || _grabs[1].Active;
        public float PartMass(sbyte bone) =>
            _configured && bone >= 0 && bone < _bodyOfBone.Length ? _drive.Mass(_bodyOfBone[bone]) : 5;

        // A one-step push on a body, e.g. the hit that destabilized it. It is
        // queued so that it shares the PhysX step gate with the drive.
        public void QueuePush(sbyte bone, Vec3 force)
        {
            if (!InteractionMath.IsFinite(force)) return;
            _pendingBone = bone; _pendingImpulse = InteractionMath.ClampMagnitude(force, 4000);
            _lastInteraction = Math.Max(_lastInteraction, _lastRootSample);
        }

        // At most one activation per mission tick (native mass read included).
        private static float _configureTick = -1;
        public void Tick(Agent agent, Skeleton skeleton, Mission mission, PhysicsStepCounter clock)
        {
            if (!_captureValid) return;
            ConfiguredThisTick = false;
            if (!_configured && _configureTick == mission.CurrentTime) return;
            if (!_configured)
            {
                _configureTick = mission.CurrentTime;
                if (!Configure(agent, mission, skeleton)) { _captureValid = false; return; }
                ConfiguredThisTick = true;
            }
            float now = mission.CurrentTime;
            MatrixFrame frame = agent.AgentVisuals.GetEntity().GetGlobalFrame();
            for (int i = 0; i < _drive.Count; i++)
                _position[i] = frame.TransformToParent(skeleton.GetBoneEntitialFrameWithIndex(_bone[i])).origin;
            Vec3 pelvis = _position[_pelvisBody];
            float sampleDt = _lastRootSample < 0 ? 0 : now - _lastRootSample;
            if (sampleDt > 0) RootSpeed = (pelvis - _lastPelvis).Length / sampleDt;
            _lastPelvis = pelvis; _lastRootSample = now;
            if (now >= _nextGround)
            {
                // Creature.SphereCastGround within hips height, from the root.
                _nearGround = mission.Scene.RayCastForClosestEntityOrTerrainFixedPhysics(pelvis + Vec3.Up * .1f,
                    pelvis - Vec3.Up * 1.1f, out float _, out Vec3 hit, .05f) && InteractionMath.IsFinite(hit);
                if (_nearGround) _groundPoint = hit;
                _nextGround = now + .1f;
            }
            UpdateHandles(skeleton, frame, now);
            ActiveRagdollPhase before = _cycle.Phase;
            _cycle.Tick(sampleDt, _nearGround, RootSpeed);
            if (before == ActiveRagdollPhase.Destabilized && _cycle.Phase == ActiveRagdollPhase.StandingUp) ReanchorUnderHips();
            if (_cycle.Phase != before)
                VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                    "ACTIVE_RAGDOLL phase={0} fall={1} time={2:F3} root_speed={3:F2} pose_error={4:F3}",
                    _cycle.Phase, _cycle.Fall, now, RootSpeed, LastPoseError));
            for (int i = 0; i < _drive.Count; i++) _target[i] = _root.TransformToParent(_captured[_bone[i]]).origin;
            float error = 0;
            for (int i = 0; i < _drive.Count; i++) error = Math.Max(error, (_position[i] - _target[i]).Length);
            LastPoseError = error;
            if (_cycle.Phase == ActiveRagdollPhase.Standing) MaxStandingError = Math.Max(MaxStandingError, error);
            if (clock == null || !clock.Running || !_gate.CanApply(clock.Steps)) { SkippedTicks++; return; }
            float dt = _lastApplied < 0 ? 1f / 90 : now - _lastApplied;
            _drive.State = _cycle.DriveState;
            _drive.Multiplier = _cycle.PinMultiplier;
            _drive.CancelGravity = _cycle.CancelGravity;
            _drive.Grounded = _nearGround;
            if (!_drive.Tick(_position, _target, Math.Min(dt, .1f), _force)) { _lastApplied = now; return; }
            for (int i = 0; i < _drive.Count; i++)
            {
                if (_pendingBone == _bone[i]) { _force[i] += _pendingImpulse; _pendingBone = -1; }
                if (_force[i].LengthSquared > 0) agent.ApplyForceOnRagdoll(_bone[i], in _force[i]);
            }
            if (_pendingBone >= 0)
            {
                agent.ApplyForceOnRagdoll(_pendingBone, in _pendingImpulse);
                _pendingBone = -1;
            }
            _gate.Applied(clock.Steps);
            _lastApplied = now;
            AppliedSteps++;
        }

        private void UpdateHandles(Skeleton skeleton, MatrixFrame frame, float now)
        {
            for (int h = 0; h < _grabs.Length; h++)
            {
                if (!_grabs[h].Active) { _drive.ClearHandle(h); continue; }
                sbyte bone = _grabs[h].Bone;
                if (bone < 0 || bone >= _boneCount) { ReleaseHandle(h); _ungrab[h] = true; continue; }
                int body = _bodyOfBone[bone];
                MatrixFrame boneWorld = frame.TransformToParent(skeleton.GetBoneEntitialFrameWithIndex(bone));
                Vec3 contact = boneWorld.TransformToParent(_grabs[h].Local);
                _drive.SetHandle(h, body, _position[body] + (_hand[h] - contact));
                _lastInteraction = now;
                // HandleRagdoll lift and head rules apply while the creature is
                // alive and standing; a destabilized body simply hangs.
                if (_cycle.Phase != ActiveRagdollPhase.Standing && _cycle.Phase != ActiveRagdollPhase.StandingUp) continue;
                GrabHandle kind = _grabs[h].Kind;
                int reference = RagdollGrabRules.LiftsSelf(kind) ? body : _pelvisBody;
                float groundZ = _root.origin.z;
                float referenceHeight = _position[reference].z - groundZ;
                float referenceOriginal = _captured[_bone[reference]].origin.z;
                float headHeight = _headBody >= 0 ? _position[_headBody].z - groundZ : float.MaxValue;
                float rootOriginal = _captured[_bone[_pelvisBody]].origin.z;
                GrabOutcome outcome = RagdollGrabRules.Evaluate(kind, referenceHeight, referenceOriginal, headHeight, rootOriginal);
                if (outcome == GrabOutcome.Hold) continue;
                LastGrabOutcome = outcome;
                VrLog.Info(string.Format(CultureInfo.InvariantCulture,
                    "ACTIVE_RAGDOLL grab outcome={0} handle={1} reference_height={2:F3} original={3:F3} head={4:F3} time={5:F3}",
                    outcome, kind, referenceHeight, referenceOriginal, headHeight, now));
                if (outcome == GrabOutcome.Destabilize) _cycle.Destabilize();
                else { ReleaseHandle(h); _ungrab[h] = true; }
            }
        }

        // Ragdoll.SetBodyPositionToHips: the stand-up targets the pose placed
        // under the current hips, on the measured ground, original facing.
        private void ReanchorUnderHips()
        {
            int pelvisBone = _bone[_pelvisBody];
            Vec3 local = _capturedRoot.rotation.TransformToParent(_captured[pelvisBone].origin);
            Vec3 pelvis = _lastPelvis;
            Vec3 origin = new Vec3(pelvis.x - local.x, pelvis.y - local.y, _nearGround ? _groundPoint.z : _root.origin.z);
            if (InteractionMath.IsFinite(origin)) _root.origin = origin;
        }

        // Ready to hand the body back to animation without a visible snap.
        public bool ReadyToRelease => _configured && LastPoseError < .25f && !Grabbed &&
            (_cycle.Phase == ActiveRagdollPhase.Recovered ||
             (_cycle.Phase == ActiveRagdollPhase.Standing && _lastRootSample - _lastInteraction > PhysicEnabledDuration));
        public bool WantsHold => _configured && _cycle.Phase != ActiveRagdollPhase.Off && _cycle.Phase != ActiveRagdollPhase.Recovered;
    }
}
