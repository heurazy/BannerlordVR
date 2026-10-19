using System;
using TaleWorlds.Library;

namespace BannerlordVR.VR.Physics
{
    public enum PenetrationProfile { DaggerPierce, SwordPierce, WarpickPierce, Arrow, DaggerSlash, SaberSlash, AxeSlash1H, AxeSlash2H }
    public enum PenetrationMaterial { Flesh, Leather, Chainmail, Plate }

    // Blade & Sorcery Damager penetration joint. The weapon is locked to the
    // part laterally; along the blade a damper without spring brakes motion,
    // with DamagerData.penetrationHeldDamperIn while the depth grows and
    // penetrationHeldDamperOut while it shrinks (penetrationDamper when the
    // item is not held), times the target material's
    // penetrationDamperMultiplier from the DamageModifier catalog.
    public static class PenetrationJoint
    {
        public struct Profile
        {
            public float HeldIn, HeldOut, Loose;
            public bool Pierce;
        }

        public static Profile Get(PenetrationProfile profile)
        {
            switch (profile)
            {
                case PenetrationProfile.DaggerPierce: return new Profile { HeldIn = 500, HeldOut = 250, Loose = 500, Pierce = true };
                case PenetrationProfile.SwordPierce: return new Profile { HeldIn = 1000, HeldOut = 500, Loose = 1250, Pierce = true };
                case PenetrationProfile.WarpickPierce: return new Profile { HeldIn = 500, HeldOut = 1500, Loose = 3000, Pierce = true };
                case PenetrationProfile.Arrow: return new Profile { HeldIn = 750, HeldOut = 1000, Loose = 5000, Pierce = true };
                case PenetrationProfile.DaggerSlash: return new Profile { HeldIn = 10000, HeldOut = 100, Loose = 100 };
                case PenetrationProfile.SaberSlash: return new Profile { HeldIn = 10000, HeldOut = 100, Loose = 100 };
                case PenetrationProfile.AxeSlash1H: return new Profile { HeldIn = 0, HeldOut = 10000, Loose = 5000 };
                default: return new Profile { HeldIn = 0, HeldOut = 100000, Loose = 5000 };
            }
        }

        // DamageModifier_Pierce / PierceFine / Slash / SlashAxe multipliers.
        public static float MaterialMultiplier(PenetrationProfile profile, PenetrationMaterial material)
        {
            bool fine = profile == PenetrationProfile.DaggerPierce;
            bool axe = profile == PenetrationProfile.AxeSlash1H || profile == PenetrationProfile.AxeSlash2H;
            bool pierce = Get(profile).Pierce;
            switch (material)
            {
                case PenetrationMaterial.Flesh: return fine ? .75f : axe ? .9f : pierce ? 1 : .5f;
                case PenetrationMaterial.Leather: return fine ? 1.25f : axe ? 1.4f : pierce ? 1.5f : 1;
                case PenetrationMaterial.Chainmail: return fine ? 2 : axe ? 1.8f : pierce ? 4 : 1;
                default: return fine ? 6 : axe ? 3 : pierce ? 8 : 1;
            }
        }

        // DamageModifier penetrationMinVelocity: slower contacts do not enter.
        public static float MinVelocity(PenetrationProfile profile, PenetrationMaterial material)
        {
            bool axe = profile == PenetrationProfile.AxeSlash1H || profile == PenetrationProfile.AxeSlash2H;
            if (Get(profile).Pierce) return 1;
            if (axe) return material == PenetrationMaterial.Plate ? 4.5f : 3;
            switch (material)
            {
                case PenetrationMaterial.Flesh: return 1.5f;
                case PenetrationMaterial.Leather:
                case PenetrationMaterial.Chainmail: return 3;
                default: return 4;
            }
        }

        public static float Damper(PenetrationProfile profile, PenetrationMaterial material, bool held, bool deepening)
        {
            Profile p = Get(profile);
            float d = held ? (deepening ? p.HeldIn : p.HeldOut) : p.Loose;
            return d * MaterialMultiplier(profile, material);
        }

        // Force on the pierced part along the blade axis: it follows the blade
        // through the damper. The cap keeps the explicit damper stable for the
        // part's mass at one PhysX step of latency.
        public static Vec3 AxialForce(Vec3 axis, Vec3 partVelocity, Vec3 bladeVelocity, float damper, float partMass)
        {
            if (!InteractionMath.IsFinite(axis) || !InteractionMath.IsFinite(partVelocity) || !InteractionMath.IsFinite(bladeVelocity) ||
                !InteractionMath.IsFinite(damper) || damper <= 0 || !InteractionMath.IsFinite(partMass) || partMass <= 0) return Vec3.Zero;
            float length = axis.Length;
            if (length < 1e-4f) return Vec3.Zero;
            axis = axis * (1 / length);
            float relative = InteractionMath.Dot(bladeVelocity - partVelocity, axis);
            float c = Math.Min(damper, partMass * RagdollPinDrive.MaxDampingRate);
            return axis * (relative * c);
        }
    }
}
