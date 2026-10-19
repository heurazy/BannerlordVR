using System;
using BannerlordVR.VR.Physics;
using TaleWorlds.Library;

internal static class PenetrationJointTests
{
    public static void Run(Action<bool, string> check)
    {
        check(PenetrationJoint.Damper(PenetrationProfile.SwordPierce, PenetrationMaterial.Flesh, true, true) == 1000 &&
              PenetrationJoint.Damper(PenetrationProfile.SwordPierce, PenetrationMaterial.Flesh, true, false) == 500,
            "Held sword: 1000 going in, 500 coming out (DamagerData SwordPierce)");
        check(PenetrationJoint.Damper(PenetrationProfile.SwordPierce, PenetrationMaterial.Plate, true, true) == 8000 &&
              PenetrationJoint.Damper(PenetrationProfile.SwordPierce, PenetrationMaterial.Chainmail, true, true) == 4000,
            "Armour multiplies the brake (DamageModifier_Pierce plate x8, chainmail x4)");
        check(PenetrationJoint.Damper(PenetrationProfile.DaggerPierce, PenetrationMaterial.Plate, true, true) == 3000,
            "Fine piercing is braked less by plate (PierceFine x6)");
        check(PenetrationJoint.Damper(PenetrationProfile.AxeSlash1H, PenetrationMaterial.Flesh, true, true) == 0 &&
              PenetrationJoint.Damper(PenetrationProfile.AxeSlash1H, PenetrationMaterial.Flesh, true, false) == 9000,
            "An axe bites freely and is hard to pull out");
        check(PenetrationJoint.Damper(PenetrationProfile.SwordPierce, PenetrationMaterial.Flesh, false, true) == 1250,
            "A loose weapon uses penetrationDamper");
        check(PenetrationJoint.MinVelocity(PenetrationProfile.SaberSlash, PenetrationMaterial.Plate) == 4 &&
              PenetrationJoint.MinVelocity(PenetrationProfile.AxeSlash2H, PenetrationMaterial.Plate) == 4.5f &&
              PenetrationJoint.MinVelocity(PenetrationProfile.SwordPierce, PenetrationMaterial.Plate) == 1,
            "Minimum penetration speeds per material");

        Vec3 axis = new Vec3(0, 1, 0);
        Vec3 pull = PenetrationJoint.AxialForce(axis, Vec3.Zero, new Vec3(.5f, -1, 0), 500, 20);
        check(Math.Abs(pull.y + 500) < .01f && Math.Abs(pull.x) < 1e-4f,
            "Withdrawing the blade drags the part along the blade axis only");
        Vec3 capped = PenetrationJoint.AxialForce(axis, Vec3.Zero, new Vec3(0, 1, 0), 8000, 2);
        check(Math.Abs(capped.y - 2 * RagdollPinDrive.MaxDampingRate) < .01f, "A light part's damper is capped for stability");
        check(PenetrationJoint.AxialForce(axis, Vec3.Zero, new Vec3(0, 1, 0), 0, 20).LengthSquared == 0 &&
              PenetrationJoint.AxialForce(Vec3.Zero, Vec3.Zero, Vec3.Up, 500, 20).LengthSquared == 0 &&
              PenetrationJoint.AxialForce(axis, new Vec3(float.NaN, 0, 0), Vec3.Up, 500, 20).LengthSquared == 0,
            "No axial force without damper, axis or valid velocity");

        // Explicit 90 Hz model: a 20 kg torso pierced by a held sword follows
        // a withdrawing blade without overshooting it.
        float v = 0, blade = -1;
        bool bounded = true;
        for (int i = 0; i < 90; i++)
        {
            Vec3 f = PenetrationJoint.AxialForce(axis, new Vec3(0, v, 0), new Vec3(0, blade, 0),
                PenetrationJoint.Damper(PenetrationProfile.SwordPierce, PenetrationMaterial.Flesh, true, false), 20);
            v += f.y / 20 * (1f / 90);
            bounded &= v <= 0 && v >= blade - 1e-4f;
        }
        check(bounded && v < -.9f, "The pierced body converges to the blade speed without overshoot");
    }
}
