using TaleWorlds.Library;

namespace BannerlordVR.VR.Physics
{
    // Keeps a pierced corpse's contact attached laterally to the blade while
    // permitting axial withdrawal. The real engine still solves the ragdoll.
    public sealed class CorpsePenetration
    {
        public EnemyBody Body { get; private set; }
        private int _point;
        private Vec3 _localContact, _lastBladePoint;
        private bool _sampled;
        private float _age;
        public void Begin(EnemyBody body, int point, Vec3 contact)
        {
            Body = body; _point = point; _sampled = false; _age = 0;
            MatrixFrame bone = body.Frame.TransformToParent(body.Skeleton.GetBoneEntitialFrameWithIndex(body.Points[point].Bone));
            _localContact = bone.TransformToLocalNonOrthogonal(contact);
        }
        public void Release() { Body = null; _sampled = false; }
        public bool Tick(Vec3 bladeBase, Vec3 bladeTip, float dt)
        {
            if (Body == null) return false;
            _age += dt;
            if (!Body.IsCorpse || _age > 8) { Release(); return false; }
            // Let a newly killed target enter the engine's corpse state itself.
            if (!Body.HasNativePhysics)
            {
                if (_age > .25f) Release();
                return Body != null;
            }
            BodyPoint p = Body.Points[_point];
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
