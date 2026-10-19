using System;
using System.Collections.Generic;
using System.Globalization;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR.Physics
{
    // Bannerlord cannot cut skinned meshes. The sliced bone is scaled to
    // nothing through the engine's own per-bone skeleton scale (the call it
    // uses for horse proportions), hiding everything skinned to it and its
    // children: head and helmet, forearm and hand, calf and foot. The stump
    // bleeds from the parent bone and the cut kills, as sliceForceKill does.
    internal static class DismemberFx
    {
        private struct Joint { public SliceJoint Kind; public sbyte Bone, Parent, Child; }
        private static readonly Dictionary<Monster, Joint[]> Joints = new Dictionary<Monster, Joint[]>();
        private static readonly Vec3 Vanished = new Vec3(.001f, .001f, .001f);

        private static Joint[] JointsOf(Agent agent, Skeleton skeleton)
        {
            if (Joints.TryGetValue(agent.Monster, out Joint[] cached)) return cached;
            var found = new List<Joint>();
            int count = skeleton.GetBoneCount();
            for (sbyte b = 0; b < count; b++)
            {
                string name = (skeleton.GetBoneName(b) ?? "").ToLowerInvariant();
                SliceJoint kind;
                if (name == "head") kind = SliceJoint.Neck;
                else if (name.EndsWith("forearm")) kind = SliceJoint.Elbow;
                else if (name == "l_hand" || name == "r_hand") kind = SliceJoint.Wrist;
                else if (name.EndsWith("calf")) kind = SliceJoint.Knee;
                else if (name == "l_foot" || name == "r_foot") kind = SliceJoint.Ankle;
                else continue;
                sbyte parent = skeleton.GetParentBoneIndex(b);
                sbyte child = skeleton.GetBoneChildCount(b) > 0 ? skeleton.GetBoneChildAtIndex(b, 0) : (sbyte)-1;
                if (parent < 0) continue;
                found.Add(new Joint { Kind = kind, Bone = b, Parent = parent, Child = child });
            }
            Joint[] joints = found.ToArray();
            Joints[agent.Monster] = joints;
            return joints;
        }

        public static bool TrySlice(Agent player, EnemyBody body, Vec3 contact, Vec3 bladeBase, Vec3 bladeTip, Vec3 velocity,
            PenetrationProfile profile, bool twoHanded, bool thrust)
        {
            Agent victim = body?.Agent;
            if (victim == null || body.IsCorpse || !victim.IsActive() || thrust) return false;
            float speed = velocity.Length;
            if (speed <= Dismemberment.MinVelocity(profile, twoHanded)) return false;
            Skeleton skeleton = body.Skeleton;
            MatrixFrame frame = victim.AgentVisuals.GetEntity().GetGlobalFrame();
            Vec3 bladeUp = bladeTip - bladeBase;
            foreach (Joint joint in JointsOf(victim, skeleton))
            {
                Vec3 at = frame.TransformToParent(skeleton.GetBoneEntitialFrameWithIndex(joint.Bone)).origin;
                Vec3 from = frame.TransformToParent(skeleton.GetBoneEntitialFrameWithIndex(joint.Parent)).origin;
                Vec3 to = joint.Child >= 0 ? frame.TransformToParent(skeleton.GetBoneEntitialFrameWithIndex(joint.Child)).origin : at + (at - from);
                Vec3 axis = to - from;
                if (axis.LengthSquared < 1e-6f) continue;
                Vec3 offset = contact - at;
                float along = Math.Abs(InteractionMath.Dot(offset, axis.NormalizedCopy()));
                if ((offset - axis.NormalizedCopy() * InteractionMath.Dot(offset, axis.NormalizedCopy())).Length > .25f) continue;
                if (!Dismemberment.Slices(profile, twoHanded, false, speed, axis, bladeUp, velocity, along)) continue;
                Slice(player, victim, joint.Bone, joint.Parent, at, joint.Kind, speed);
                return true;
            }
            return false;
        }

        public enum Method { SkeletonScale, LocalFrame, Both, Rebuild }

        // Bannerlord's own equipment refresh, then the body skin again without
        // the head (SkinMask.HeadVisible), the way full helmets hide it.
        // Run on the living agent, as for any equipment change, before the kill.
        public static bool RebuildWithoutHead(Agent agent)
        {
            try
            {
                var equipment = new Equipment(agent.SpawnEquipment);
                equipment[EquipmentIndex.Head] = EquipmentElement.Invalid;
                agent.UpdateSpawnEquipmentAndRefreshVisuals(equipment);
                MBAgentVisuals visuals = agent.AgentVisuals;
                visuals.ClearVisualComponents(false, false);
                agent.Mission.OnEquipItemsFromSpawnEquipment(agent, Agent.CreationType.FromCharacterObj);
                SkinMask mask = equipment.GetSkinMeshesMask() & ~SkinMask.HeadVisible;
                bool female = agent.IsFemale && agent.BodyPropertiesValue.Age >= 14f;
                var skin = new SkinGenerationParams((int)mask, equipment.GetUnderwearType(female), (int)equipment.BodyMeshType,
                    (int)equipment.HairCoverType, (int)equipment.BeardCoverType, (int)equipment.BodyDeformType, true,
                    agent.Character.FaceDirtAmount, agent.IsFemale ? 1 : 0, agent.Character.Race, false, false, 0);
                visuals.AddSkinMeshes(skin, agent.BodyPropertiesValue, true, false);
                return true;
            }
            catch (Exception ex)
            {
                VrLog.Error("Head removal by visual rebuild failed.", ex);
                return false;
            }
        }
        private struct Held { public Agent Agent; public Skeleton Skeleton; public sbyte Bone, Parent; }
        private static readonly List<Held> Maintained = new List<Held>();
        public static float LastPersistedScale { get; private set; } = float.NaN;

        // Re-applied every mission tick: the engine rewrites bone frames from
        // animation or ragdoll bodies, so a one-off local frame would not hold.
        public static void Maintain()
        {
            for (int i = Maintained.Count - 1; i >= 0; i--)
            {
                Held h = Maintained[i];
                if (h.Agent == null || h.Agent.AgentVisuals?.GetSkeleton()?.Pointer != h.Skeleton.Pointer)
                { Maintained.RemoveAt(i); continue; }
                LastPersistedScale = BoneScale(h.Skeleton, h.Bone);
                MatrixFrame parent = h.Skeleton.GetBoneEntitialFrameWithIndex(h.Parent);
                MatrixFrame local = parent.TransformToLocalNonOrthogonal(h.Skeleton.GetBoneEntitialFrameWithIndex(h.Bone));
                local.rotation.Orthonormalize();
                local.rotation.ApplyScaleLocal(Vanished);
                h.Skeleton.SetBoneLocalFrame(h.Bone, local);
            }
        }
        public static void Clear() => Maintained.Clear();

        public static void Slice(Agent player, Agent victim, sbyte bone, sbyte parent, Vec3 at, SliceJoint kind, float speed,
            Method method = Method.Both)
        {
            if (method == Method.Rebuild || (method == Method.Both && kind == SliceJoint.Neck)) RebuildWithoutHead(victim);
            MBAgentVisuals visuals = victim.AgentVisuals;
            if (method == Method.SkeletonScale || method == Method.Both)
                visuals.ApplySkeletonScale(Vec3.One, 0, new[] { bone }, new[] { Vanished });
            if (method == Method.LocalFrame)
                Maintained.Add(new Held { Agent = victim, Skeleton = visuals.GetSkeleton(), Bone = bone, Parent = parent });
            try
            {
                MatrixFrame local = MatrixFrame.Identity;
                visuals.CreateParticleSystemAttachedToBone("psys_game_blood_sword_exit", parent, ref local);
            }
            catch (Exception ex) { VrLog.Error("Stump blood particle unavailable.", ex); }
            BloodFx.Burst(BloodFx.Kind.Exit, at, Vec3.Up);
            BoneBodyPartType part = visuals.GetBoneTypeData(bone).BodyPartType;
            if (EnemyPhysicsSettings.Trace)
                VrLog.Info(string.Format(CultureInfo.InvariantCulture, "DISMEMBER agent={0} joint={1} bone={2} speed={3:F2}",
                    victim.Index, kind, bone, speed));
            // sliceForceKill: infinite damage through the normal blow path.
            PhysicalHit.ApplyPenetration(player, victim, bone, part, at, 1000, DamageTypes.Cut);
        }

        // Scale read back from the skinned skeleton, for the in-game probe.
        public static float BoneScale(Skeleton skeleton, sbyte bone) =>
            skeleton.GetBoneEntitialFrameWithIndex(bone).rotation.s.Length;

        public static bool FindHead(Agent agent, Skeleton skeleton, out sbyte bone, out sbyte parent)
        {
            bone = parent = -1;
            foreach (Joint joint in JointsOf(agent, skeleton))
                if (joint.Kind == SliceJoint.Neck) { bone = joint.Bone; parent = joint.Parent; return true; }
            return false;
        }
    }
}
