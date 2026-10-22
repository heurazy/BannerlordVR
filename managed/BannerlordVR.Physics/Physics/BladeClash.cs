using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using BannerlordVR.VR;
using HarmonyLib;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR.Physics
{
    // B&S weapons are rigidbodies: an enemy's blade is as solid as its shield.
    // After VrCollide the shown blade is tested against the blade of every
    // armed enemy near the player (VrBlade's enemy segment, a capsule); one
    // inside it moves the hand back out along the line between the two
    // blades, to the side the blade was on last frame, by at most MaxLag.
    //
    // BrainModuleMelee.OnWeaponCollision: the attacking creature's weapon
    // meeting the player's weapon or shield at more than recoilMinWeaponSpeed
    // (1 m/s) is a parry: the attack stops and the attacker recoils
    // (TryPush Parry, push level 2 against an enemy: the hand holding the
    // weapon is thrown back). The engine's own hit for that swing (its blade
    // goes on to the body behind the player's guard) bounces off instead.
    internal static class BladeClash
    {
        public const float MaxLag = .6f, PlayerRadius = .015f, RecoilMinWeaponSpeed = 1, ContactCooldown = .25f;
        public const float BounceSeconds = .8f;
        private struct Last { public Vec3 Point; public long At; public Vec3 Normal; public Vec3 Grip, Tip; }
        private static readonly Dictionary<int, Last> Enemy = new Dictionary<int, Last>();
        private static readonly Dictionary<int, long> ContactAt = new Dictionary<int, long>();
        private static readonly Dictionary<Agent, float> ParriedUntil = new Dictionary<Agent, float>();
        private static Mission _mission;
        private static bool _broken;
        private static int _logs;

        public static bool Parried(Agent attacker)
        {
            Mission mission = Mission.Current;
            if (attacker == null || mission == null || !ParriedUntil.TryGetValue(attacker, out float until) || mission.CurrentTime > until) return false;
            // A chained second swing is a new attack: parryable and landing
            // like any other. The window held over it, so it could neither be
            // parried nor do damage (its hit bounced).
            Agent.ActionStage stage = attacker.GetCurrentActionStage(1);
            if (stage == Agent.ActionStage.AttackReady || stage == Agent.ActionStage.AttackQuickReady || stage == Agent.ActionStage.AttackRelease)
            {
                ParriedUntil.Remove(attacker);
                return false;
            }
            return true;
        }

        public static bool Apply(HandRole role, ref MatrixFrame world, MatrixFrame previous, Vec3 gripLocal, Vec3 tipLocal)
        {
            if (_broken || !EnemyPhysicsSettings.SolidWeapons) return false;
            try
            {
                Mission mission = Mission.Current;
                Agent player = mission?.MainAgent;
                if (player == null || (tipLocal - gripLocal).Length < .1f) return false;
                if (mission != _mission) { Enemy.Clear(); ContactAt.Clear(); ParriedUntil.Clear(); _mission = mission; }
                long now = Stopwatch.GetTimestamp();
                // A shield is not pushed aside by the blades it stops (that
                // carried it out of the swing it was blocking): a blade that
                // meets it parries, as a weapon does (BrainModuleMelee).
                if (role == HandRole.Off && player.WieldedOffhandWeapon.CurrentUsageItem?.IsShield == true)
                {
                    ShieldContacts(mission, player, now);
                    return false;
                }
                Vec3 total = Vec3.Zero;
                bool moved = false;
                foreach (Agent agent in EnemyPhysicsRuntime.TrackedLiving())
                {
                    EquipmentIndex slot = agent.GetPrimaryWieldedItemIndex();
                    if (slot == EquipmentIndex.None || agent.Equipment[slot].CurrentUsageItem?.IsMeleeWeapon != true) continue;
                    if (!VrBlade.TryGetEnemyBlade(agent, slot, out VrBlade.Segment blade) || !blade.Valid) continue;
                    moved |= Push(mission, player, agent, slot, ref world, previous, gripLocal, tipLocal, blade, ref total, now);
                }
                return moved;
            }
            catch (Exception ex)
            {
                _broken = true;
                PhysicsLog.Error("Solid enemy weapons failed; blades go through enemy weapons as before.", ex);
                return false;
            }
        }

        private static bool Push(Mission mission, Agent player, Agent agent, EquipmentIndex slot, ref MatrixFrame world, MatrixFrame previous,
            Vec3 gripLocal, Vec3 tipLocal, VrBlade.Segment blade, ref Vec3 total, long now)
        {
            Vec3 a0 = world.TransformToParent(gripLocal), a1 = world.TransformToParent(tipLocal);
            Vec3 b0 = blade.GripWorld, b1 = blade.TipWorld;
            float reach = blade.Radius + PlayerRadius;
            bool known = Enemy.TryGetValue(agent.Index, out Last last) && now - last.At < Stopwatch.Frequency / 4;
            // A parried swing's recoil animation whips the blade back at 20-35
            // m/s some 0.6 s later, through the guard that stopped it: it
            // knocked the player's sword 17 cm aside after every parry. The
            // recoiling blade is not solid.
            if (Parried(agent))
            {
                Enemy[agent.Index] = new Last { Point = b1, At = now, Normal = last.Normal, Grip = b0, Tip = b1 };
                return false;
            }
            Closest(a0, a1, b0, b1, out Vec3 mine, out Vec3 theirs);
            Vec3 gap = mine - theirs;
            // The side the blade was on last frame (a fast blade can cross
            // the other between two frames), else where it is now.
            Vec3 n = known ? last.Normal : gap.NormalizedCopy();
            if (!known || gap.Length > reach * 2)
            {
                Vec3 pa0 = previous.TransformToParent(gripLocal), pa1 = previous.TransformToParent(tipLocal);
                Closest(pa0, pa1, b0, b1, out Vec3 pm, out Vec3 pt);
                if ((pm - pt).Length > 1e-4f) n = (pm - pt).NormalizedCopy();
            }
            if (n.LengthSquared < .25f) return false;
            float depth = reach - InteractionMath.Dot(gap, n);
            Vec3 point = theirs;
            Vec3 enemyVelocity = known ? (point - last.Point) * (float)(Stopwatch.Frequency / (double)Math.Max(1, now - last.At)) : Vec3.Zero;
            if (depth <= 0)
            {
                // A fast swing (20-25 m/s) moves the enemy blade 40 cm a frame:
                // seen on one side then the other, never touching, its parry
                // was missed. Its sweep since last frame counts as a meeting.
                if (known && Sweeps(a0, a1, last.Grip, last.Tip, b0, b1, reach))
                    Contact(mission, player, agent, slot, point, Vec3.Zero, enemyVelocity, now, false);
                Enemy[agent.Index] = new Last { Point = point, At = now, Normal = gap.Length > 1e-4f ? gap.NormalizedCopy() : n, Grip = b0, Tip = b1 };
                return false;
            }
            Vec3 shift = n * depth;
            if ((total + shift).Length > MaxLag) { Enemy.Remove(agent.Index); return false; } // out of reach: it goes through
            total += shift;
            world.origin += shift;
            Enemy[agent.Index] = new Last { Point = point, At = now, Normal = n, Grip = b0, Tip = b1 };
            Contact(mission, player, agent, slot, point, shift, enemyVelocity, now, false);
            return true;
        }

        // The enemy blade's sweep from last frame to this one (sampled at
        // quarters) passes within reach of the player's blade.
        private static bool Sweeps(Vec3 a0, Vec3 a1, Vec3 grip0, Vec3 tip0, Vec3 grip1, Vec3 tip1, float reach)
        {
            for (int i = 1; i < 4; i++)
            {
                float t = i / 4f;
                Closest(a0, a1, grip0 + (grip1 - grip0) * t, tip0 + (tip1 - tip0) * t, out Vec3 m, out Vec3 q);
                if ((m - q).Length <= reach) return true;
            }
            return false;
        }

        private static void Contact(Mission mission, Agent player, Agent agent, EquipmentIndex slot, Vec3 at, Vec3 shift, Vec3 enemyVelocity, long now, bool shield)
        {
            // The impact speed: the enemy blade's own and the player's into it.
            Vec3 impact = enemyVelocity - shift * 60;
            float speed = impact.Length;
            Agent.ActionStage stage = agent.GetCurrentActionStage(1);
            bool attacking = stage == Agent.ActionStage.AttackRelease || stage == Agent.ActionStage.AttackReady ||
                stage == Agent.ActionStage.AttackQuickReady;
            // Every meeting of an attacking blade is judged, whatever touched
            // just before: a guard contact a moment earlier held the parry
            // back for the sound's cooldown and the swing came through.
            if (attacking && speed > RecoilMinWeaponSpeed && !Parried(agent))
            {
                ParriedUntil[agent] = mission.CurrentTime + BounceSeconds;
                EnemyPhysicsRuntime.Parried(agent, impact);
            }
            if (ContactAt.TryGetValue(agent.Index, out long last) && now - last < Stopwatch.Frequency * ContactCooldown) return;
            ContactAt[agent.Index] = now;
            try
            {
                WeaponComponentData mine = shield ? player.WieldedOffhandWeapon.CurrentUsageItem : player.WieldedWeapon.CurrentUsageItem;
                WeaponComponentData theirs = agent.Equipment[slot].CurrentUsageItem;
                if (mine != null && theirs != null)
                    VrCombatSound.Clash(mission, at, VrCombatSound.Material(theirs, false), VrCombatSound.Material(mine, shield), agent.Index, player.Index);
            }
            catch (Exception) { }
            float strength = Math.Min(1, .3f + speed / 8);
            VrHand hand = shield ? VrHandSlots.OffController : VrHandSlots.MainController;
            PhysicsInterop.Haptic(hand == VrHand.Left ? 0 : 1, strength, .04f);
            if (EnemyPhysicsSettings.Trace || _logs < 6)
            {
                _logs++;
                PhysicsLog.Info(string.Format(CultureInfo.InvariantCulture,
                    "CLASH agent={0} stage={1} speed={2:F2} parried={3} hand_held_back={4:F3}", agent.Index, stage, speed,
                    attacking && speed > RecoilMinWeaponSpeed, shift.Length));
            }
        }

        private static readonly MethodInfo OwnPlate = AccessTools.Method(typeof(VrBlade), "TryGetOwnPlate");
        private static void ShieldContacts(Mission mission, Agent player, long now)
        {
            if (OwnPlate == null) return;
            object[] args = { player, null };
            if (!(bool)OwnPlate.Invoke(null, args) || !(args[1] is VrBlade.Plate plate) || !plate.Valid) return;
            Vec3 n = plate.N.NormalizedCopy();
            foreach (Agent agent in EnemyPhysicsRuntime.TrackedLiving())
            {
                EquipmentIndex slot = agent.GetPrimaryWieldedItemIndex();
                if (slot == EquipmentIndex.None || agent.Equipment[slot].CurrentUsageItem?.IsMeleeWeapon != true) continue;
                if (!VrBlade.TryGetEnemyBlade(agent, slot, out VrBlade.Segment blade) || !blade.Valid) continue;
                int key = -1 - agent.Index;
                Vec3 tip = blade.TipWorld;
                bool known = Enemy.TryGetValue(key, out Last last) && now - last.At < Stopwatch.Frequency / 4;
                Vec3 velocity = known ? (tip - last.Point) * (float)(Stopwatch.Frequency / (double)Math.Max(1, now - last.At)) : Vec3.Zero;
                Enemy[key] = new Last { Point = tip, At = now };
                for (int i = 0; i <= 8; i++)
                {
                    Vec3 d = blade.GripWorld + (tip - blade.GripWorld) * (i / 8f) - plate.Centre;
                    if (Math.Abs(InteractionMath.Dot(d, n)) > plate.HalfN + blade.Radius ||
                        Math.Abs(InteractionMath.Dot(d, plate.A)) > plate.HalfA || Math.Abs(InteractionMath.Dot(d, plate.B)) > plate.HalfB) continue;
                    Contact(mission, player, agent, slot, plate.Centre + d, Vec3.Zero, velocity, now, true);
                    break;
                }
            }
        }

        // Closest points between segments p0-p1 and q0-q1.
        private static void Closest(Vec3 p0, Vec3 p1, Vec3 q0, Vec3 q1, out Vec3 onP, out Vec3 onQ)
        {
            Vec3 d1 = p1 - p0, d2 = q1 - q0, r = p0 - q0;
            float a = InteractionMath.Dot(d1, d1), e = InteractionMath.Dot(d2, d2), f = InteractionMath.Dot(d2, r);
            float s, t;
            if (a < 1e-8f && e < 1e-8f) { onP = p0; onQ = q0; return; }
            if (a < 1e-8f) { s = 0; t = Clamp(f / e); }
            else
            {
                float c = InteractionMath.Dot(d1, r);
                if (e < 1e-8f) { t = 0; s = Clamp(-c / a); }
                else
                {
                    float b = InteractionMath.Dot(d1, d2), denom = a * e - b * b;
                    s = denom > 1e-8f ? Clamp((b * f - c * e) / denom) : 0;
                    t = (b * s + f) / e;
                    if (t < 0) { t = 0; s = Clamp(-c / a); }
                    else if (t > 1) { t = 1; s = Clamp((b - c) / a); }
                }
            }
            onP = p0 + d1 * s; onQ = q0 + d2 * t;
        }
        private static float Clamp(float v) => v < 0 ? 0 : v > 1 ? 1 : v;

        // The engine's hit of a parried swing bounces off the player's guard.
        [HarmonyPatch]
        private static class BouncePatch
        {
            private static MethodBase Target => AccessTools.Method(typeof(Mission), "MeleeHitCallback", new[]
            {
                typeof(AttackCollisionData).MakeByRefType(), typeof(Agent), typeof(Agent), typeof(GameEntity),
                typeof(float).MakeByRefType(), typeof(MeleeCollisionReaction).MakeByRefType(), typeof(CrushThroughState),
                typeof(Vec3), typeof(Vec3), typeof(HitParticleResultData).MakeByRefType(), typeof(bool)
            });
            public static bool Prepare() => EnemyPhysicsSettings.SolidWeapons && Target != null;
            public static MethodBase TargetMethod() => Target;
            [HarmonyPriority(Priority.First)]
            public static bool Prefix(Agent attacker, Agent victim, ref MeleeCollisionReaction colReaction, ref HitParticleResultData hitParticleResultData)
            {
                if (victim == null || attacker == null || !victim.IsMainAgent) return true;
                bool parried = Parried(attacker);
                if (EnemyPhysicsSettings.Trace)
                    PhysicsLog.Info("ENEMY_HIT on the player by agent " + attacker.Index + " action=" + attacker.GetCurrentAction(1).GetName() +
                        " stage=" + attacker.GetCurrentActionStage(1) + (parried ? " -> bounced (parried)" : " -> lands"));
                if (!parried) return true;
                colReaction = MeleeCollisionReaction.Bounced;
                hitParticleResultData.Reset();
                if (EnemyPhysicsSettings.Trace) PhysicsLog.Info("CLASH engine hit of parried agent " + attacker.Index + " bounced");
                return false;
            }
        }
    }
}
