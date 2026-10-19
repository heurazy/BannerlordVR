namespace BannerlordVR.VR.Physics
{
    public enum GrabHandle { Hand, Wrist, Shoulder, Torso, Head, Neck, Legs }
    public enum GrabOutcome { Hold, Destabilize, Ungrab }

    // Blade & Sorcery HandleRagdoll.ManagedUpdate with the human handle data
    // (Interactable_Human*.json): lifting the reference part above its
    // original creature-local height plus liftOffset triggers the handle's
    // liftBehaviour, and a head pulled below the original hip height always
    // destabilizes.
    public static class RagdollGrabRules
    {
        // Self: the grabbed part itself; otherwise the root (hips).
        public static bool LiftsSelf(GrabHandle handle) => handle == GrabHandle.Legs;
        public static float LiftOffset(GrabHandle handle) => handle == GrabHandle.Legs ? .1f : .05f;

        public static GrabOutcome LiftBehaviour(GrabHandle handle)
        {
            switch (handle)
            {
                case GrabHandle.Neck:
                case GrabHandle.Legs: return GrabOutcome.Destabilize;
                case GrabHandle.Hand: return GrabOutcome.Hold;
                default: return GrabOutcome.Ungrab;
            }
        }

        // Heights are creature-local, above the creature's ground origin.
        public static GrabOutcome Evaluate(GrabHandle handle, float referenceHeight, float referenceOriginalHeight,
            float headHeight, float rootOriginalHeight)
        {
            if (!InteractionMath.IsFinite(referenceHeight) || !InteractionMath.IsFinite(referenceOriginalHeight) ||
                !InteractionMath.IsFinite(headHeight) || !InteractionMath.IsFinite(rootOriginalHeight)) return GrabOutcome.Ungrab;
            GrabOutcome lift = LiftBehaviour(handle);
            if (lift != GrabOutcome.Hold && referenceHeight > referenceOriginalHeight + LiftOffset(handle)) return lift;
            if (headHeight < rootOriginalHeight) return GrabOutcome.Destabilize;
            return GrabOutcome.Hold;
        }
    }
}
