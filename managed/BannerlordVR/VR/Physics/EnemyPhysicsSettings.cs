using System;

namespace BannerlordVR.VR.Physics
{
    public static class EnemyPhysicsSettings
    {
        public static readonly bool Enabled = VrConfig.Bool("enemy_physics", false);
        public static readonly int Capacity = Math.Max(1, Math.Min(12, VrConfig.Int("enemy_physics_max_agents", 6)));
        public static readonly int ScanBudget = Math.Max(16, Math.Min(512, VrConfig.Int("enemy_physics_scan_budget", 128)));
        public static readonly float Radius = Read("enemy_physics_radius", 4, 2, 8);
        public static readonly float ExitRadius = Radius + 1;
        public static readonly float Spring = Read("enemy_physics_grab_spring", 180, 10, 500);
        public static readonly float Damping = Read("enemy_physics_grab_damping", 22, 1, 80);
        public static readonly float MaxForce = Read("enemy_physics_max_force", 350, 20, 800);
        public static readonly float BreakDistance = Read("enemy_physics_break_distance", 0.65f, 0.2f, 1.2f);
        public static readonly bool WeaponDamage = VrConfig.Bool("enemy_physics_weapon_damage", false);
        private static float Read(string key, float fallback, float min, float max)
        {
            float v = VrConfig.Float(key, fallback);
            return InteractionMath.IsFinite(v) ? Math.Max(min, Math.Min(max, v)) : fallback;
        }
    }
}
