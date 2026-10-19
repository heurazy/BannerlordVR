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

        public void Begin(EnemyBody body, int point, Vec3 hand)
        {
            Body = body; Point = point;
            // Anchor the contact in the bone's space, so it follows rotation.
            MatrixFrame world = body.Frame.TransformToParent(body.Skeleton.GetBoneEntitialFrameWithIndex(body.Points[point].Bone));
            _localOffset = world.TransformToLocalNonOrthogonal(hand);
            _lastTarget = hand;
        }
        public void Release() { Body = null; _localOffset = Vec3.Zero; _lastTarget = Vec3.Zero; }

        public void Tick(MatrixFrame hand, float dt)
        {
            if (!Held) return;
            BodyPoint p = Body.Points[Point];
            MatrixFrame boneWorld = Body.Frame.TransformToParent(Body.Skeleton.GetBoneEntitialFrameWithIndex(p.Bone));
            Vec3 anchor = boneWorld.TransformToParent(_localOffset);
            Vec3 error = hand.origin - anchor;
            if (error.Length > EnemyPhysicsSettings.BreakDistance) { Release(); return; }
            Vec3 targetVelocity = InteractionMath.ClampMagnitude((hand.origin - _lastTarget) * (1 / dt), 8);
            _lastTarget = hand.origin;
            if (Body.HasNativePhysics)
            {
                Vec3 force = InteractionMath.SpringForce(error, p.Velocity - targetVelocity,
                    EnemyPhysicsSettings.Spring, EnemyPhysicsSettings.Damping, EnemyPhysicsSettings.MaxForce);
                Body.Agent.ApplyForceOnRagdoll(p.Bone, in force);
            }
            else if (!Body.IsCorpse)
            {
                MatrixFrame target = Body.Frame.TransformToLocalNonOrthogonal(hand);
                target.rotation.Orthonormalize();
                target.origin = Body.Frame.TransformToLocalNonOrthogonal(boneWorld.origin + error);
                string name = Body.Skeleton.GetBoneName(p.Bone) ?? "";
                if (name.EndsWith("hand"))
                    CompliantPose.Arm(Body.Skeleton, p.Bone, target);
                else if (p.Part == TaleWorlds.MountAndBlade.BoneBodyPartType.Head || p.Part == TaleWorlds.MountAndBlade.BoneBodyPartType.Neck)
                {
                    sbyte parent = Body.Skeleton.GetParentBoneIndex(p.Bone);
                    if (parent >= 0) CompliantPose.Aim(Body.Skeleton, parent, p.Bone, target.origin, .35f);
                }
            }
        }
    }
}
