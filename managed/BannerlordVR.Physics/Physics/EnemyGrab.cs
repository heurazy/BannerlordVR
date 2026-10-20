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
        public bool Held => Body != null;
        public int HandIndex;

        public void Begin(EnemyBody body, int point, Vec3 hand)
        {
            Body = body; Point = point;
            // Anchor the contact in the bone's space, so it follows rotation.
            MatrixFrame world = body.Frame.TransformToParent(body.Skeleton.GetBoneEntitialFrameWithIndex(body.Points[point].Bone));
            _localOffset = world.TransformToLocalNonOrthogonal(hand);
            _lastTarget = hand;
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
            Body = null; _localOffset = Vec3.Zero; _lastTarget = Vec3.Zero;
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
                Vec3 force = InteractionMath.SpringForce(error, p.Velocity - targetVelocity,
                    EnemyPhysicsSettings.Spring, EnemyPhysicsSettings.Damping, EnemyPhysicsSettings.MaxForce);
                Body.Agent.ApplyForceOnRagdoll(p.Bone, in force);
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
