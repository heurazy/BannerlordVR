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
        public static void Apply(Agent player, EnemyBody body, int pointIndex, Vec3 contact,
                                 Vec3 velocity, MissionWeapon weapon, EquipmentIndex slot, bool thrust)
        {
            BodyPoint point = body.Points[pointIndex];
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
            float armor = body.Agent.GetBaseArmorEffectivenessForBodyPart(point.Part);
            float damage = raw * (point.Part == BoneBodyPartType.Head ? 1.5f : 1) / (1 + Math.Max(0, armor) * .045f);
            var blow = new Blow(player.Index)
            {
                BoneIndex = point.Bone, VictimBodyPart = point.Part,
                GlobalPosition = contact, Direction = direction, SwingDirection = direction,
                DamageType = damageType, StrikeType = thrust ? StrikeType.Thrust : StrikeType.Swing,
                AttackType = AgentAttackType.Standard, BaseMagnitude = raw,
                InflictedDamage = Math.Max(1, Math.Min(150, (int)damage)),
                AbsorbedByArmor = Math.Max(0, raw - damage), DamageCalculated = true,
                DefenderStunPeriod = .2f, MovementSpeedDamageModifier = 1,
                BlowFlag = BlowFlags.None
            };
            blow.WeaponRecord.FillAsMeleeBlow(weapon.IsEmpty ? null : weapon.Item, usage,
                usage == null ? -1 : (int)slot, player.Monster.MainHandItemBoneIndex);
            var collision = AttackCollisionData.GetAttackCollisionDataForDebugPurpose(
                false, false, false, true, false, false, false, false, false, thrust, false, false,
                CombatCollisionResult.StrikeAgent, usage == null ? -1 : (int)slot,
                (int)blow.StrikeType, (int)damageType, point.Bone, point.Part,
                player.Monster.MainHandBoneIndex, Agent.UsageDirection.AttackUp, -1,
                CombatHitResultFlags.NormalHit, 1, 0, 0, .2f, 0, 0, 0, 0,
                Vec3.Up, direction, contact, Vec3.Zero, Vec3.Zero, body.Agent.Velocity, Vec3.Up);
            body.Agent.RegisterBlow(blow, in collision);
        }
    }
}
