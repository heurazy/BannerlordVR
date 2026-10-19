using System;
using TaleWorlds.Engine;
using TaleWorlds.Library;

namespace BannerlordVR.VR.Physics
{
    public static class CompliantPose
    {
        // CCD arm solve. Rotate joints, preserve bone lengths and the engine's root placement.
        // No root translation: the navigation capsule stays under engine ownership.
        public static bool Arm(Skeleton skeleton, sbyte hand, MatrixFrame target)
        {
            if (hand < 0 || hand >= skeleton.GetBoneCount() || !InteractionMath.IsFinite(target.origin)) return false;
            sbyte elbow = skeleton.GetParentBoneIndex(hand);
            if (elbow < 0) return false;
            // Twist bones can intervene; stop rather than rotating a clavicle or spine.
            for (int n = 0; n < 3 && (skeleton.GetBoneName(elbow) ?? "").IndexOf("twist", StringComparison.OrdinalIgnoreCase) >= 0; n++)
                elbow = skeleton.GetParentBoneIndex(elbow);
            if (elbow < 0) return false;
            sbyte shoulder = skeleton.GetParentBoneIndex(elbow);
            if (shoulder < 0) return false;
            string upperName = (skeleton.GetBoneName(shoulder) ?? "").ToLowerInvariant();
            if (!upperName.Contains("upperarm") && !upperName.Contains("upper_arm")) return false;
            Vec3 shoulderPos = skeleton.GetBoneEntitialFrameWithIndex(shoulder).origin;
            Vec3 elbowPos = skeleton.GetBoneEntitialFrameWithIndex(elbow).origin;
            Vec3 handPos = skeleton.GetBoneEntitialFrameWithIndex(hand).origin;
            float reach = (shoulderPos - elbowPos).Length + (elbowPos - handPos).Length;
            target.origin = shoulderPos + InteractionMath.ClampMagnitude(target.origin - shoulderPos, reach * .98f);
            for (int pass = 0; pass < 4; pass++)
            {
                Aim(skeleton, elbow, hand, target.origin, .30f);
                Aim(skeleton, shoulder, hand, target.origin, .30f);
            }
            // Wrist orientation follows the grip; its position comes from the solved chain.
            MatrixFrame wrist = skeleton.GetBoneEntitialFrameWithIndex(hand);
            wrist.rotation = target.rotation;
            WriteEntity(skeleton, hand, wrist);
            return true;
        }

        public static void Aim(Skeleton skeleton, sbyte joint, sbyte endpoint, Vec3 target, float maxAngle)
        {
            MatrixFrame frame = skeleton.GetBoneEntitialFrameWithIndex(joint);
            Vec3 from = skeleton.GetBoneEntitialFrameWithIndex(endpoint).origin - frame.origin;
            Vec3 to = target - frame.origin;
            frame.rotation.s = InteractionMath.RotateTowards(from, to, frame.rotation.s, maxAngle);
            frame.rotation.f = InteractionMath.RotateTowards(from, to, frame.rotation.f, maxAngle);
            frame.rotation.u = InteractionMath.RotateTowards(from, to, frame.rotation.u, maxAngle);
            WriteEntity(skeleton, joint, frame);
        }

        public static void WriteEntity(Skeleton skeleton, sbyte bone, MatrixFrame entityFrame)
        {
            sbyte parent = skeleton.GetParentBoneIndex(bone);
            if (parent < 0) return; // Never drag or rotate the root.
            MatrixFrame local = skeleton.GetBoneEntitialFrameWithIndex(parent).TransformToLocalNonOrthogonal(entityFrame);
            skeleton.SetBoneLocalFrame(bone, local);
            // Rebuilding from locals otherwise loses the placed root (including rider lift).
            for (sbyte i = 0; i < skeleton.GetBoneCount(); i++)
                if (skeleton.GetParentBoneIndex(i) < 0)
                    skeleton.SetBoneLocalFrame(i, skeleton.GetBoneEntitialFrameWithIndex(i));
            skeleton.UpdateEntitialFramesFromLocalFrames();
        }
    }
}
