using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace BannerlordVR.VR.Physics
{
    // Where the physics spends the main thread, section by section, and how
    // often the garbage collector ran: one PERF line every 10 s. A section is
    // timed with two Stopwatch reads (some 20 ns each).
    internal static class Perf
    {
        public enum Section { Runtime, Ragdoll, BladePose, Clash, Limbs, EnemyBlade, Count }
        private static readonly string[] Names = { "runtime", "ragdoll", "blade_pose", "clash", "limbs", "enemy_blade" };
        // A call over 2 ms is a dropped VR frame: it is logged when it
        // happens, so the lines around it say what caused it.
        private const double SpikeMs = 2;
        private static long _lastSpike;
        private static readonly long[] Ticks = new long[(int)Section.Count], Calls = new long[(int)Section.Count];
        private static readonly double[] Worst = new double[(int)Section.Count];
        private static long _frames, _windowStart;
        private static int _gc0, _gc1, _gc2;
        private static readonly StringBuilder Line = new StringBuilder(256);
        private const double LogSeconds = 10;

        public static long Start() => Stopwatch.GetTimestamp();

        public static void Stop(Section section, long started)
        {
            long spent = Stopwatch.GetTimestamp() - started;
            int s = (int)section;
            Ticks[s] += spent; Calls[s]++;
            double ms = spent * 1000.0 / Stopwatch.Frequency;
            if (ms > Worst[s]) Worst[s] = ms;
            if (ms > SpikeMs && started - _lastSpike > Stopwatch.Frequency / 4)
            {
                _lastSpike = started;
                PhysicsLog.Info("PERF_SPIKE " + Names[s] + " " + ms.ToString("F2", CultureInfo.InvariantCulture) + " ms");
            }
        }

        // Once per mission tick.
        public static void Frame()
        {
            long now = Stopwatch.GetTimestamp();
            if (_windowStart == 0) { Reset(now); return; }
            _frames++;
            if ((now - _windowStart) < Stopwatch.Frequency * LogSeconds || _frames == 0) return;
            Line.Clear();
            Line.Append("PERF ").Append(_frames).Append(" frames:");
            double total = 0;
            for (int s = 0; s < (int)Section.Count; s++)
            {
                double ms = Ticks[s] * 1000.0 / Stopwatch.Frequency / _frames;
                total += s == (int)Section.Clash || s == (int)Section.EnemyBlade ? 0 : ms; // both run inside blade_pose
                Line.Append(' ').Append(Names[s]).Append('=').Append(ms.ToString("F3", CultureInfo.InvariantCulture))
                    .Append(" ms (").Append(Calls[s]).Append(" calls, worst ").Append(Worst[s].ToString("F2", CultureInfo.InvariantCulture)).Append(')');
            }
            Line.Append("; total ").Append(total.ToString("F3", CultureInfo.InvariantCulture)).Append(" ms/frame; GC gen0 ")
                .Append(GC.CollectionCount(0) - _gc0).Append(" gen1 ").Append(GC.CollectionCount(1) - _gc1).Append(" gen2 ").Append(GC.CollectionCount(2) - _gc2)
                .Append(" in ").Append(((now - _windowStart) / (double)Stopwatch.Frequency).ToString("F0", CultureInfo.InvariantCulture)).Append(" s");
            PhysicsLog.Info(Line.ToString());
            Reset(now);
        }

        private static void Reset(long now)
        {
            _windowStart = now; _frames = 0;
            Array.Clear(Ticks, 0, Ticks.Length); Array.Clear(Calls, 0, Calls.Length); Array.Clear(Worst, 0, Worst.Length);
            _gc0 = GC.CollectionCount(0); _gc1 = GC.CollectionCount(1); _gc2 = GC.CollectionCount(2);
        }
    }
}
