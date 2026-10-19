using System;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

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
        private float _age, _lastDepth, _mass = 5;
        private PenetrationProfile _profile = PenetrationProfile.SwordPierce;
        private PenetrationMaterial _material = PenetrationMaterial.Flesh;
        private readonly PhysicsStepGate _gate = new PhysicsStepGate();
        // Magnitude of the axial braking force, for the controller haptics.
        public float LastResistance { get; private set; }

        // Blade & Sorcery damager profile closest to the Bannerlord weapon.
        public static bool ProfileFor(WeaponComponentData usage, bool thrust, out PenetrationProfile profile)
        {
            profile = PenetrationProfile.SwordPierce;
            if (usage == null) return false;
            switch (usage.WeaponClass)
            {
                case WeaponClass.Dagger: profile = thrust ? PenetrationProfile.DaggerPierce : PenetrationProfile.DaggerSlash; return true;
                case WeaponClass.Pick: profile = PenetrationProfile.WarpickPierce; return thrust || usage.SwingDamageType == DamageTypes.Pierce;
                case WeaponClass.OneHandedAxe: profile = PenetrationProfile.AxeSlash1H; return !thrust;
                case WeaponClass.TwoHandedAxe:
                case WeaponClass.LowGripPolearm: profile = PenetrationProfile.AxeSlash2H; return !thrust;
                case WeaponClass.Arrow:
                case WeaponClass.Bolt: profile = PenetrationProfile.Arrow; return true;
                default:
                    if (thrust) { profile = PenetrationProfile.SwordPierce; return usage.ThrustDamageType == DamageTypes.Pierce || usage.ThrustDamageType == DamageTypes.Cut; }
                    profile = PenetrationProfile.SaberSlash;
                    return false;
            }
        }

        // B&S looks up the collider's physic material; Bannerlord armour has a
        // material per equipment piece covering the hit part.
        public static PenetrationMaterial MaterialFor(Agent agent, BoneBodyPartType part)
        {
            EquipmentIndex slot;
            switch (part)
            {
                case BoneBodyPartType.Head:
                case BoneBodyPartType.Neck: slot = EquipmentIndex.Head; break;
                case BoneBodyPartType.ArmLeft:
                case BoneBodyPartType.ArmRight: slot = EquipmentIndex.Gloves; break;
                case BoneBodyPartType.Legs: slot = EquipmentIndex.Leg; break;
                default: slot = EquipmentIndex.Body; break;
            }
            ArmorComponent armor = agent?.SpawnEquipment[slot].Item?.ArmorComponent;
            switch (armor?.MaterialType ?? ArmorComponent.ArmorMaterialTypes.None)
            {
                case ArmorComponent.ArmorMaterialTypes.Leather: return PenetrationMaterial.Leather;
                case ArmorComponent.ArmorMaterialTypes.Chainmail: return PenetrationMaterial.Chainmail;
                case ArmorComponent.ArmorMaterialTypes.Plate: return PenetrationMaterial.Plate;
                default: return PenetrationMaterial.Flesh;
            }
        }

        public void Begin(EnemyBody body, BodyPoint point, Vec3 contact, PenetrationProfile profile, float speed)
        {
            PenetrationMaterial material = body.IsCorpse ? PenetrationMaterial.Flesh : MaterialFor(body.Agent, point.Part);
            if (speed < PenetrationJoint.MinVelocity(profile, material)) return;
            Begin(body, point, contact);
            if (Body == null) return;
            _profile = profile; _material = material;
            _mass = EnemyPhysicsRuntime.PartMass(body, point.Bone);
        }

        public void Begin(EnemyBody body, BodyPoint point, Vec3 contact)
        {
            Body = body; _bone = point.Bone; _sampled = false; _age = 0; _lastDepth = 0; LastResistance = 0; _gate.Reset();
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
            float depth = Math.Max(0, (bladeTip - bladeBase).Length - along);
            if (!Body.IsCorpse) EnemyPhysicsRuntime.DeepenWound(Body.Agent, _bone, depth);
            Vec3 targetVelocity = _sampled ? InteractionMath.ClampMagnitude((bladePoint - _lastBladePoint) * (1 / dt), 8) : Vec3.Zero;
            _lastBladePoint = bladePoint; _sampled = true;
            Vec3 error = bladePoint - anchor;
            Vec3 relativeVelocity = p.Velocity - targetVelocity;
            // Free axial sliding; friction is deliberately weaker than lateral attachment.
            Vec3 lateralVelocity = relativeVelocity - axis * InteractionMath.Dot(relativeVelocity, axis);
            Vec3 force = InteractionMath.SpringForce(error, lateralVelocity,
                EnemyPhysicsSettings.Spring, EnemyPhysicsSettings.Damping, EnemyPhysicsSettings.MaxForce * .5f);
            // B&S axial damper: the part is dragged along with the blade, with
            // a different brake going in and coming out, set by its armour.
            float damper = PenetrationJoint.Damper(_profile, _material, true, depth >= _lastDepth);
            Vec3 axial = PenetrationJoint.AxialForce(axis, p.Velocity, targetVelocity, damper, _mass);
            _lastDepth = depth;
            LastResistance = axial.Length;
            force += axial;
            long steps = EnemyPhysicsRuntime.PhysicsSteps;
            if (steps >= 0 && !_gate.CanApply(steps)) return true;
            Body.Agent.ApplyForceOnRagdoll(p.Bone, in force);
            if (steps >= 0) _gate.Applied(EnemyPhysicsRuntime.PhysicsSteps);
            return true;
        }
    }
}
