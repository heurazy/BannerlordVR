using System;
using System.Collections.Generic;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR.Physics
{
    // Blade & Sorcery: an arrow stuck in you, or in a shield, is an item like
    // any other: it can be grabbed, pulled out, held and thrown. Bannerlord
    // keeps a missile that struck an agent as one of its attached weapons, on
    // a bone (Agent.GetAttachedWeapon and its frame, local to the bone), and
    // one that struck a shield as one of the shield's own (MissionWeapon
    // GetAttachedWeapon, local to the shield's entity; Mission sticks it with
    // Agent.AttachWeaponToWeapon). An empty hand closing on one takes it out
    // and holds it where it was; opening the hand throws it with the hand's
    // speed (a missile of the player's, as VrThrow throws), or drops it.
    internal static class StuckMissiles
    {
        public const float Reach = .12f, ShieldSearch = 2.5f, ThrowSpeed = 3, ThrowGain = 1.5f;
        private static bool _broken;

        private sealed class Held
        {
            public bool Active;
            public MissionWeapon Weapon;
            public GameEntity Entity;
            public MatrixFrame Local;
            public readonly Vec3[] Positions = new Vec3[6];
            public readonly float[] Times = new float[6];
            public int Count;
        }
        private static readonly Held[] Hands = { new Held(), new Held() };

        public static bool Holding(int hand) => hand >= 0 && hand < Hands.Length && Hands[hand].Active;

        private struct Found { public int Index; public float Gap; public MatrixFrame World; public Agent Owner; public EquipmentIndex Shield; }

        public static bool TryPull(Agent player, MatrixFrame hand, int handIndex)
        {
            if (_broken || player == null || Holding(handIndex)) return false;
            try
            {
                var best = new Found { Index = -1, Gap = Reach };
                FromBones(player, hand.origin, ref best);
                FromShield(player, hand.origin, ref best);
                Mission mission = Mission.Current;
                if (mission != null)
                    foreach (Agent agent in mission.GetNearbyAgents(hand.origin.AsVec2, ShieldSearch, new MBList<Agent>()))
                        if (agent != player && agent.IsHuman) FromShield(agent, hand.origin, ref best);
                if (best.Index < 0) return false;

                MissionWeapon pulled;
                string from;
                if (best.Shield == EquipmentIndex.None)
                {
                    pulled = best.Owner.GetAttachedWeapon(best.Index);
                    best.Owner.DeleteAttachedWeapon(best.Index);
                    BloodFx.Burst(BloodFx.Kind.Exit, best.World.origin, Vec3.Up);
                    from = best.Owner == player ? "the player" : "agent " + best.Owner.Index;
                }
                else
                {
                    // No native call takes one missile off a shield: it is
                    // taken from the shield's list and the shield re-equipped,
                    // which draws the arrows left (EquipWeaponWithNewEntity
                    // re-attaches them) and keeps it wielded.
                    MissionWeapon shield = best.Owner.Equipment[best.Shield];
                    pulled = shield.GetAttachedWeapon(best.Index);
                    shield.RemoveAttachedWeapon(best.Index);
                    best.Owner.EquipWeaponWithNewEntity(best.Shield, ref shield);
                    from = (best.Owner == player ? "the player's" : "agent " + best.Owner.Index + "'s") + " shield";
                }
                PhysicsInterop.Haptic(handIndex, .6f, .05f);
                Hold(handIndex, pulled, hand, best.World);
                PhysicsLog.Info("STUCK_MISSILE pulled " + (pulled.Item?.StringId ?? "?") + " out of " + from + " (" +
                    best.Gap.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + " m from the hand); held");
                return true;
            }
            catch (Exception ex)
            {
                _broken = true;
                PhysicsLog.Error("Pulling a stuck missile failed; arrows stay in for this session.", ex);
                return false;
            }
        }

        private static void FromBones(Agent agent, Vec3 hand, ref Found best)
        {
            int count = agent.GetAttachedWeaponsCount();
            if (count == 0 || agent.AgentVisuals == null) return;
            Skeleton skeleton = agent.AgentVisuals.GetSkeleton();
            if (skeleton == null) return;
            MatrixFrame visuals = agent.AgentVisuals.GetGlobalFrame();
            for (int i = 0; i < count; i++)
            {
                sbyte bone = agent.GetAttachedWeaponBoneIndex(i);
                if (bone < 0 || bone >= skeleton.GetBoneCount()) continue;
                MatrixFrame world = visuals.TransformToParent(skeleton.GetBoneEntitialFrameWithIndex(bone)).TransformToParent(agent.GetAttachedWeaponFrame(i));
                Consider(agent.GetAttachedWeapon(i), world, hand, i, agent, EquipmentIndex.None, ref best);
            }
        }

        private static void FromShield(Agent agent, Vec3 hand, ref Found best)
        {
            EquipmentIndex off = agent.GetOffhandWieldedItemIndex();
            if (off == EquipmentIndex.None) return;
            MissionWeapon shield = agent.Equipment[off];
            if (shield.IsEmpty || shield.CurrentUsageItem?.IsShield != true) return;
            int count = shield.GetAttachedWeaponsCount();
            if (count == 0) return;
            WeakGameEntity entity = agent.GetWeaponEntityFromEquipmentSlot(off);
            if (!entity.IsValid) return;
            MatrixFrame frame = entity.GetGlobalFrame();
            for (int i = 0; i < count; i++)
                Consider(shield.GetAttachedWeapon(i), frame.TransformToParent(shield.GetAttachedWeaponFrame(i)), hand, i, agent, off, ref best);
        }

        private static void Consider(MissionWeapon weapon, MatrixFrame world, Vec3 hand, int index, Agent owner, EquipmentIndex shield, ref Found best)
        {
            float length = Math.Max(.3f, (weapon.CurrentUsageItem?.WeaponLength ?? 80) * .01f);
            // The shaft's direction in the attach frame is not known here:
            // the nearest of its axes both ways.
            float gap = float.MaxValue;
            foreach (Vec3 axis in new[] { world.rotation.u, -world.rotation.u, world.rotation.f, -world.rotation.f })
            {
                if (axis.Length < 1e-4f) continue;
                Vec3 dir = axis.NormalizedCopy();
                float t = Math.Max(0, Math.Min(length, InteractionMath.Dot(hand - world.origin, dir)));
                gap = Math.Min(gap, (hand - (world.origin + dir * t)).Length);
            }
            if (gap < best.Gap) best = new Found { Index = index, Gap = gap, World = world, Owner = owner, Shield = shield };
        }

        // The pulled missile in the hand: its own mesh where it was, moved
        // with the hand.
        private static void Hold(int handIndex, MissionWeapon weapon, MatrixFrame hand, MatrixFrame world)
        {
            Held held = Hands[handIndex];
            Clear(held);
            held.Active = true; held.Weapon = weapon; held.Count = 0;
            held.Local = hand.TransformToLocal(world);
            try
            {
                string mesh = !string.IsNullOrEmpty(weapon.Item?.FlyingMeshName) ? weapon.Item.FlyingMeshName : weapon.Item?.MultiMeshName;
                Scene scene = Mission.Current?.Scene;
                MetaMesh copy = string.IsNullOrEmpty(mesh) || scene == null ? null : MetaMesh.GetCopy(mesh, false, true);
                if (copy != null)
                {
                    held.Entity = GameEntity.CreateEmptyDynamic(scene, false);
                    held.Entity.AddMultiMesh(copy);
                    held.Entity.SetGlobalFrame(in world);
                }
            }
            catch (Exception) { held.Entity = null; }
        }

        // Each frame with the hand's pose: the held missile follows, and is
        // let go as the grip opens.
        public static void Tick(Agent player, int handIndex, MatrixFrame hand, bool grip, float now)
        {
            if (!Holding(handIndex)) return;
            Held held = Hands[handIndex];
            int slot = held.Count % held.Positions.Length;
            held.Positions[slot] = hand.origin; held.Times[slot] = now; held.Count++;
            MatrixFrame world = hand.TransformToParent(held.Local);
            if (grip)
            {
                try { held.Entity?.SetGlobalFrame(in world); } catch (Exception) { }
                return;
            }
            Let(player, held, world, handIndex);
        }

        // The hand's speed over its last frames (about 0.06 s).
        private static Vec3 Velocity(Held held)
        {
            int n = Math.Min(held.Count, held.Positions.Length);
            if (n < 2) return Vec3.Zero;
            int last = (held.Count - 1) % held.Positions.Length, first = (held.Count - n) % held.Positions.Length;
            float dt = held.Times[last] - held.Times[first];
            return dt > 1e-3f ? (held.Positions[last] - held.Positions[first]) * (1 / dt) : Vec3.Zero;
        }

        private static void Let(Agent player, Held held, MatrixFrame world, int handIndex)
        {
            MissionWeapon weapon = held.Weapon;
            Vec3 velocity = Velocity(held);
            Clear(held);
            Mission mission = Mission.Current;
            if (mission == null || weapon.IsEmpty || player == null) return;
            try
            {
                weapon.Amount = 1;
                float speed = velocity.Length;
                if (speed >= ThrowSpeed && weapon.CurrentUsageItem != null && weapon.CurrentUsageItem.IsConsumable)
                {
                    Vec3 dir = velocity * (1 / speed);
                    float baseSpeed = Math.Max(1, weapon.GetModifiedMissileSpeedForCurrentUsage());
                    float thrown = Math.Min(baseSpeed, speed * ThrowGain);
                    Mat3 orientation = Mat3.Identity;
                    orientation.f = dir;
                    orientation.Orthonormalize();
                    mission.AddCustomMissile(player, weapon, world.origin, dir, orientation, baseSpeed, thrown, false, null);
                    PhysicsInterop.Haptic(handIndex, .4f, .04f);
                    PhysicsLog.Info(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "STUCK_MISSILE threw {0} at {1:F1} m/s (hand {2:F1} m/s)", weapon.Item?.StringId ?? "?", thrown, speed));
                }
                else
                {
                    var drop = new MatrixFrame(world.rotation, world.origin);
                    mission.SpawnWeaponWithNewEntity(ref weapon, Mission.WeaponSpawnFlags.WithPhysics, drop);
                    PhysicsLog.Info("STUCK_MISSILE dropped " + (weapon.Item?.StringId ?? "?"));
                }
            }
            catch (Exception ex) { PhysicsLog.Error("Letting go of a pulled missile failed.", ex); }
        }

        private static void Clear(Held held)
        {
            held.Active = false; held.Count = 0;
            try { held.Entity?.Remove(79); } catch (Exception) { }
            held.Entity = null;
        }

        // A new mission: the last one's entities are gone with its scene.
        public static void ForgetMission()
        {
            foreach (Held held in Hands) { held.Entity = null; held.Active = false; held.Count = 0; }
        }
    }
}
