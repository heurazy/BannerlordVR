using System;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR.Physics
{
    // Blade & Sorcery: an arrow stuck in you is an item like any other: it
    // can be grabbed and pulled out. Bannerlord keeps a missile that struck an
    // agent as one of its attached weapons, on a bone (Agent.GetAttachedWeapon
    // and its frame, local to the bone). An empty hand closing on one takes it
    // out: it is deleted from the body and dropped as a pickable item where
    // the hand is.
    internal static class StuckMissiles
    {
        public const float Reach = .12f;
        private static bool _broken;

        public static bool TryPull(Agent player, Vec3 hand, int handIndex)
        {
            if (_broken || player?.AgentVisuals == null) return false;
            try
            {
                int count = player.GetAttachedWeaponsCount();
                if (count == 0) return false;
                Skeleton skeleton = player.AgentVisuals.GetSkeleton();
                if (skeleton == null) return false;
                MatrixFrame visuals = player.AgentVisuals.GetGlobalFrame();
                int best = -1; float bestGap = Reach; MatrixFrame bestFrame = MatrixFrame.Identity;
                for (int i = 0; i < count; i++)
                {
                    sbyte bone = player.GetAttachedWeaponBoneIndex(i);
                    if (bone < 0 || bone >= skeleton.GetBoneCount()) continue;
                    MatrixFrame world = visuals.TransformToParent(skeleton.GetBoneEntitialFrameWithIndex(bone)).TransformToParent(player.GetAttachedWeaponFrame(i));
                    MissionWeapon weapon = player.GetAttachedWeapon(i);
                    float length = Math.Max(.3f, (weapon.CurrentUsageItem?.WeaponLength ?? 80) * .01f);
                    // The shaft's direction in the attach frame is not known
                    // here: the nearest of its axes both ways.
                    float gap = float.MaxValue;
                    foreach (Vec3 axis in new[] { world.rotation.u, -world.rotation.u, world.rotation.f, -world.rotation.f })
                    {
                        if (axis.Length < 1e-4f) continue;
                        Vec3 dir = axis.NormalizedCopy();
                        float t = Math.Max(0, Math.Min(length, InteractionMath.Dot(hand - world.origin, dir)));
                        gap = Math.Min(gap, (hand - (world.origin + dir * t)).Length);
                    }
                    if (gap < bestGap) { bestGap = gap; best = i; bestFrame = world; }
                }
                if (best < 0) return false;
                MissionWeapon pulled = player.GetAttachedWeapon(best);
                player.DeleteAttachedWeapon(best);
                BloodFx.Burst(BloodFx.Kind.Exit, bestFrame.origin, Vec3.Up);
                PhysicsInterop.Haptic(handIndex, .6f, .05f);
                try
                {
                    var drop = new MatrixFrame(bestFrame.rotation, hand);
                    if (!pulled.IsEmpty) Mission.Current?.SpawnWeaponWithNewEntity(ref pulled, Mission.WeaponSpawnFlags.WithPhysics, drop);
                }
                catch (Exception) { }
                PhysicsLog.Info("STUCK_MISSILE pulled " + (pulled.Item?.StringId ?? "?") + " out of the player (" +
                    bestGap.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + " m from the hand)");
                return true;
            }
            catch (Exception ex)
            {
                _broken = true;
                PhysicsLog.Error("Pulling a stuck missile failed; arrows stay in for this session.", ex);
                return false;
            }
        }
    }
}
