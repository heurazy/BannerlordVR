using System;
using TaleWorlds.Engine;
using TaleWorlds.Library;

namespace BannerlordVR.VR.Physics
{
    public sealed class EnemyGrab
    {
        public EnemyBody Body { get; private set; }
        public int Point { get; private set; }
        private Vec3 _localOffset;
        private Vec3 _lastTarget;
        private float _waking;
        private readonly PhysicsStepGate _gate = new PhysicsStepGate();
        public bool Held => Body != null;
        public int HandIndex;
        public BannerlordVR.VR.HandRole Role;

        public void Begin(EnemyBody body, int point, Vec3 hand)
        {
            Body = body; Point = point;
            // Anchor the contact in the bone's space, so it follows rotation.
            MatrixFrame world = body.Frame.TransformToParent(body.Skeleton.GetBoneEntitialFrameWithIndex(body.Points[point].Bone));
            _localOffset = world.TransformToLocalNonOrthogonal(hand);
            _lastTarget = hand; _waking = 0; _gate.Reset();
            bool native = EnemyPhysicsRuntime.BeginLivingGrab(body);
            if (!body.IsCorpse && !native)
            {
                BodyPoint p = body.Points[point];
                string name = body.Skeleton.GetBoneName(p.Bone) ?? "";
                if ((p.Part != TaleWorlds.MountAndBlade.BoneBodyPartType.Head &&
                     p.Part != TaleWorlds.MountAndBlade.BoneBodyPartType.Neck && !name.EndsWith("hand")) ||
                    body.Skeleton.GetCurrentRagdollState() != RagdollState.Disabled) Release();
            }
        }
        public void Release()
        {
            if (Body != null && EnemyPhysicsSettings.ActiveRagdoll) EnemyPhysicsRuntime.ReleaseActiveGrab(Body, HandIndex);
            if (Body != null) PhysicalBlade.ClearGrip(Role);
            Body = null; _localOffset = Vec3.Zero; _lastTarget = Vec3.Zero;
        }

        // Low estimates of a human ragdoll's part masses (89.7 kg over 16
        // bodies): too light a guess only softens the grip.
        private static float PartMass(string bone)
        {
            if (bone.Contains("hand") || bone.Contains("foot")) return 1;
            if (bone.Contains("forearm")) return 1.5f;
            if (bone.Contains("upperarm") || bone.Contains("neck") || bone.Contains("clavicle")) return 2;
            if (bone.Contains("calf")) return 3.5f;
            if (bone.Contains("head")) return 4;
            if (bone.Contains("thigh")) return 6;
            if (bone.Contains("spine") || bone.Contains("pelvis") || bone.Contains("abdomen") || bone.Contains("chest")) return 8;
            return 3;
        }

        public void Tick(MatrixFrame hand, float dt)
        {
            if (!Held) return;
            EnemyPhysicsRuntime.MaintainLivingGrab(Body);
            BodyPoint p = Body.Points[Point];
            MatrixFrame boneWorld = Body.Frame.TransformToParent(Body.Skeleton.GetBoneEntitialFrameWithIndex(p.Bone));
            Vec3 anchor = boneWorld.TransformToParent(_localOffset);
            Vec3 error = hand.origin - anchor;
            if (error.Length > EnemyPhysicsSettings.BreakDistance) { Release(); return; }
            // B&S: the hand is jointed to the handle, so the shown hand stays
            // on the grabbed point while the controller pulls on.
            PhysicalBlade.SetGrip(Role, anchor - hand.origin);
            Vec3 targetVelocity = InteractionMath.ClampMagnitude((hand.origin - _lastTarget) * (1 / dt), 8);
            _lastTarget = hand.origin;
            // Blade & Sorcery: the grip is a joint inside the whole-body drive.
            if (EnemyPhysicsSettings.ActiveRagdoll && !Body.IsCorpse && Body.HasNativePhysics)
            {
                if (!EnemyPhysicsRuntime.HandleActiveGrab(Body, HandIndex, p, hand.origin, boneWorld, anchor)) Release();
                return;
            }
            if (Body.HasNativePhysics)
            {
                // B&S: a corpse's part is held by the same arm joint as a
                // living one (spring 5000, max 3000 N), one force per PhysX step.
                long steps = EnemyPhysicsRuntime.PhysicsSteps;
                if (steps >= 0 && !_gate.CanApply(steps)) return;
                float mass = PartMass(Body.Skeleton.GetBoneName(p.Bone) ?? "");
                float k = Math.Min(RagdollPinDrive.HandSpring, mass * RagdollPinDrive.HandNaturalFrequency * RagdollPinDrive.HandNaturalFrequency);
                float c = Math.Min(RagdollPinDrive.HandDamper * 4, mass * RagdollPinDrive.HandDampingRate);
                Vec3 force = InteractionMath.ClampMagnitude(error * k - (p.Velocity - targetVelocity) * c, RagdollPinDrive.HandMaxForce);
                Body.Agent.ApplyForceOnRagdoll(p.Bone, in force);
                if (steps >= 0) _gate.Applied(EnemyPhysicsRuntime.PhysicsSteps);
            }
            else if (Body.IsCorpse && Body.Skeleton.GetCurrentRagdollState() != RagdollState.Disabled && (_waking += dt) < .5f)
            {
                // The woken ragdoll becomes active within a few ticks.
            }
            else if (!Body.IsCorpse)
            {
                // Never pose over native physics while activation/recovery is pending.
                if (Body.Skeleton.GetCurrentRagdollState() != RagdollState.Disabled)
                {
                    if (!EnemyPhysicsRuntime.HasLivingLease(Body.Agent)) Release();
                    return;
                }
                MatrixFrame target = Body.Frame.TransformToLocalNonOrthogonal(hand);
                target.rotation.Orthonormalize();
                target.origin = Body.Frame.TransformToLocalNonOrthogonal(boneWorld.origin + error);
                string name = Body.Skeleton.GetBoneName(p.Bone) ?? "";
                if (name.EndsWith("hand"))
                    CompliantPose.Arm(Body.Skeleton, p.Bone, target, name.StartsWith("l_")
                        ? Body.Agent.Monster.LeftUpperArmBoneIndex : Body.Agent.Monster.RightUpperArmBoneIndex);
                else if (p.Part == TaleWorlds.MountAndBlade.BoneBodyPartType.Head || p.Part == TaleWorlds.MountAndBlade.BoneBodyPartType.Neck)
                {
                    sbyte parent = Body.Skeleton.GetParentBoneIndex(p.Bone);
                    if (parent >= 0) CompliantPose.Aim(Body.Skeleton, parent, p.Bone, target.origin, .35f);
                }
                else if (!EnemyPhysicsRuntime.HasLivingLease(Body.Agent)) Release();
            }
            else Release(); // The engine retired this corpse's native ragdoll.
        }
    }
}
