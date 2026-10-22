using System;
using System.Reflection;
using BannerlordVR.VR;
using HarmonyLib;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR.Physics
{
    // BannerlordVR's VrCollide keeps the hand on the controller and only
    // turns the weapon about the grip (up to collide_max_deg), so a hand
    // pushed into a shield carried the blade through it. In B&S the weapon is
    // a rigidbody held by the hand joint: a shield stops the weapon and the
    // hand with it, and the hand lags the controller. After VrCollide the
    // shown blade is tested against every shield plate near the player (the
    // enemies' and the player's own, for the main hand); one still inside a
    // plate moves the whole hand back out along the plate's normal, to the
    // side the blade was on, by at most MaxLag.
    internal static class ShieldBlock
    {
        public const float MaxLag = 1.2f, Pad = .012f;
        // While a shield holds a blade back, the physics does not strike with
        // it (the controller's blade is behind the shield): per hand role.
        private static readonly long[] BlockedAt = new long[2];
        private static readonly long Fresh = System.Diagnostics.Stopwatch.Frequency / 8;
        public static bool Blocked(HandRole role) =>
            BlockedAt[(int)role] != 0 && System.Diagnostics.Stopwatch.GetTimestamp() - BlockedAt[(int)role] < Fresh;
        private const int Samples = 10;
        // Per enemy agent index: the blade's drive into its shield (world,
        // away from the player), from the last frame the shield held it.
        private static readonly System.Collections.Generic.Dictionary<int, Vec3> PressDepth = new System.Collections.Generic.Dictionary<int, Vec3>();
        private static readonly System.Collections.Generic.Dictionary<int, long> PressAt = new System.Collections.Generic.Dictionary<int, long>();
        public static bool Pressing(int agent, out Vec3 depth)
        {
            depth = Vec3.Zero;
            if (!PressAt.TryGetValue(agent, out long at) || System.Diagnostics.Stopwatch.GetTimestamp() - at > Fresh) return false;
            depth = PressDepth[agent];
            return InteractionMath.IsFinite(depth) && depth.Length > 1e-4f;
        }
        private static readonly MethodInfo OwnPlate = AccessTools.Method(typeof(VrBlade), "TryGetOwnPlate");
        private static bool _broken;
        private static int _logs;

        // world: the hand frame VrCollide produced; previous: last shown frame;
        // gripLocal/tipLocal: the blade in the hand frame.
        public static bool Apply(HandRole role, ref MatrixFrame world, MatrixFrame previous, Vec3 gripLocal, Vec3 tipLocal)
        {
            if (_broken || !EnemyPhysicsSettings.SolidShields) return false;
            try
            {
                Agent player = Mission.Current?.MainAgent;
                if (player == null || (tipLocal - gripLocal).Length < .1f) return false;
                Vec3 total = Vec3.Zero;
                bool moved = false;
                Vec3 eye = player.GetEyeGlobalPosition();
                foreach (Agent agent in EnemyPhysicsRuntime.TrackedLiving())
                {
                    if (!VrBlade.TryGetEnemyPlate(agent, out VrBlade.Plate plate) || !plate.Valid) continue;
                    // An enemy's shield is struck from the player's side (as
                    // VrCollide sides it): a fast blade already past its middle
                    // last frame was pushed out the far side.
                    float side = InteractionMath.Dot(eye - plate.Centre, plate.N.NormalizedCopy()) >= 0 ? 1 : -1;
                    moved |= Push(ref world, previous, gripLocal, tipLocal, plate, ref total, agent.Index, side);
                }
                if (role == HandRole.Main && OwnPlate != null)
                {
                    object[] args = { player, null };
                    if ((bool)OwnPlate.Invoke(null, args) && args[1] is VrBlade.Plate own && own.Valid)
                        moved |= Push(ref world, previous, gripLocal, tipLocal, own, ref total, -1, 0);
                }
                if (moved) BlockedAt[(int)role] = System.Diagnostics.Stopwatch.GetTimestamp();
                return moved;
            }
            catch (Exception ex)
            {
                _broken = true;
                PhysicsLog.Error("Solid shields failed; blades go through shields as before.", ex);
                return false;
            }
        }

        private static bool Push(ref MatrixFrame world, MatrixFrame previous, Vec3 gripLocal, Vec3 tipLocal,
            VrBlade.Plate plate, ref Vec3 total, int owner, float side)
        {
            Vec3 n = plate.N.NormalizedCopy();
            if (n.Length < .5f) return false;
            // The own shield: the side the blade was on last frame.
            if (side == 0)
            {
                Vec3 before = previous.TransformToParent((gripLocal + tipLocal) * .5f);
                side = InteractionMath.Dot(before - plate.Centre, n) >= 0 ? 1 : -1;
            }
            // Held only when the blade comes in through the shield's face: a
            // blade point crosses the front face, inside its outline, between
            // last frame and this one. A blade that went round the edge (or
            // was already behind) is never dragged back through: that was the
            // sword "teleported to the other side".
            bool entered = false;
            for (int i = 0; i <= Samples && !entered; i++)
            {
                Vec3 local = gripLocal + (tipLocal - gripLocal) * (i / (float)Samples);
                Vec3 p0 = previous.TransformToParent(local) - plate.Centre, p1 = world.TransformToParent(local) - plate.Centre;
                float d0 = side * InteractionMath.Dot(p0, n), d1 = side * InteractionMath.Dot(p1, n);
                if (d0 < plate.HalfN || d1 >= plate.HalfN + Pad) continue;
                float t = Math.Abs(d0 - d1) < 1e-5f ? 0 : (d0 - plate.HalfN) / (d0 - d1);
                Vec3 hit = p0 + (p1 - p0) * Math.Max(0, Math.Min(1, t));
                entered = Math.Abs(InteractionMath.Dot(hit, plate.A)) <= plate.HalfA + Pad && Math.Abs(InteractionMath.Dot(hit, plate.B)) <= plate.HalfB + Pad;
            }
            if (!entered) return false;
            float need = 0;
            for (int i = 0; i <= Samples; i++)
            {
                Vec3 p = world.TransformToParent(gripLocal + (tipLocal - gripLocal) * (i / (float)Samples));
                Vec3 d = p - plate.Centre;
                if (Math.Abs(InteractionMath.Dot(d, plate.A)) > plate.HalfA + Pad || Math.Abs(InteractionMath.Dot(d, plate.B)) > plate.HalfB + Pad) continue;
                float depth = plate.HalfN + Pad - side * InteractionMath.Dot(d, n);
                if (depth > 0 && depth < 2 * plate.HalfN + .5f) need = Math.Max(need, depth);
            }
            if (need <= 0) return false;
            Vec3 shift = n * (side * need);
            if ((total + shift).Length > MaxLag) return false; // past the hand's reach: it goes through
            total += shift;
            world.origin += shift;
            // How far the controller drives the blade into this shield: the
            // B&S weapon pushes the shield (and the arm holding it) with that.
            if (owner >= 0) { PressDepth[owner] = -shift; PressAt[owner] = System.Diagnostics.Stopwatch.GetTimestamp(); }
            if (_logs < 6)
            {
                _logs++;
                PhysicsLog.Info("SHIELD blade stopped by " + (owner < 0 ? "the player's shield" : "agent " + owner + "'s shield") +
                    " (hand held back " + need.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + " m, side " + side +
                    ", blade middle " + InteractionMath.Dot(world.TransformToParent((gripLocal + tipLocal) * .5f) - plate.Centre, n).ToString("F2", System.Globalization.CultureInfo.InvariantCulture) +
                    " m off the plate centre along its normal)");
            }
            return true;
        }
    }
}
