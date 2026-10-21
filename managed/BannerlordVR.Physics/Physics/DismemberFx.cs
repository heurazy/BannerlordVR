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

        public static void Slice(Agent player, Agent victim, sbyte bone, sbyte parent, Vec3 at, SliceJoint kind, float speed)
        {
            // Hiding the cut part was tried three ways in game on 19 Oct 2026:
            // MBAgentVisuals.ApplySkeletonScale has no visible effect on humans,
            // a per-tick local bone frame is overwritten by the engine, and
            // rebuilding the visuals without the head crashed the game when
            // the corpse went ragdoll. The cut keeps what B&S does besides the
            // separation: the stump bleeds and sliceForceKill kills.
            if (kind == SliceJoint.Neck && EnemyPhysicsSettings.Decapitation && HideHead(victim))
            {
                // The refreshed visuals get a tick to build their skeleton
                // before the kill ragdolls them.
                Pending.Add(new Cut { Player = player, Victim = victim, Bone = bone, Parent = parent, At = at, Kind = kind, Speed = speed, Ticks = 2 });
                return;
            }
            Kill(player, victim, bone, parent, at, kind, speed);
        }

        private struct Cut { public Agent Player, Victim; public sbyte Bone, Parent; public Vec3 At; public SliceJoint Kind; public float Speed; public int Ticks; }
        private static readonly List<Cut> Pending = new List<Cut>();
        public static void Tick()
        {
            for (int i = Pending.Count - 1; i >= 0; i--)
            {
                Cut cut = Pending[i];
                if (--cut.Ticks > 0) { Pending[i] = cut; continue; }
                Pending.RemoveAt(i);
                try { if (cut.Victim != null && cut.Victim.IsActive()) Kill(cut.Player, cut.Victim, cut.Bone, cut.Parent, cut.At, cut.Kind, cut.Speed); }
                catch (Exception ex) { PhysicsLog.Error("Beheading kill failed.", ex); }
            }
        }

        private static void Kill(Agent player, Agent victim, sbyte bone, sbyte parent, Vec3 at, SliceJoint kind, float speed)
        {
            MBAgentVisuals visuals = victim.AgentVisuals;
            try
            {
                MatrixFrame local = MatrixFrame.Identity;
                visuals.CreateParticleSystemAttachedToBone("psys_game_blood_sword_exit", parent, ref local);
            }
            catch (Exception ex) { PhysicsLog.Error("Stump blood particle unavailable.", ex); }
            BloodFx.Burst(BloodFx.Kind.Exit, at, Vec3.Up);
            BoneBodyPartType part = visuals.GetBoneTypeData(bone).BodyPartType;
            if (EnemyPhysicsSettings.Trace)
                PhysicsLog.Info(string.Format(CultureInfo.InvariantCulture, "DISMEMBER agent={0} joint={1} bone={2} speed={3:F2}",
                    victim.Index, kind, bone, speed));
            PhysicalHit.ApplyPenetration(player, victim, bone, part, at, 1000, DamageTypes.Cut);
        }

        // Official equipment refresh with a meshless head item that covers the
        // head (ModuleData/bvr_items.xml). Untested in game; skipped when the
        // item is not registered.
        // The body is let go first (blades, grips, its living ragdoll): the
        // refresh replaces the skeleton they hold.
        private static bool HideHead(Agent agent)
        {
            try
            {
                ItemObject severed = TaleWorlds.ObjectSystem.MBObjectManager.Instance?.GetObject<ItemObject>("bvr_severed_head");
                if (severed == null) { PhysicsLog.Info("DISMEMBER head kept: bvr_severed_head is not registered."); return false; }
                EnemyPhysicsRuntime.LetGo(agent);
                var equipment = new Equipment(agent.SpawnEquipment);
                equipment[EquipmentIndex.Head] = new EquipmentElement(severed);
                agent.UpdateSpawnEquipmentAndRefreshVisuals(equipment);
                PhysicsLog.Info("DISMEMBER head hidden agent=" + agent.Index);
                return true;
            }
            catch (Exception ex) { PhysicsLog.Error("Head removal by equipment refresh failed.", ex); return false; }
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
