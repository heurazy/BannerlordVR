using System;

namespace BannerlordVR.VR.Physics
{
    public enum LivingRagdollPhase { Idle, Activating, Simulating, Recovering, Faulted }
    public interface ILivingRagdollHost
    {
        bool Exists { get; }
        bool Alive { get; }
        bool Safe { get; }
        bool Paused { get; set; }
        bool NativeActive { get; }
        bool NativeDisabled { get; }
        void BeginNative();
        void EndNative();
        void SuppressMovement();
        void ResumeDecisions();
    }

    // Explicit ownership of activation and of the AI pause bit. The engine's
    // deactivation is asynchronous; requesting EndNative is not proof of recovery.
    public sealed class LivingRagdollLease
    {
        private ILivingRagdollHost _host;
        private bool _changedPause, _began, _ended;
        private float _started, _releaseRequested, _heldUntil;
        public LivingRagdollPhase Phase { get; private set; }
        public bool Occupied => _host != null;
        public bool OwnsPhysics => _began && !_ended && Phase == LivingRagdollPhase.Simulating;
        public bool Faulted { get; private set; }
        public bool Begin(ILivingRagdollHost host, float now)
        {
            if (Occupied || Faulted || !InteractionMath.IsFinite(now) || host == null ||
                !host.Exists || !host.Alive || !host.Safe || !host.NativeDisabled) return false;
            _host = host; _started = now; _heldUntil = now + .12f;
            _changedPause = !host.Paused; _began = _ended = false;
            Phase = LivingRagdollPhase.Activating;
            try
            {
                if (_changedPause) host.Paused = true;
                if (_changedPause) host.SuppressMovement();
                // Mark ownership first: a managed failure after native activation
                // must still execute the matching EndNative path.
                _began = true;
                host.BeginNative();
                return true;
            }
            catch
            {
                EmergencyStop();
                throw;
            }
        }
        public void KeepHeld(float now, float graceSeconds = .12f)
        {
            if (Occupied && !_ended && InteractionMath.IsFinite(now) && InteractionMath.IsFinite(graceSeconds))
                _heldUntil = Math.Max(_heldUntil, now + Math.Max(.05f, Math.Min(1, graceSeconds)));
        }
        public void Tick(float now)
        {
            if (!Occupied) return;
            if (!_host.Exists) { Forget(); return; }
            if (!_host.Alive)
            {
                // A real death transfers ragdoll ownership to the game. Ending
                // the lease must not deactivate the newly created corpse.
                RestorePause(); Forget(); return;
            }
            if (!InteractionMath.IsFinite(now)) { RequestRelease(_started); return; }
            if (!_ended && (!_host.Safe || (_changedPause && !_host.Paused) || now > _heldUntil || now - _started > 6)) RequestRelease(now);
            if (!Occupied) return;
            if (Phase == LivingRagdollPhase.Activating)
            {
                if (_host.NativeActive) Phase = LivingRagdollPhase.Simulating;
                else if (now - _started > .35f) RequestRelease(now);
            }
            if (Phase == LivingRagdollPhase.Simulating && _host.NativeDisabled) RequestRelease(now);
            if (Phase == LivingRagdollPhase.Recovering)
            {
                if (_host.NativeDisabled) { RestorePause(); Forget(); return; }
                if (now - _releaseRequested > 1.5f)
                {
                    // Never leave an AI paused forever on an unverified engine
                    // recovery. Stop this backend for the rest of the mission.
                    Faulted = true; RestorePause(); Forget(); Phase = LivingRagdollPhase.Faulted;
                    return;
                }
            }
            if (_changedPause && Phase != LivingRagdollPhase.Idle) _host.SuppressMovement();
        }
        public void RequestRelease(float now)
        {
            if (!Occupied || _ended) return;
            _ended = true; _releaseRequested = now;
            Phase = LivingRagdollPhase.Recovering;
            if (_began && _host.Exists && _host.Alive) _host.EndNative();
        }
        public void EmergencyStop()
        {
            if (!Occupied) return;
            try { RequestRelease(_started); }
            finally { try { RestorePause(); } finally { Forget(); } }
        }
        // Use only after deletion: no method may touch the invalid native agent.
        public void AbandonDeleted() => Forget();
        private void RestorePause()
        {
            if (_changedPause && _host.Exists)
            {
                _host.Paused = false;
                if (_host.Alive) _host.ResumeDecisions();
            }
            _changedPause = false;
        }
        private void Forget()
        {
            _host = null; _changedPause = _began = _ended = false;
            Phase = LivingRagdollPhase.Idle;
        }
    }
}
