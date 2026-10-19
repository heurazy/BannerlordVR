using System;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR.Physics
{
    public static class PhysicalHit
    {
        // RegisterBlow preserves mortality rules, hit events and kill attribution.
        // This prototype uses an explicit armor-aware damage approximation, not
        // a claim to reproduce the native combat model's full damage calculation.
        public static void Apply(Agent player, EnemyBody body, BodyPoint point, Vec3 contact,
                                 Vec3 velocity, MissionWeapon weapon, EquipmentIndex slot, bool thrust,
                                 PushEffect push = PushEffect.None)
        {
            Vec3 direction = velocity.NormalizedCopy();
            float speed = velocity.Length;
            if (speed < .01f) return;
            if (body.IsCorpse)
            {
                if (body.HasNativePhysics)
                {
                    Vec3 force = InteractionMath.ClampMagnitude(velocity * 25, EnemyPhysicsSettings.MaxForce);
                    body.Agent.ApplyForceOnRagdoll(point.Bone, in force);
                }
                return;
            }
            WeaponComponentData usage = weapon.IsEmpty ? null : weapon.CurrentUsageItem;
            DamageTypes damageType = usage == null ? DamageTypes.Blunt : thrust ? usage.ThrustDamageType : usage.SwingDamageType;
            float raw = usage == null ? Math.Min(30, speed * speed * 1.5f) :
                (thrust ? usage.ThrustDamage : usage.SwingDamage) * Math.Min(1.5f, speed / (thrust ? 2.5f : 5));
            Register(player, body, point, contact, direction, weapon, slot, usage, damageType, raw, thrust, false, push);
        }

        // Blade & Sorcery material of a VR hit, for its push level table.
        public static PushMaterial Material(WeaponComponentData usage, bool thrust)
        {
            if (usage == null) return PushMaterial.Punch;
            if (thrust) return PushMaterial.Pierce;
            if (usage.SwingDamageType == DamageTypes.Blunt) return usage.WeaponLength > 90 ? PushMaterial.Blunt : PushMaterial.BluntLight;
            switch (usage.WeaponClass)
            {
                case WeaponClass.OneHandedAxe:
                case WeaponClass.TwoHandedAxe:
                case WeaponClass.LowGripPolearm: return PushMaterial.SlashAxe;
                case WeaponClass.TwoHandedSword:
                case WeaponClass.TwoHandedPolearm: return PushMaterial.SlashHeavy;
                default: return PushMaterial.Slash;
            }
        }

        public static void ApplyDrawCut(Agent player, EnemyBody body, BodyPoint point, Vec3 contact,
            Vec3 velocity, MissionWeapon weapon, EquipmentIndex slot)
        {
            if (body.IsCorpse || !body.Agent.IsActive() || point.Part != BoneBodyPartType.Neck || weapon.IsEmpty) return;
            WeaponComponentData usage = weapon.CurrentUsageItem;
            if (usage == null || usage.SwingDamageType != DamageTypes.Cut) return;
            // A held-neck draw has a length threshold instead of kinetic scaling.
            // Armor and RegisterBlow mortality still apply; this is not an instant-kill override.
            Vec3 direction = velocity.Length > .001f ? velocity.NormalizedCopy() : player.LookDirection;
            Register(player, body, point, contact, direction, weapon, slot, usage, DamageTypes.Cut,
                Math.Min(100, Math.Max(35, usage.SwingDamage * 1.25f)), false, true);
        }

        private static void Register(Agent player, EnemyBody body, BodyPoint point, Vec3 contact,
            Vec3 direction, MissionWeapon weapon, EquipmentIndex slot, WeaponComponentData usage,
            DamageTypes damageType, float raw, bool thrust, bool drawCut = false, PushEffect push = PushEffect.None)
        {
            float armor = body.Agent.GetBaseArmorEffectivenessForBodyPart(point.Part);
            float damage = raw * (point.Part == BoneBodyPartType.Head ? 1.5f : 1) / (1 + Math.Max(0, armor) * .045f);
            Agent victim = body.Agent;
            float lost = Deliver(player, victim, point, contact, direction, weapon, slot, usage,
                damageType, raw, Math.Max(1, Math.Min(150, (int)damage)), Math.Max(0, raw - damage), thrust, false, push);
            if (lost > 0 && body.Agent == victim)
                EnemyPhysicsRuntime.RecordWound(player, body, point, contact, damageType, lost, drawCut);
        }

        // B&S ragdoll collision: blunt, without a weapon, attributed to whoever
        // last pushed the body (BrainModuleHitReaction.lastHitSource).
        public static float ApplyRagdollImpact(Agent source, Agent victim, sbyte bone, BoneBodyPartType part, Vec3 contact, float damage)
        {
            if (victim == null || !victim.IsActive() || !InteractionMath.IsFinite(contact) || !InteractionMath.IsFinite(damage) || damage < .5f) return 0;
            var point = new BodyPoint { Bone = bone, Part = part, World = contact };
            return Deliver(source, victim, point, contact, -Vec3.Up, MissionWeapon.Invalid, EquipmentIndex.None,
                null, DamageTypes.Blunt, damage, Math.Max(1, (int)Math.Round(damage)), 0, false, true, PushEffect.None);
        }

        public static void ApplyBloodLoss(Agent source, Agent victim, sbyte bone, BoneBodyPartType part, Vec3 contact, int dose)
        {
            if (victim == null || !victim.IsActive() || dose <= 0 || !InteractionMath.IsFinite(contact)) return;
            // The initial wound already passed armor. Internal blood loss is a
            // quiet, non-staggering blow, not another weapon impact through armor.
            var point = new BodyPoint { Bone = bone, Part = part, World = contact };
            Deliver(source, victim, point, contact, Vec3.Up, MissionWeapon.Invalid, EquipmentIndex.None,
                null, DamageTypes.Cut, 0, Math.Min(6, dose), 0, false, true, PushEffect.None);
        }

        private static float Deliver(Agent player, Agent victim, BodyPoint point, Vec3 contact,
            Vec3 direction, MissionWeapon weapon, EquipmentIndex slot, WeaponComponentData usage,
            DamageTypes damageType, float raw, int damage, float absorbed, bool thrust, bool bloodLoss, PushEffect push)
        {
            // B&S staggers play as Bannerlord knockbacks; their recoil amount
            // (0.2 / 0.6 / 1) becomes the stun period. A destabilization is
            // handed to the physical body instead of a knockdown animation.
            float stun = bloodLoss ? 0 : push == PushEffect.None || push == PushEffect.Destabilize ? .2f : PushReaction.Recoil(push);
            BlowFlags flags = bloodLoss ? BlowFlags.NoSound | BlowFlags.ShrugOff :
                push == PushEffect.StaggerMedium || push == PushEffect.StaggerFull ? BlowFlags.KnockBack : BlowFlags.None;
            Mission mission = Mission.Current;
            int victimIndex = victim.Index;
            float before = victim.Health;
            var blow = new Blow(player?.Index ?? -1)
            {
                BoneIndex = point.Bone, VictimBodyPart = point.Part,
                GlobalPosition = contact, Direction = direction, SwingDirection = direction,
                DamageType = damageType, StrikeType = thrust ? StrikeType.Thrust : StrikeType.Swing,
                AttackType = AgentAttackType.Standard, BaseMagnitude = raw,
                InflictedDamage = damage,
                AbsorbedByArmor = absorbed, DamageCalculated = true,
                DefenderStunPeriod = stun, MovementSpeedDamageModifier = bloodLoss ? 0 : 1,
                BlowFlag = flags
            };
            blow.WeaponRecord.FillAsMeleeBlow(weapon.IsEmpty ? null : weapon.Item, usage,
                usage == null ? -1 : (int)slot, player?.Monster.MainHandItemBoneIndex ?? -1);
            var collision = AttackCollisionData.GetAttackCollisionDataForDebugPurpose(
                false, false, false, true, false, false, false, false, false, thrust, false, false,
                CombatCollisionResult.StrikeAgent, usage == null ? -1 : (int)slot,
                (int)blow.StrikeType, (int)damageType, point.Bone, point.Part,
                player?.Monster.MainHandBoneIndex ?? -1, Agent.UsageDirection.AttackUp, -1,
                CombatHitResultFlags.NormalHit, 1, 0, 0, stun, 0, 0, 0, 0,
                Vec3.Up, direction, contact, Vec3.Zero, Vec3.Zero, victim.Velocity, Vec3.Up);
            collision.BaseMagnitude = blow.BaseMagnitude;
            collision.MovementSpeedDamageModifier = blow.MovementSpeedDamageModifier;
            collision.InflictedDamage = blow.InflictedDamage;
            collision.AbsorbedByArmor = (int)blow.AbsorbedByArmor;
            victim.RegisterBlow(blow, in collision);
            // A blow can synchronously remove its victim or end the mission.
            // Query the captured index before reading the native health again.
            if (mission == null || mission != Mission.Current || mission.IsFinalized ||
                mission.FindAgentWithIndex(victimIndex) != victim || !victim.IsActive()) return 0;
            float lost = before - victim.Health;
            return InteractionMath.IsFinite(lost) ? Math.Max(0, lost) : 0;
        }
    }
}
