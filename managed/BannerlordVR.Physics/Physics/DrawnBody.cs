using System;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR.Physics
{
    // The capsules BannerlordVR's VrCollide stops the blade on
    // (VrPhysicalCombat.BodyOf: bone to child bone on the drawn skeleton,
    // the head a sphere over the neck, a helmet's thickness added by
    // HelmetVolume). Hits are tested on these too: the engine's limb
    // colliders are a second, coarser body that follows the hidden agent,
    // and a swing that the drawn body let through struck them in empty air.
    // In B&S the colliders that stop a blade are the ones it damages.
    internal static class DrawnBody
    {
        private static bool _ready, _broken;
        private static MethodInfo _bodyOf;
        private static FieldInfo _count, _capsules, _a, _b, _radius, _bone;
        private static readonly System.Collections.Generic.Dictionary<int, object> _cache = new System.Collections.Generic.Dictionary<int, object>();
        private static float _cacheTime = -2;

        private static bool Ready()
        {
            if (_ready || _broken) return _ready;
            try
            {
                Type combat = AccessTools.TypeByName("BannerlordVR.VR.VrPhysicalCombat");
                Type body = AccessTools.Inner(combat, "Body"), capsule = AccessTools.Inner(combat, "Capsule");
                _bodyOf = AccessTools.Method(combat, "BodyOf", new[] { typeof(Agent) });
                _count = AccessTools.Field(body, "Count"); _capsules = AccessTools.Field(body, "Capsules");
                _a = AccessTools.Field(capsule, "A"); _b = AccessTools.Field(capsule, "B");
                _radius = AccessTools.Field(capsule, "Radius"); _bone = AccessTools.Field(capsule, "Bone");
                _ready = _bodyOf != null && _count != null && _capsules != null && _a != null && _b != null && _radius != null && _bone != null;
                if (!_ready) { _broken = true; PhysicsLog.Info("DRAWN_BODY BannerlordVR capsules not found; hits use the engine limbs."); }
            }
            catch (Exception ex) { _broken = true; PhysicsLog.Error("DRAWN_BODY setup failed; hits use the engine limbs.", ex); }
            return _ready;
        }

        // True when the drawn capsules answered for this agent (hit or not);
        // false sends the caller back to its other tests.
        public static bool Cast(Agent agent, Vec3 from, Vec3 to, float thickness, out float distance, out sbyte bone, out bool hit)
        {
            distance = 0; bone = -1; hit = false;
            if (!EnemyPhysicsSettings.DrawnHits || !Ready() || agent == null || !agent.IsActive() || agent.AgentVisuals == null) return false;
            try
            {
                float now = Mission.Current != null ? Mission.Current.CurrentTime : -1;
                if (now != _cacheTime) { _cache.Clear(); _cacheTime = now; }
                if (!_cache.TryGetValue(agent.Index, out object body))
                    _cache[agent.Index] = body = _bodyOf.Invoke(null, new object[] { agent });
                if (body == null) return false;
                int count = (int)_count.GetValue(body);
                var capsules = (Array)_capsules.GetValue(body);
                float length = (to - from).Length;
                if (count <= 0 || length < 1e-5f) return count > 0;
                float best = float.MaxValue;
                for (int i = 0; i < count && i < capsules.Length; i++)
                {
                    object c = capsules.GetValue(i);
                    float t = First(from, to, (Vec3)_a.GetValue(c), (Vec3)_b.GetValue(c), (float)_radius.GetValue(c) + thickness);
                    if (t < 0 || t * length >= best) continue;
                    best = t * length; bone = (sbyte)_bone.GetValue(c);
                }
                hit = bone >= 0; distance = hit ? best : 0;
                return true;
            }
            catch (Exception ex)
            {
                _broken = true; _ready = false;
                PhysicsLog.Error("DRAWN_BODY cast failed; hits use the engine limbs.", ex);
                return false;
            }
        }

        // The first fraction of p->q within r of segment ab, or -1.
        public static float First(Vec3 p, Vec3 q, Vec3 a, Vec3 b, float r)
        {
            // Bounding sphere of the capsule against the swept segment.
            if (Distance((a + b) * .5f, p, q) > r + (b - a).Length * .5f) return -1;
            if (Distance(p, a, b) <= r) return 0;
            float s = Closest(p, q, a, b);
            if (Distance(p + (q - p) * s, a, b) > r) return -1;
            // Distance to a capsule is convex along a line: bisect for the
            // entry between the start (outside) and the closest point (inside).
            float lo = 0, hi = s;
            for (int k = 0; k < 12; k++)
            {
                float mid = (lo + hi) * .5f;
                if (Distance(p + (q - p) * mid, a, b) <= r) hi = mid; else lo = mid;
            }
            return hi;
        }

        private static float Distance(Vec3 x, Vec3 a, Vec3 b)
        {
            Vec3 ab = b - a;
            float len = ab.LengthSquared;
            float t = len < 1e-8f ? 0 : Math.Max(0, Math.Min(1, InteractionMath.Dot(x - a, ab) / len));
            return (x - (a + ab * t)).Length;
        }

        // Fraction along p->q closest to segment ab (sampled then refined: the
        // distance is convex, so a ternary search converges).
        private static float Closest(Vec3 p, Vec3 q, Vec3 a, Vec3 b)
        {
            float lo = 0, hi = 1;
            for (int k = 0; k < 24; k++)
            {
                float m1 = lo + (hi - lo) / 3, m2 = hi - (hi - lo) / 3;
                if (Distance(p + (q - p) * m1, a, b) <= Distance(p + (q - p) * m2, a, b)) hi = m2; else lo = m1;
            }
            return (lo + hi) * .5f;
        }
    }
}
