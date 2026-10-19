using TaleWorlds.Library;

namespace BannerlordVR.VR.Physics
{
    // Keeps a pierced body's contact attached laterally to the blade while
    // permitting axial withdrawal. The real engine still solves the ragdoll.
    public sealed class BodyPenetration
    {
        public EnemyBody Body { get; private set; }
        private sbyte _bone;
        private Vec3 _localContact, _lastBladePoint;
        private bool _sampled;
        private float _age;
        public void Begin(EnemyBody body, BodyPoint point, Vec3 contact)
        {
            Body = body; _bone = point.Bone; _sampled = false; _age = 0;
            MatrixFrame bone = body.Frame.TransformToParent(body.Skeleton.GetBoneEntitialFrameWithIndex(_bone));
            _localContact = bone.TransformToLocalNonOrthogonal(contact);
            if (!body.IsCorpse && !EnemyPhysicsRuntime.BeginLivingGrab(body)) Release();
        }
        public void Release() { Body = null; _sampled = false; }
        public bool Tick(Vec3 bladeBase, Vec3 bladeTip, float dt)
        {
            if (Body == null) return false;
            EnemyPhysicsRuntime.MaintainLivingGrab(Body);
            _age += dt;
            if ((!Body.IsCorpse && !Body.Agent.IsActive()) || _age > 8) { Release(); return false; }
            // Let a newly killed target enter the engine's corpse state itself.
            if (!Body.HasNativePhysics)
            {
                if (_age > .4f) Release();
                return Body != null;
            }
            BodyPoint p = Body.ReadContact(_bone);
            MatrixFrame bone = Body.Frame.TransformToParent(Body.Skeleton.GetBoneEntitialFrameWithIndex(p.Bone));
            Vec3 anchor = bone.TransformToParent(_localContact);
            Vec3 bladePoint = InteractionMath.ClosestPointOnSegment(bladeBase, bladeTip, anchor);
            Vec3 axis = bladeTip - bladeBase;
            if (axis.Length < .05f || (bladePoint - anchor).Length > .16f)
            { Release(); return false; }
            axis.Normalize();
            float along = InteractionMath.Dot(anchor - bladeBase, axis);
            if (along < -.02f || along > (bladeTip - bladeBase).Length + .03f)
            { Release(); return false; }
            if (!Body.IsCorpse) EnemyPhysicsRuntime.DeepenWound(Body.Agent, _bone,
                System.Math.Max(0, (bladeTip - bladeBase).Length - along));
            Vec3 targetVelocity = _sampled ? InteractionMath.ClampMagnitude((bladePoint - _lastBladePoint) * (1 / dt), 8) : Vec3.Zero;
            _lastBladePoint = bladePoint; _sampled = true;
            Vec3 error = bladePoint - anchor;
            Vec3 relativeVelocity = p.Velocity - targetVelocity;
            // Free axial sliding; friction is deliberately weaker than lateral attachment.
            Vec3 lateralVelocity = relativeVelocity - axis * InteractionMath.Dot(relativeVelocity, axis);
            Vec3 force = InteractionMath.SpringForce(error, lateralVelocity,
                EnemyPhysicsSettings.Spring, EnemyPhysicsSettings.Damping, EnemyPhysicsSettings.MaxForce * .5f);
            Body.Agent.ApplyForceOnRagdoll(p.Bone, in force);
            return true;
        }
    }
}
