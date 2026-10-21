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
        public const float MaxLag = .6f, Pad = .012f;
        private const int Samples = 10;
        private static readonly MethodInfo OwnPlate = AccessTools.Method(typeof(VrBlade), "TryGetOwnPlate");
        private static bool _broken, _logged;

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
                foreach (Agent agent in EnemyPhysicsRuntime.TrackedLiving())
                {
                    if (!VrBlade.TryGetEnemyPlate(agent, out VrBlade.Plate plate) || !plate.Valid) continue;
                    moved |= Push(ref world, previous, gripLocal, tipLocal, plate, ref total, agent.Index);
                }
                if (role == HandRole.Main && OwnPlate != null)
                {
                    object[] args = { player, null };
                    if ((bool)OwnPlate.Invoke(null, args) && args[1] is VrBlade.Plate own && own.Valid)
                        moved |= Push(ref world, previous, gripLocal, tipLocal, own, ref total, -1);
                }
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
            VrBlade.Plate plate, ref Vec3 total, int owner)
        {
            Vec3 n = plate.N.NormalizedCopy();
            if (n.Length < .5f) return false;
            // The side the blade was on last frame (its grip, else its middle).
            Vec3 before = previous.TransformToParent((gripLocal + tipLocal) * .5f);
            float side = InteractionMath.Dot(before - plate.Centre, n) >= 0 ? 1 : -1;
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
            if (!_logged)
            {
                _logged = true;
                PhysicsLog.Info("SHIELD blade stopped by " + (owner < 0 ? "the player's shield" : "agent " + owner + "'s shield") +
                    " (hand held back " + need.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + " m)");
            }
            return true;
        }
    }
}
