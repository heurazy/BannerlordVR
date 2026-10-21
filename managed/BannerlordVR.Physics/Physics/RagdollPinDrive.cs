using System;
using TaleWorlds.Library;

namespace BannerlordVR.VR.Physics
{
    // Blade & Sorcery ragdoll states that keep physics on (Ragdoll.State).
    public enum PinState { Inert, Destabilized, Standing }

    // Port of the Blade & Sorcery creature drive. There, every ragdoll part is
    // a rigidbody joined to a kinematic anchor that follows its animated bone:
    // a position drive (spring 1000, damper 50, max 1000) and a slerp rotation
    // drive (800, 50, 100). Standing pins position and rotation and disables
    // gravity on the parts; Destabilized drops position pins, restores gravity
    // and keeps rotation pins at 0.5x spring / 0.1x damper except on the root
    // (0.2x spring once on the ground); Inert has no pins at all.
    //
    // Bannerlord exposes neither joint drives nor torque on ragdoll bodies,
    // only a continuous force per body. Position drives map directly to a
    // force towards the target. A rotation drive is reproduced by an equal
    // and opposite force pair between a body and its parent body that restores
    // their animated relative offset: the joint angle error times the lever
    // arm, with torque k*theta becoming force (k / L^2) * offset error.
    public sealed class RagdollPinDrive
    {
        public const float SpringPosition = 1000, DamperPosition = 50, MaxPosition = 1000;
        public const float SpringRotation = 800, DamperRotation = 50, MaxRotation = 100;
        public const float DestabilizedSpringRotation = .5f, DestabilizedDamperRotation = .1f;
        public const float DestabilizedGroundSpringRotation = .2f;
        public const float Gravity = 9.81f;
        // PhysX integrates Unity's drives implicitly. Here forces are explicit
        // and arrive one PhysX step late, so very light bodies would oscillate
        // under the raw spring. Cap each body's natural frequency instead.
        public const float MaxNaturalFrequency = 18, MaxDampingRate = 30;
        private const float MinLever = .05f;
        // Ragdoll.playerArmPositionSpring / Damper / MaxPositionForce: the joint
        // between the player's grip and a grabbed part is the player's arm.
        // A hand that holds something switches to the creature's grip force
        // (Creature_PlayerDefault*.json: gripForcePositionSpringDamperMult
        // 0.5, gripForceMaxPosition 2000 N, PlayerLink.SetJointConfig): a held
        // body or a planted blade pulls with that, not the free hand's 3000 N.
        public const float GripMultiplier = .5f;
        public const float HandSpring = 5000 * GripMultiplier, HandDamper = 40 * GripMultiplier, HandMaxForce = 2000;
        public const int MaxHandles = 2;
        // The grip joint acts alone on its body once the pins follow the hand
        // (see ActiveRagdollBody.ReachTargets): omega*h = 0.31, c*h = 0.5.
        public const float HandNaturalFrequency = 28, HandDampingRate = 45;
        private readonly int[] _handleBody = { -1, -1 };
        private readonly Vec3[] _handleTarget = new Vec3[MaxHandles], _lastHandleTarget = new Vec3[MaxHandles];
        private readonly bool[] _handleSampled = new bool[MaxHandles];
        // Damager.Penetrate joint: x and y Locked, z (along the blade) free
        // with its own damper. Zero means a grip, locked on every axis.
        private readonly Vec3[] _handleAxis = new Vec3[MaxHandles];

        private float[] _mass = new float[0];
        private int[] _parent = new int[0];
        private Vec3[] _lastPosition = new Vec3[0], _lastTarget = new Vec3[0];
        private bool _sampled;
        public int Count { get; private set; }
        public PinState State { get; set; } = PinState.Inert;
        // Blade & Sorcery SetPinForceMultiplier: scales spring, damper and caps.
        public float Multiplier { get; set; } = 1;
        // Standing removes gravity from the parts; the stand-up ramp keeps it.
        public bool CancelGravity { get; set; } = true;
        public bool Grounded { get; set; }
        public float TotalMass { get; private set; }

        // parent[i] < 0 marks the root body. Parents must precede children.
        public bool Configure(float[] mass, int[] parent)
        {
            Count = 0; TotalMass = 0; _sampled = false;
            if (mass == null || parent == null || mass.Length != parent.Length || mass.Length == 0 || mass.Length > 64) return false;
            float total = 0;
            int roots = 0;
            for (int i = 0; i < mass.Length; i++)
            {
                if (!InteractionMath.IsFinite(mass[i]) || mass[i] <= 0 || mass[i] > 200) return false;
                if (parent[i] >= i) return false;
                if (parent[i] < 0) roots++;
                total += mass[i];
            }
            if (roots != 1 || parent[0] >= 0 || total > 500) return false;
            _mass = (float[])mass.Clone(); _parent = (int[])parent.Clone();
            _lastPosition = new Vec3[mass.Length]; _lastTarget = new Vec3[mass.Length];
            Count = mass.Length; TotalMass = total;
            return true;
        }

        public float Mass(int body) => body >= 0 && body < Count ? _mass[body] : 0;
        public int Parent(int body) => body >= 0 && body < Count ? _parent[body] : -1;
        // True when ancestor is body itself or one of its parents.
        public bool InSubtree(int body, int ancestor)
        {
            for (int b = body; b >= 0; b = _parent[b]) if (b == ancestor) return true;
            return false;
        }
        public void ResetVelocity() { _sampled = false; for (int h = 0; h < MaxHandles; h++) _handleSampled[h] = false; }

        // Handle h (one per player hand) pulls body towards a world point.
        public bool SetHandle(int h, int body, Vec3 target) => SetHandle(h, body, target, Vec3.Zero);
        public bool SetHandle(int h, int body, Vec3 target, Vec3 freeAxis)
        {
            if (h < 0 || h >= MaxHandles || body < 0 || body >= Count || !InteractionMath.IsFinite(target)) return false;
            if (_handleBody[h] != body) _handleSampled[h] = false;
            _handleBody[h] = body; _handleTarget[h] = target;
            _handleAxis[h] = InteractionMath.IsFinite(freeAxis) && freeAxis.Length > .5f ? freeAxis.NormalizedCopy() : Vec3.Zero;
            return true;
        }
        public void ClearHandle(int h) { if (h >= 0 && h < MaxHandles) { _handleBody[h] = -1; _handleSampled[h] = false; } }
        public bool Handled(int body) => body >= 0 && (_handleBody[0] == body || _handleBody[1] == body);
        public bool AnyHandle => _handleBody[0] >= 0 || _handleBody[1] >= 0;

        // dt is the time since the previous call that actually reached PhysX.
        public bool Tick(Vec3[] position, Vec3[] target, float dt, Vec3[] force)
        {
            if (Count == 0 || position == null || target == null || force == null ||
                position.Length < Count || target.Length < Count || force.Length < Count) return false;
            for (int i = 0; i < Count; i++) force[i] = Vec3.Zero;
            if (!InteractionMath.IsFinite(dt) || dt <= 0 || dt > .1f) { _sampled = false; return false; }
            for (int i = 0; i < Count; i++)
                if (!InteractionMath.IsFinite(position[i]) || !InteractionMath.IsFinite(target[i])) { _sampled = false; return false; }
            float multiplier = InteractionMath.IsFinite(Multiplier) ? Math.Max(0, Math.Min(1, Multiplier)) : 0;
            bool sampled = _sampled;
            for (int i = 0; i < Count && State != PinState.Inert; i++)
            {
                Vec3 velocity = sampled ? (position[i] - _lastPosition[i]) * (1 / dt) : Vec3.Zero;
                Vec3 targetVelocity = sampled ? (target[i] - _lastTarget[i]) * (1 / dt) : Vec3.Zero;
                if (State == PinState.Standing)
                {
                    float k = Math.Min(SpringPosition * multiplier, _mass[i] * MaxNaturalFrequency * MaxNaturalFrequency);
                    float c = Math.Min(DamperPosition * multiplier, _mass[i] * MaxDampingRate);
                    Vec3 pin = (target[i] - position[i]) * k - (velocity - targetVelocity) * c;
                    force[i] += InteractionMath.ClampMagnitude(pin, MaxPosition * multiplier);
                    if (CancelGravity) force[i] += Vec3.Up * (_mass[i] * Gravity);
                }
                int p = _parent[i];
                // Destabilized keeps no rotation drive on the root (rootPart).
                if (p < 0) continue;
                float springScale = State == PinState.Standing ? 1 :
                    Grounded ? DestabilizedGroundSpringRotation : DestabilizedSpringRotation;
                float damperScale = State == PinState.Standing ? 1 : DestabilizedDamperRotation;
                Vec3 targetOffset = target[i] - target[p];
                float lever = targetOffset.Length;
                if (lever < MinLever) continue;
                Vec3 offset = position[i] - position[p];
                Vec3 parentVelocity = sampled ? (position[p] - _lastPosition[p]) * (1 / dt) : Vec3.Zero;
                Vec3 parentTargetVelocity = sampled ? (target[p] - _lastTarget[p]) * (1 / dt) : Vec3.Zero;
                float reduced = _mass[i] * _mass[p] / (_mass[i] + _mass[p]);
                float inverseLever = 1 / (lever * lever);
                float kr = Math.Min(SpringRotation * springScale * multiplier * inverseLever,
                    reduced * MaxNaturalFrequency * MaxNaturalFrequency);
                float cr = Math.Min(DamperRotation * damperScale * multiplier * inverseLever, reduced * MaxDampingRate);
                Vec3 relativeVelocity = (velocity - parentVelocity) - (targetVelocity - parentTargetVelocity);
                Vec3 pair = InteractionMath.ClampMagnitude((targetOffset - offset) * kr - relativeVelocity * cr,
                    MaxRotation * multiplier / lever);
                force[i] += pair; force[p] -= pair;
            }
            // The grip joint acts in every state, as the hand joint does in B&S.
            for (int h = 0; h < MaxHandles; h++)
            {
                int i = _handleBody[h];
                if (i < 0 || i >= Count) continue;
                Vec3 velocity = sampled ? (position[i] - _lastPosition[i]) * (1 / dt) : Vec3.Zero;
                Vec3 handVelocity = _handleSampled[h] ? (_handleTarget[h] - _lastHandleTarget[h]) * (1 / dt) : Vec3.Zero;
                float k = Math.Min(HandSpring, _mass[i] * HandNaturalFrequency * HandNaturalFrequency);
                float c = Math.Min(HandDamper * 4, _mass[i] * HandDampingRate);
                Vec3 grip = (_handleTarget[h] - position[i]) * k -
                    (velocity - InteractionMath.ClampMagnitude(handVelocity, 12)) * c;
                Vec3 axis = _handleAxis[h];
                if (axis.LengthSquared > 0) grip -= axis * InteractionMath.Dot(grip, axis);
                force[i] += InteractionMath.ClampMagnitude(grip, HandMaxForce);
                _lastHandleTarget[h] = _handleTarget[h]; _handleSampled[h] = true;
            }
            for (int i = 0; i < Count; i++) { _lastPosition[i] = position[i]; _lastTarget[i] = target[i]; }
            _sampled = true;
            return true;
        }
    }
}
