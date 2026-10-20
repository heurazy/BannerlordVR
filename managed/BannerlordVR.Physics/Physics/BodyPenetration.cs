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
        public int HandIndex;
        // Bannerlord ItemTiers Tier1..Tier6 are B&S item tiers 0..5.
        public static int TierOf(ItemObject item) => item == null ? 2 : Math.Max(0, Math.Min(6, (int)item.Tier));
        // True while the blade holds the part through the active ragdoll's
        // hand joint (rigid across the blade) rather than a soft spring.
        public bool Locked { get; private set; }
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
            Agent source = null, bool pressure = false, int tier = 2)
        {
            PenetrationMaterial material = body.IsCorpse ? PenetrationMaterial.Flesh : MaterialFor(body.Agent, point.Part);
            PenetrationModifiers.Row row = PenetrationModifiers.Get(profile, material, tier);
            // Damager.CheckPenetration: the material row must allow it, a
            // pressure entry needs pressureAllowed, an impact the min speed.
            string refused = !row.Allow ? "material" : pressure ? (row.Pressure ? null : "no_pressure") :
                speed < row.MinVelocity ? "speed" : null;
            if (refused != null)
            {
                if (EnemyPhysicsSettings.Trace)
                    PhysicsLog.Info(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "PENETRATION refused reason={0} speed={1:F2} min={2:F2} material={3} tier={4}", refused, speed, row.MinVelocity, material, tier));
                return;
            }
            Begin(body, point, contact);
            if (Body == null) return;
            _profile = profile; _material = material; _tier = tier;
            // penetrationVelocityMaxDepth: a pressure entry has no impact
            // speed and stops at the curve's start.
            _depthLimit = row.DepthLimit(pressure ? row.MinVelocity : speed);
            _source = source; _zone = ZoneFor(body, point); _part = point.Part;
            if (!body.IsCorpse) BloodFx.Burst(BloodFx.Kind.Enter, contact, Vec3.Up);
            _mass = EnemyPhysicsRuntime.PartMass(body, point.Bone);
            _maxDepth = 0; _lockedLogged = false;
            if (EnemyPhysicsSettings.Trace)
                PhysicsLog.Info(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "PENETRATION begin profile={0} zone={1} material={2} tier={3} speed={4:F2} pressure={5} bone={6} depth_limit={7:F3} damper_x={8:F2}",
                    profile, _zone, material, tier, speed, pressure, point.Bone, _depthLimit, row.DamperMultiplier));
        }

        public void Begin(EnemyBody body, BodyPoint point, Vec3 contact)
        {
            Body = body; _bone = point.Bone; _sampled = false; _age = 0; _lastDepth = 0; LastResistance = 0; _gate.Reset();
            _deepDone = _skewerDone = false; _source = null; _zone = BodyZone.Torso; _part = point.Part; _depthBase = -1;
            MatrixFrame bone = body.Frame.TransformToParent(body.Skeleton.GetBoneEntitialFrameWithIndex(_bone));
            _localContact = bone.TransformToLocalNonOrthogonal(contact);
            _hasLocalAxis = false;
            if (!body.IsCorpse && !EnemyPhysicsRuntime.BeginLivingGrab(body)) Release();
        }
        // Damage that would kill while the blade is still inside is held back
        // and dealt when it leaves the body: the victim dies on withdrawal.
        private float _pending, _woundLost, _depthBase = -1, _maxDepth;
        private bool _lockedLogged;
        private const float MaxShove = .1f, LateralLoss = .5f;
        private int _tier = 2;
        // The blade direction in the part's frame at entry: the joint's
        // angular limits are relative to it.
        private Vec3 _localAxis;
        private bool _hasLocalAxis;
        private float _depthLimit = float.PositiveInfinity;
        private void End(string reason, float value = 0)
        {
            if (EnemyPhysicsSettings.Trace && Body != null)
                PhysicsLog.Info(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "PENETRATION end reason={0} value={1:F3} age={2:F2} max_depth={3:F3} locked={4}", reason, value, _age, _maxDepth, Locked));
            Release();
        }
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
            if (Locked && Body != null) EnemyPhysicsRuntime.ReleaseActiveGrab(Body, HandIndex);
            Locked = false;
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
            if ((!Body.IsCorpse && !Body.Agent.IsActive()) || _age > 8) { End(_age > 8 ? "timeout" : "victim_inactive"); return false; }
            // Let a newly killed target enter the engine's corpse state itself.
            if (!Body.HasNativePhysics)
            {
                if (_age > .4f) End("no_native_physics");
                return Body != null;
            }
            BodyPoint p = Body.ReadContact(_bone);
            MatrixFrame bone = Body.Frame.TransformToParent(Body.Skeleton.GetBoneEntitialFrameWithIndex(p.Bone));
            Vec3 anchor = bone.TransformToParent(_localContact);
            Vec3 axis = bladeTip - bladeBase;
            float bladeLength = axis.Length;
            if (bladeLength < .05f) { End("no_blade"); return false; }
            axis.Normalize();
            // The VR blade is the controller: it cannot be stopped by the
            // joint. Measured on the blade's line, so a hand pushed on past
            // the hilt still holds the part (the B&S weapon would stop there
            // while the arm keeps pushing).
            float along = InteractionMath.Dot(anchor - bladeBase, axis);
            Vec3 bladePoint = bladeBase + axis * along;
            // Damager.Penetrate locks x and y and never lets go sideways: only
            // the blade leaving along its axis ends the joint. The VR blade
            // cannot be slowed by the body, so a fast hand leaves the part
            // behind; 16 cm released every stab within 0.2-0.36 s. Only a part
            // half a metre off the blade (a lost body) is let go.
            if ((bladePoint - anchor).Length > LateralLoss)
            { End("slipped_off_blade", (bladePoint - anchor).Length); return false; }
            if (!_hasLocalAxis)
            {
                Vec3 local = bone.rotation.TransformToLocal(axis);
                if (local.Length > 1e-4f) { local.Normalize(); _localAxis = local; _hasLocalAxis = true; }
            }
            // The tip came back out of the entry: the blade is withdrawn.
            if (along > bladeLength + .03f) { End("withdrawn", along - bladeLength); return false; }
            // Depth counts what went in after the entry, not where along the
            // blade the first contact happened to be sampled.
            float rawDepth = Math.Max(0, bladeLength - along);
            if (_depthBase < 0) _depthBase = Math.Min(rawDepth, bladeLength);
            float depth = Math.Max(0, rawDepth - _depthBase);
            // Joint zMotion Limited at penetrationVelocityMaxDepth, and never
            // past the hilt: beyond it the stopped blade carries the part.
            float limit = Math.Min(_depthLimit, Math.Max(0, bladeLength - .02f - _depthBase));
            bool atLimit = depth > limit;
            Vec3 bladeTarget = bladePoint;
            if (atLimit)
            {
                // The stopped blade stays in while the hand pushes on (B&S:
                // the weapon stays in the body, the arm keeps shoving it);
                // only a hand far beyond any reach has let go of it.
                if (depth - limit > 1.5f) { End("overshoot", depth - limit); return false; }
                // The part belongs where the stopped blade's depth ends, further
                // along the blade (towards the tip). The shove is bounded: past
                // 10 cm the arm joint's pull would launch the body.
                bladeTarget = bladePoint + axis * Math.Min(depth - limit, MaxShove);
                depth = limit;
            }
            _lastAnchor = anchor;
            _maxDepth = Math.Max(_maxDepth, depth);
            if (!Body.IsCorpse)
            {
                EnemyPhysicsRuntime.DeepenWound(Body.Agent, _bone, depth);
                DeepDamage(anchor, depth);
                if (Body == null) return false;
            }
            Vec3 targetVelocity = _sampled ? InteractionMath.ClampMagnitude((bladePoint - _lastBladePoint) * (1 / dt), 8) : Vec3.Zero;
            _lastBladePoint = bladePoint; _sampled = true;
            Vec3 error = bladeTarget - anchor;
            Vec3 relativeVelocity = p.Velocity - targetVelocity;
            // Damager.Penetrate locks x and y: on an active ragdoll the part is
            // carried across the blade by the player's arm joint (5000 N/m,
            // 3000 N), with its pose following. Elsewhere a soft spring.
            Vec3 force = Vec3.Zero;
            Locked = !Body.IsCorpse && EnemyPhysicsSettings.ActiveRagdoll &&
                EnemyPhysicsRuntime.PierceActive(Body, HandIndex, p.Bone, bladeTarget, axis, atLimit,
                    _hasLocalAxis ? _localAxis : Vec3.Zero, PenetrationJoint.AngleLimit(_profile, depth), bone, anchor);
            if (Locked && !_lockedLogged && EnemyPhysicsSettings.Trace)
            { _lockedLogged = true; PhysicsLog.Info("PENETRATION locked to the arm joint age=" + _age.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)); }
            if (!Locked)
            {
                Vec3 lateralVelocity = atLimit ? relativeVelocity : relativeVelocity - axis * InteractionMath.Dot(relativeVelocity, axis);
                force = InteractionMath.SpringForce(error, lateralVelocity,
                    EnemyPhysicsSettings.Spring, EnemyPhysicsSettings.Damping, EnemyPhysicsSettings.MaxForce * .5f);
            }
            // B&S axial damper: the part is dragged along with the blade, with
            // a different brake going in and coming out, set by its armour.
            float damper = atLimit ? 0 : PenetrationJoint.Damper(_profile, _material, true, depth >= _lastDepth, _tier);
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
