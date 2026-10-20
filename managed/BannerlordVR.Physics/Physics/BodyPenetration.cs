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
        private Agent _source;
        private BodyZone _zone = BodyZone.Torso;
        private BoneBodyPartType _part;
        private bool _deepDone, _skewerDone;
        public static BodyZone ZoneFor(EnemyBody body, BodyPoint point)
        {
            string name = body.Skeleton.GetBoneName(point.Bone) ?? "";
            switch (point.Part)
            {
                case BoneBodyPartType.Head: return BodyZone.Head;
                case BoneBodyPartType.Neck: return BodyZone.Neck;
                case BoneBodyPartType.ArmLeft:
                case BoneBodyPartType.ArmRight: return name.EndsWith("hand") ? BodyZone.Hand : BodyZone.Arm;
                case BoneBodyPartType.Legs: return name.Contains("foot") ? BodyZone.Foot : BodyZone.Leg;
                default: return BodyZone.Torso;
            }
        }
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

        // pressure: Damager.TryHitByPressure, a slow push along the blade that
        // enters without impact damage and without the minimum speed.
        public void Begin(EnemyBody body, BodyPoint point, Vec3 contact, PenetrationProfile profile, float speed,
            Agent source = null, bool pressure = false)
        {
            PenetrationMaterial material = body.IsCorpse ? PenetrationMaterial.Flesh : MaterialFor(body.Agent, point.Part);
            if (!pressure && speed < PenetrationJoint.MinVelocity(profile, material)) return;
            Begin(body, point, contact);
            if (Body == null) return;
            _profile = profile; _material = material;
            _source = source; _zone = ZoneFor(body, point); _part = point.Part;
            if (!body.IsCorpse) BloodFx.Burst(BloodFx.Kind.Enter, contact, Vec3.Up);
            _mass = EnemyPhysicsRuntime.PartMass(body, point.Bone);
        }

        public void Begin(EnemyBody body, BodyPoint point, Vec3 contact)
        {
            Body = body; _bone = point.Bone; _sampled = false; _age = 0; _lastDepth = 0; LastResistance = 0; _gate.Reset();
            _deepDone = _skewerDone = false; _source = null; _zone = BodyZone.Torso; _part = point.Part; _depthBase = -1;
            MatrixFrame bone = body.Frame.TransformToParent(body.Skeleton.GetBoneEntitialFrameWithIndex(_bone));
            _localContact = bone.TransformToLocalNonOrthogonal(contact);
            if (!body.IsCorpse && !EnemyPhysicsRuntime.BeginLivingGrab(body)) Release();
        }
        // Damage that would kill while the blade is still inside is held back
        // and dealt when it leaves the body: the victim dies on withdrawal.
        private float _pending, _woundLost, _depthBase = -1;
        private EnemyBody _woundBody;
        private Agent _pendingVictim;
        private Vec3 _lastAnchor;
        public float PendingDamage => _pending;

        public void Release()
        {
            Agent victim = _pendingVictim;
            float pending = _pending, woundLost = _woundLost;
            EnemyBody woundBody = _woundBody;
            _pending = 0; _pendingVictim = null; _woundLost = 0; _woundBody = null;
            Agent source = _source;
            DamageTypes woundType = PenetrationJoint.Get(_profile).Pierce ? DamageTypes.Pierce : DamageTypes.Cut;
            if (woundLost > 0 && woundBody?.Agent != null && woundBody.Agent.IsActive() && (pending < .5f || victim == null))
                EnemyPhysicsRuntime.RecordWound(source, woundBody, woundBody.ReadContact(_bone), _lastAnchor, woundType, woundLost, false);
            Body = null; _sampled = false; _source = null;
            if (victim == null || pending < .5f || !victim.IsActive()) return;
            DamageTypes type = PenetrationJoint.Get(_profile).Pierce ? DamageTypes.Pierce : DamageTypes.Cut;
            float lost = PhysicalHit.ApplyPenetration(source, victim, _bone, _part, _lastAnchor, pending, type);
            BloodFx.Burst(BloodFx.Kind.Exit, _lastAnchor, Vec3.Up);
            if (EnemyPhysicsSettings.Trace)
                PhysicsLog.Info(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "PENETRATION withdrawn profile={0} zone={1} held_damage={2:F1} health_lost={3:F1} alive={4}",
                    _profile, _zone, pending, lost, victim.IsActive()));
        }

        private void DeepDamage(Vec3 anchor, float depth)
        {
            Agent victim = Body.Agent;
            float limit = victim.HealthLimit;
            if (!_deepDone && depth > PenetrationDamage.DeepTrigger(_profile, _zone))
            {
                _deepDone = true;
                Deliver(victim, anchor, PenetrationDamage.DeepDamage(_profile, _zone, limit), "deep");
            }
            if (Body != null && !_skewerDone && PenetrationDamage.SkewerDamage(_profile) > 0 &&
                depth > PenetrationDamage.SkewerDepth(_zone))
            {
                _skewerDone = true;
                Deliver(victim, anchor, PenetrationDamage.SkewerTotal(_profile, _zone, limit), "skewer");
            }
        }

        private void Deliver(Agent victim, Vec3 anchor, float damage, string kind)
        {
            DamageTypes type = PenetrationJoint.Get(_profile).Pierce ? DamageTypes.Pierce : DamageTypes.Cut;
            // Leave at least one point while the blade is in; the rest waits
            // for Release.
            // Bannerlord deals whole points: the cap is too, or 88.6 rounds
            // to 89 and leaves the victim under one point.
            float now = Math.Max(0, Math.Min((float)Math.Floor(damage), (float)Math.Floor(victim.Health - 1)));
            if (damage - now > 0) { _pending += damage - now; _pendingVictim = victim; _lastAnchor = anchor; }
            float lost = now >= .5f ? PhysicalHit.ApplyPenetration(_source, victim, _bone, _part, anchor, now, type) : 0;
            if (lost > 0) BloodFx.Burst(kind == "skewer" ? BloodFx.Kind.Exit : BloodFx.Kind.Enter, anchor, Vec3.Up);
            if (EnemyPhysicsSettings.Trace)
                PhysicsLog.Info(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "PENETRATION {0} profile={1} zone={2} material={3} damage={4:F1} health_lost={5:F1} held_until_withdrawal={6:F1}",
                    kind, _profile, _zone, _material, damage, lost, _pending));
            // The wound starts bleeding when the blade comes out, so bleeding
            // cannot finish a victim the blade is still holding at 1 point.
            if (lost > 0 && Body != null && Body.Agent == victim) { _woundLost += lost; _woundBody = Body; }
            if (!victim.IsActive()) Release();
        }
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
            // Depth counts what went in after the entry, not where along the
            // blade the first contact happened to be sampled.
            float rawDepth = Math.Max(0, (bladeTip - bladeBase).Length - along);
            if (_depthBase < 0) _depthBase = rawDepth;
            float depth = Math.Max(0, rawDepth - _depthBase);
            _lastAnchor = anchor;
            if (!Body.IsCorpse)
            {
                EnemyPhysicsRuntime.DeepenWound(Body.Agent, _bone, depth);
                DeepDamage(anchor, depth);
                if (Body == null) return false;
            }
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
            // Sliding inside the wound bleeds; pulling out spills at the exit.
            if (!Body.IsCorpse && Math.Abs(depth - _lastDepth) > .004f)
                BloodFx.Burst(depth < _lastDepth ? BloodFx.Kind.Exit : BloodFx.Kind.Inside, anchor, -axis);
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
