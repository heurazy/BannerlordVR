using System;
using System.Diagnostics;
using System.Reflection;
using BannerlordVR.VR;
using HarmonyLib;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR.Physics
{
    // Blade & Sorcery's weapon is a rigidbody held by the player's hand
    // joint: once it is jointed into a part (Damager.Penetrate) the weapon
    // and the virtual hand stay with the part while the controller moves on,
    // and the arm joint pulls the part instead. BannerlordVR draws the hand
    // (arm IK) and the weapon on the hand target that VrCollide.Resolve
    // returns. While a blade is in a body that target is replaced by the
    // controller pose constrained by the penetration joint:
    //  - angular X/Y limited: the blade keeps its direction in the part,
    //    within the shallow-depth limit, turning about the entry point;
    //  - x/y locked: the blade line passes through the entry point;
    //  - z limited: the blade goes no deeper than the joint's limit.
    // The physics keeps working from the raw controller blade, so the part is
    // pulled by the controller as by the B&S arm joint.
    internal static class PhysicalBlade
    {
        private sealed class State
        {
            public MatrixFrame Raw, Shown;
            public long At;
            public bool Pierced;
            public Vec3 Anchor, PartAxis, GripLocal, TipLocal;
            public float AngleLimit, AlongMin;
            // Offset still shown after the blade came out, glided away.
            public Vec3 Residual; public long ResidualAt;
            // A grabbing hand: offset from the controller hand to the
            // grabbed point, set by the physics each tick.
            public Vec3 Grip; public long GripAt;
            // The held blade in the hand frame, from the last physics tick.
            public Vec3 BladeGrip, BladeTip; public long BladeAt;
        }
        private static readonly State[] States = { new State(), new State() };
        private const float MaxSeparation = .9f, GlideSeconds = .12f;
        private static readonly long Fresh = Stopwatch.Frequency / 10;

        // The physics, from the raw controller blade of this frame.
        public static void SetPierce(HandRole role, Vec3 anchor, Vec3 partAxis, float angleLimit, float alongMin, Vec3 rawGrip, Vec3 rawTip)
        {
            State s = States[(int)role];
            if (Stopwatch.GetTimestamp() - s.At > Fresh) { s.Pierced = false; return; }
            s.GripLocal = s.Raw.TransformToLocalNonOrthogonal(rawGrip);
            s.TipLocal = s.Raw.TransformToLocalNonOrthogonal(rawTip);
            s.Anchor = anchor; s.PartAxis = partAxis; s.AngleLimit = angleLimit; s.AlongMin = alongMin;
            s.Pierced = InteractionMath.IsFinite(anchor) && (s.TipLocal - s.GripLocal).Length > .05f;
        }
        public static void SetGrip(HandRole role, Vec3 offset)
        {
            State s = States[(int)role];
            if (!InteractionMath.IsFinite(offset)) return;
            s.Grip = InteractionMath.ClampMagnitude(offset, MaxSeparation); s.GripAt = Stopwatch.GetTimestamp();
        }
        public static void ClearGrip(HandRole role)
        {
            State s = States[(int)role];
            if (s.GripAt == 0) return;
            if (Stopwatch.GetTimestamp() - s.GripAt <= Fresh) { s.Residual = s.Grip; s.ResidualAt = Stopwatch.GetTimestamp(); }
            s.GripAt = 0; s.Grip = Vec3.Zero;
        }
        public static void Clear(HandRole role)
        {
            State s = States[(int)role];
            if (s.Pierced) { s.Residual = s.Shown.origin - s.Raw.origin; s.ResidualAt = Stopwatch.GetTimestamp(); }
            s.Pierced = false;
        }

        // A blade measured on the shown weapon, mapped back to where the
        // controller holds it this frame.
        public static void ToRaw(HandRole role, ref Vec3 grip, ref Vec3 tip)
        {
            State s = States[(int)role];
            if (Stopwatch.GetTimestamp() - s.At > Fresh) return;
            Vec3 g = s.Shown.TransformToLocalNonOrthogonal(grip), t = s.Shown.TransformToLocalNonOrthogonal(tip);
            Vec3 rg = s.Raw.TransformToParent(g), rt = s.Raw.TransformToParent(t);
            if (InteractionMath.IsFinite(rg) && InteractionMath.IsFinite(rt)) { grip = rg; tip = rt; }
        }
        // The shown blade (world) mapped into the hand frame shown this frame.
        public static void LearnBlade(HandRole role, Vec3 grip, Vec3 tip)
        {
            State s = States[(int)role];
            if (Stopwatch.GetTimestamp() - s.At > Fresh) return;
            Vec3 g = s.Shown.TransformToLocalNonOrthogonal(grip), t = s.Shown.TransformToLocalNonOrthogonal(tip);
            if (!InteractionMath.IsFinite(g) || !InteractionMath.IsFinite(t)) return;
            s.BladeGrip = g; s.BladeTip = t; s.BladeAt = Stopwatch.GetTimestamp();
        }
        public static float Separation(HandRole role)
        {
            State s = States[(int)role];
            return Stopwatch.GetTimestamp() - s.At > Fresh ? 0 : (s.Shown.origin - s.Raw.origin).Length;
        }

        private static MatrixFrame Constrain(State s, MatrixFrame raw)
        {
            MatrixFrame w = raw;
            // Angular limit about the entry point.
            Vec3 dir = w.rotation.TransformToParent(s.TipLocal - s.GripLocal);
            if (dir.Length > 1e-4f && s.PartAxis.LengthSquared > .5f)
            {
                dir.Normalize();
                float angle = (float)Math.Acos(Math.Max(-1, Math.Min(1, InteractionMath.Dot(dir, s.PartAxis)))) - s.AngleLimit;
                Vec3 k = Cross(dir, s.PartAxis);
                if (angle > 1e-3f && k.Length > 1e-5f)
                {
                    k.Normalize();
                    w.rotation.s = Rotate(w.rotation.s, k, angle);
                    w.rotation.f = Rotate(w.rotation.f, k, angle);
                    w.rotation.u = Rotate(w.rotation.u, k, angle);
                    w.origin = s.Anchor + Rotate(w.origin - s.Anchor, k, angle);
                }
            }
            // Lateral lock and depth limit.
            Vec3 grip = w.TransformToParent(s.GripLocal), tip = w.TransformToParent(s.TipLocal);
            Vec3 axis = tip - grip;
            if (axis.Length < .05f) return raw;
            axis.Normalize();
            float along = InteractionMath.Dot(s.Anchor - grip, axis);
            Vec3 shift = s.Anchor - (grip + axis * along);
            if (along < s.AlongMin) shift -= axis * (s.AlongMin - along);
            w.origin += shift;
            Vec3 total = w.origin - raw.origin;
            if (total.Length > MaxSeparation) w.origin = raw.origin + total * (MaxSeparation / total.Length);
            return w;
        }

        private static Vec3 Cross(Vec3 a, Vec3 b) => new Vec3(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x);
        private static Vec3 Rotate(Vec3 v, Vec3 k, float angle)
        {
            float c = (float)Math.Cos(angle), sn = (float)Math.Sin(angle);
            return v * c + Cross(k, v) * sn + k * (InteractionMath.Dot(k, v) * (1 - c));
        }

        // VrCollide.Resolve(HandRole role, ref MatrixFrame world): the last
        // step before the arm IK places the hand and weapon.
        [HarmonyPatch]
        internal static class ResolvePatch
        {
            public static MethodBase TargetMethod() =>
                AccessTools.Method(AccessTools.TypeByName("BannerlordVR.VR.VrCollide"), "Resolve");

            [HarmonyPrefix]
            public static void Prefix(HandRole role, ref MatrixFrame world, out MatrixFrame __state) => __state = world;

            [HarmonyPostfix]
            public static void Postfix(HandRole role, ref MatrixFrame world, MatrixFrame __state, ref bool __result)
            {
                try
                {
                    State s = States[(int)role];
                    long now = Stopwatch.GetTimestamp();
                    s.Raw = __state; s.At = now;
                    if (EnemyPhysicsSettings.Enabled && s.Pierced)
                    {
                        // VrCollide would push the blade out of the body it is
                        // jointed into; the joint decides instead.
                        world = Constrain(s, __state);
                        __result = true;
                    }
                    else if (EnemyPhysicsSettings.Enabled && s.GripAt != 0 && now - s.GripAt <= Fresh)
                    {
                        // The hand holds the grabbed part; VrCollide would
                        // keep it out of the body it is holding.
                        world = __state;
                        world.origin += s.Grip;
                        __result = true;
                    }
                    else if (s.ResidualAt != 0)
                    {
                        float t = (float)((now - s.ResidualAt) / (double)Stopwatch.Frequency) / GlideSeconds;
                        if (t >= 1) s.ResidualAt = 0;
                        else world.origin += s.Residual * (1 - t);
                    }
                    // A shield stops the weapon and the hand (B&S), where
                    // VrCollide only turned the weapon about the grip.
                    if (EnemyPhysicsSettings.Enabled && !s.Pierced && s.BladeAt != 0 && now - s.BladeAt <= Fresh &&
                        ShieldBlock.Apply(role, ref world, s.Shown, s.BladeGrip, s.BladeTip))
                        __result = true;
                    s.Shown = world;
                }
                catch (Exception ex)
                {
                    PhysicsLog.Error("Physical blade pose failed; the shown weapon follows the controller.", ex);
                }
            }
        }

        // VrCollide treats only natively hostile agents as solid; the sandbox
        // dummies (no controller) are hostile by team.
        [HarmonyPatch]
        internal static class HostilePatch
        {
            public static MethodBase TargetMethod() =>
                AccessTools.Method(AccessTools.TypeByName("BannerlordVR.VR.VrCollide"), "Hostile");

            [HarmonyPostfix]
            public static void Postfix(Agent player, Agent agent, ref bool __result)
            {
                if (!__result && player != null && agent != null && EnemyBody.Hostile(agent, player)) __result = true;
            }
        }
    }
}
