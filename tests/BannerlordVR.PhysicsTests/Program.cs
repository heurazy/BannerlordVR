using System;
using BannerlordVR.VR.Physics;
using TaleWorlds.Library;

internal static class Program
{
    private static int _checks;
    private static void Check(bool condition, string message)
    {
        _checks++;
        if (!condition) throw new Exception(message);
    }
    private static bool Near(float a, float b) => Math.Abs(a - b) < .0001f;
    public static int Main()
    {
        try
        {
            float t;
            Check(InteractionMath.SegmentSphere(new Vec3(-4, 0, 0), new Vec3(4, 0, 0), Vec3.Zero, .1f, out t) && Near(t, .4875f), "Fast hand must not tunnel through small target");
            Check(!InteractionMath.SegmentSphere(new Vec3(-4, .2f, 0), new Vec3(4, .2f, 0), Vec3.Zero, .1f, out t), "Parallel near miss");
            Check(InteractionMath.SegmentSphere(Vec3.Zero, Vec3.Zero, Vec3.Zero, .1f, out t) && t == 0, "Stationary overlap");
            Check(!InteractionMath.SegmentSphere(Vec3.Up, Vec3.Up, Vec3.Zero, .1f, out t), "Stationary outside");
            Check(InteractionMath.SegmentSphere(new Vec3(-1, .1f, 0), new Vec3(1, .1f, 0), Vec3.Zero, .1f, out t) && Near(t, .5f), "Tangent contact");
            Check(!InteractionMath.SegmentSphere(Vec3.Zero, Vec3.Up, Vec3.Zero, -1, out t), "Invalid radius");
            Check(!InteractionMath.SegmentSphere(new Vec3(float.NaN, 0, 0), Vec3.Zero, Vec3.Zero, 1, out t), "Invalid tracked position");
            Check(InteractionMath.ClampMagnitude(new Vec3(float.PositiveInfinity, 0, 0), 2).LengthSquared == 0, "Infinite force rejected");
            Check(Near(InteractionMath.ClampMagnitude(new Vec3(float.MaxValue, float.MaxValue, 0), 2).Length, 2), "Overflow-safe velocity limit");
            Vec3 damped = InteractionMath.SpringForce(new Vec3(1, 0, 0), new Vec3(3, 0, 0), 100, 20, 500);
            Check(Near(damped.x, 40), "Damping opposes body velocity relative to target");
            Check(Near(InteractionMath.SpringForce(new Vec3(1, 0, 0), new Vec3(-3, 0, 0), 100, 20, 500).x, 160), "Approaching target velocity sign");
            Check(Near(InteractionMath.SpringForce(new Vec3(100, 0, 0), Vec3.Zero, 500, 20, 350).Length, 350), "Grab force bounded");
            Check(InteractionMath.SpringForce(Vec3.Zero, Vec3.Zero, 100, 20, 350).LengthSquared == 0, "No drift at equilibrium");
            Check(InteractionMath.SpringForce(Vec3.Up, Vec3.Zero, float.NaN, 20, 350).LengthSquared == 0, "Invalid configuration rejected");
            Vec3 closest = InteractionMath.ClosestPointOnSegment(Vec3.Zero, Vec3.Up, new Vec3(0, 0, 5));
            Check(Near(closest.z, 1), "Closest point clamped to endpoint");
            Vec3 rotated = InteractionMath.RotateTowards(new Vec3(1, 0, 0), Vec3.Up, new Vec3(1, 0, 0), .35f);
            Check(Near(rotated.Length, 1) && Near(rotated.z, (float)Math.Sin(.35)), "Joint angular bound preserves bone length");
            // Moving-target relative sweep: both objects end apart but crossed this frame.
            Check(InteractionMath.SegmentSphere(new Vec3(-2, 0, 0), new Vec3(2, 0, 0), Vec3.Zero, .2f, out t) && Near(t, .45f), "Relative sweep catches moving target");
            var random = new Random(12946);
            Check(InteractionMath.GestureSpeed(Vec3.Zero, Vec3.Zero, Vec3.Up, Vec3.Up, 1, 1, .01f) == 0, "Stationary stage input cannot cause locomotion damage");
            Check(InteractionMath.GestureSpeed(Vec3.Zero, Vec3.Zero, Vec3.Up, new Vec3(0, 1, 0), 1, 1, .1f) > 10, "Wrist-only blade rotation remains an attack");
            Check(InteractionMath.GestureSpeed(Vec3.Zero, Vec3.Up, Vec3.Up, Vec3.Up, 0, 1, 0) == 0, "Paused gesture rejected");
            for (int i = 0; i < 2000; i++)
            {
                float r = .02f + (float)random.NextDouble();
                float before = r + .01f + (float)random.NextDouble() * 10;
                float after = r + .01f + (float)random.NextDouble() * 10;
                Check(InteractionMath.SegmentSphere(new Vec3(-before, 0, 0), new Vec3(after, 0, 0), Vec3.Zero, r, out t)
                    && Math.Abs((-before + (before + after) * t) + r) < .001f, "Earliest surface contact property");
                Vec3 force = InteractionMath.SpringForce(new Vec3(before, after, r), new Vec3(-r, before, after), 500, 80, 350);
                Check(InteractionMath.IsFinite(force) && force.Length <= 350.001f, "Finite bounded spring property");
            }
            Console.WriteLine($"PASS: {_checks} physics checks against the installed TaleWorlds.Library.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
