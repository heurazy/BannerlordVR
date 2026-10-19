using System;

namespace BannerlordVR.VR.Physics
{
    // Measured CPU work, independent of rendering FPS. Essential releases and
    // validation must still execute when admission work runs out of budget.
    public sealed class AdaptivePhysicsBudget
    {
        private readonly double[] _samples = new double[256];
        private readonly double[] _sorted = new double[256];
        private readonly int _maximum;
        private int _cursor, _count, _overloaded, _headroom;
        public int BodyLimit { get; private set; }
        public double Milliseconds { get; }
        public double Mean { get; private set; }
        public AdaptivePhysicsBudget(int maximumBodies, double milliseconds)
        {
            _maximum = Math.Max(1, Math.Min(12, maximumBodies));
            BodyLimit = _maximum;
            Milliseconds = double.IsNaN(milliseconds) || double.IsInfinity(milliseconds)
                ? 1.5 : Math.Max(.1, Math.Min(5, milliseconds));
        }
        public void Record(double milliseconds)
        {
            if (double.IsNaN(milliseconds) || double.IsInfinity(milliseconds) || milliseconds < 0) return;
            milliseconds = Math.Min(1000, milliseconds);
            _samples[_cursor] = milliseconds;
            _cursor = (_cursor + 1) % _samples.Length;
            _count = Math.Min(_count + 1, _samples.Length);
            Mean = _count == 1 ? milliseconds : Mean * .9 + milliseconds * .1;
            _overloaded = Mean > Milliseconds ? _overloaded + 1 : 0;
            _headroom = Mean < Milliseconds * .6 ? _headroom + 1 : 0;
            if (_overloaded >= 8)
            {
                BodyLimit = Math.Max(1, BodyLimit - 1);
                _overloaded = _headroom = 0;
            }
            else if (_headroom >= 180)
            {
                BodyLimit = Math.Min(_maximum, BodyLimit + 1);
                _overloaded = _headroom = 0;
            }
        }
        public double Percentile(double fraction)
        {
            if (_count == 0) return 0;
            Array.Copy(_samples, _sorted, _count);
            Array.Sort(_sorted, 0, _count);
            if (double.IsNaN(fraction)) fraction = 1;
            int index = (int)Math.Ceiling(Math.Max(0, Math.Min(1, fraction)) * _count) - 1;
            return _sorted[Math.Max(0, index)];
        }
    }
}
