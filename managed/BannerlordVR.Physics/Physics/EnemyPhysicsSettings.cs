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
        public static readonly bool LivingRagdolls = VrConfig.Bool("enemy_physics_living_ragdoll", false);
        public static readonly bool ActiveBalance = VrConfig.Bool("enemy_physics_active_balance", false);
        // Blade & Sorcery creature drive: every ragdoll body pinned to the
        // animated pose, destabilization, fall and stand-up. Supersedes the
        // pelvis/chest balance when both are enabled.
        public static readonly bool ActiveRagdoll = VrConfig.Bool("enemy_physics_active_ragdoll", false);
        // Runtime probe variant: a virtual hand grabs the neck and lifts it.
        public static readonly bool ActiveProbeGrab = VrConfig.Bool("enemy_physics_active_probe_grab", false);
        // Probe load: total number of enemies running the cycle at once.
        public static readonly int ActiveProbeBodies = Math.Max(1, Math.Min(12, VrConfig.Int("enemy_physics_active_probe_bodies", 1)));
        // Custom battle test range: player on foot, AI frozen, five dummies.
        public static readonly bool Sandbox = VrConfig.Bool("enemy_physics_sandbox", false);
        public static readonly bool RuntimeProbe = VrConfig.Bool("enemy_physics_runtime_probe", false);
        // Logs pushes, ragdoll impacts and parries during normal VR play.
        public static readonly bool Trace = RuntimeProbe || VrConfig.Bool("enemy_physics_trace", false);
        // bodyTurnDirection of the grab handles; off by default (see ActiveRagdollBody).
        // B&S rotation pins through the native torque bridge (enemy_physics_rotation_pins=0 turns them off).
        public static readonly bool RotationPins = VrConfig.Bool("enemy_physics_rotation_pins", true);
        public static readonly bool GrabBodyTurn = VrConfig.Bool("enemy_physics_grab_turn", false);
        // Every hit on a living enemy makes its body physical for the sway.
        public static readonly bool HitPhysics = VrConfig.Bool("enemy_physics_hit_physics", true);
        // Lead the headset may take over a body stopped against an enemy (m); 0 keeps BannerlordVR's walking.
        public static readonly float CloseReach = Read("enemy_physics_close_reach", 1, 0, 1.5f);
        // A body lying on its back gets up with the game's own rise animation.
        // Blend from the physical pose into the animation on release (off:
        // the helmet stayed on the animated head while the body blended).
        public static readonly bool ReleaseBlend = VrConfig.Bool("enemy_physics_release_blend", false);
        public static readonly bool AnimatedRise = VrConfig.Bool("enemy_physics_animated_rise", true);
        // While its body is physical an agent's own capsule stays at the spot
        // it was held at; close it so it does not block the player there.
        public static readonly bool CloseHeldCapsule = VrConfig.Bool("enemy_physics_close_held_capsule", true);
        // A sword thrown point first plants in the enemy it meets.
        public static readonly bool ThrownBladesStick = VrConfig.Bool("enemy_physics_thrown_blades_stick", true);
        public static readonly bool ArmStabDisarm = VrConfig.Bool("enemy_physics_arm_stab_disarm", true);
        public static readonly bool CadenceProbe = VrConfig.Bool("enemy_physics_cadence_probe", false);
        public static readonly bool CadenceRagdoll = VrConfig.Bool("enemy_physics_cadence_ragdoll", false);
        public static readonly bool NeckCuts = VrConfig.Bool("enemy_physics_neck_cut", false);
        public static readonly bool Bleeding = VrConfig.Bool("enemy_physics_bleeding", false);
        public static readonly bool WoundProbe = VrConfig.Bool("enemy_physics_wound_probe", false);
        public static readonly float BudgetMilliseconds = Read("enemy_physics_budget_ms", 1.5f, .1f, 5);
        private static float Read(string key, float fallback, float min, float max)
        {
            float v = VrConfig.Float(key, fallback);
            return InteractionMath.IsFinite(v) ? Math.Max(min, Math.Min(max, v)) : fallback;
        }
    }
}
