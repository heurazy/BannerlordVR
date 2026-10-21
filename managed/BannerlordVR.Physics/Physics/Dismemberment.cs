using System;
using TaleWorlds.Library;

namespace BannerlordVR.VR.Physics
{
    public enum SliceJoint { Neck, Elbow, Wrist, Knee, Ankle }

    // Blade & Sorcery Damager dismemberment. A cutting hit (penetration Hit,
    // not a thrust) faster than DamagerData.dismembermentMinVelocity slices a
    // part when Damager.CheckSlice passes: the contact lies within the part's
    // sliceWidth of its slice point, and the slice axis is within
    // dismemberment{Vertical,Horizontal}Angle (30 deg) of perpendicular to the
    // blade (up) and to its edge (forward). Every human part has
    // sliceForceKill: the cut kills (Creature_HumanMale ragdoll data).
    // B&S knockout uses DamageModifier allowKnockout / knockoutAllowedMinVelocity;
    // the human brains set knockoutDuration 0, so a knockout is a forced fall.
    public static class Dismemberment
    {
        // Damager_SwordSlash1H tiers: dismembermentMaxVerticalAngle 90 (the
        // blade's tilt never refuses a slice), dismembermentMaxHorizontalAngle
        // 35 (the cut's direction across the limb). 30 for both refused nearly
        // every neck cut.
        public const float MaxVerticalAngle = 90, MaxHorizontalAngle = 35, SliceWidth = .12f;

        // Damager_*.json dismembermentMinVelocity; 0 when the damager cannot slice.
        public static float MinVelocity(PenetrationProfile profile, bool twoHanded)
        {
            switch (profile)
            {
                case PenetrationProfile.SaberSlash: return twoHanded ? 11 : 12;
                case PenetrationProfile.DaggerSlash: return 13;
                case PenetrationProfile.AxeSlash1H:
                case PenetrationProfile.AxeSlash2H: return 9;
                default: return 0;
            }
        }

        public static bool CheckSlice(Vec3 sliceAxis, Vec3 bladeUp, Vec3 bladeForward, float contactDistance)
        {
            if (!InteractionMath.IsFinite(sliceAxis) || !InteractionMath.IsFinite(bladeUp) || !InteractionMath.IsFinite(bladeForward) ||
                !InteractionMath.IsFinite(contactDistance) || contactDistance >= SliceWidth) return false;
            if (sliceAxis.LengthSquared < 1e-8f || bladeUp.LengthSquared < 1e-8f || bladeForward.LengthSquared < 1e-8f) return false;
            // Damager.CheckSlice compares the dot products with angle / 90.
            Vec3 axis = sliceAxis.NormalizedCopy();
            return Math.Abs(InteractionMath.Dot(axis, bladeUp.NormalizedCopy())) < MaxVerticalAngle / 90 &&
                   Math.Abs(InteractionMath.Dot(axis, bladeForward.NormalizedCopy())) < MaxHorizontalAngle / 90;
        }

        public static bool Slices(PenetrationProfile profile, bool twoHanded, bool thrust, float speed,
            Vec3 sliceAxis, Vec3 bladeUp, Vec3 bladeForward, float contactDistance)
        {
            float min = MinVelocity(profile, twoHanded);
            return !thrust && min > 0 && InteractionMath.IsFinite(speed) && speed > min &&
                CheckSlice(sliceAxis, bladeUp, bladeForward, contactDistance);
        }

        // DamageModifier_Blunt allowKnockout rows (Flesh 4, Leather 14,
        // Chainmail 14, Plate 13 m/s); light blunts and punches disallow it.
        public static bool KnocksOut(PushMaterial material, PenetrationMaterial armour, BodyZone zone, float speed)
        {
            if (material != PushMaterial.Blunt || zone != BodyZone.Head || !InteractionMath.IsFinite(speed)) return false;
            float min;
            switch (armour)
            {
                case PenetrationMaterial.Flesh: min = 4; break;
                case PenetrationMaterial.Plate: min = 13; break;
                default: min = 14; break;
            }
            return speed >= min;
        }
    }
}
