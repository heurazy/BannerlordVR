using System;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR.Physics
{
    public struct BodyPoint
    {
        public sbyte Bone;
        public BoneBodyPartType Part;
        public float Radius;
        public Vec3 World;
        public Vec3 Velocity;
    }

    // One record per admitted nearby agent, never one per member of the army.
    public sealed class EnemyBody
    {
        public Agent Agent;
        public Skeleton Skeleton;
        public readonly BodyPoint[] Points = new BodyPoint[16];
        public int Count;
        public float LastHitLeft = -10, LastHitRight = -10;
        public MatrixFrame Frame;
        private bool _sampled;
        public void Clear()
        {
            Agent = null; Skeleton = null; Count = 0; _sampled = false;
        }
        public bool IsCorpse => Agent.State == AgentState.Killed || Agent.State == AgentState.Unconscious;
        public bool HasNativePhysics => (IsCorpse || EnemyPhysicsRuntime.OwnsLivingPhysics(Agent)) &&
            Skeleton.GetCurrentRagdollState() == RagdollState.Active;
        public BodyPoint ReadContact(sbyte bone)
        {
            if (bone < 0 || bone >= Skeleton.GetBoneCount()) return default;
            BoneBodyPartType part = Agent.AgentVisuals.GetBoneTypeData(bone).BodyPartType;
            Vec3 world = Frame.TransformToParent(Skeleton.GetBoneEntitialFrameWithIndex(bone)).origin;
            Vec3 velocity = Agent.Velocity;
            for (int i = 0; i < Count; i++) if (Points[i].Bone == bone) { velocity = Points[i].Velocity; break; }
            return new BodyPoint { Bone = bone, Part = part, World = world, Velocity = velocity };
        }

        public string LastFailure { get; private set; } = "-";
        public bool Bind(Agent agent)
        {
            Agent = agent;
            Skeleton = agent.AgentVisuals?.GetSkeleton();
            LastFailure = "no_skeleton";
            if (Skeleton == null) return false;
            LastFailure = "no_bones";
            Count = 0; _sampled = false;
            LastHitLeft = LastHitRight = -10;
            int bones = Skeleton.GetBoneCount();
            for (sbyte i = 0; i < bones && i >= 0 && Count < Points.Length; i++)
            {
                string name = (Skeleton.GetBoneName(i) ?? "").ToLowerInvariant();
                BoneBodyPartType part;
                float radius;
                if (name == "head") { part = BoneBodyPartType.Head; radius = .13f; }
                else if (name == "neck") { part = BoneBodyPartType.Neck; radius = .09f; }
                else if (name == "spine2" || name == "spine_2") { part = BoneBodyPartType.Chest; radius = .23f; }
                else if (name == "pelvis") { part = BoneBodyPartType.Abdomen; radius = .22f; }
                else if (name == "l_hand" || name == "r_hand" || name == "l_forearm" || name == "r_forearm" ||
                    name == "l_foretwist" || name == "r_foretwist" || name == "l_upperarm_twist" || name == "r_upperarm_twist")
                { part = name[0] == 'l' ? BoneBodyPartType.ArmLeft : BoneBodyPartType.ArmRight; radius = .09f; }
                else if (name == "l_calf" || name == "r_calf" || name == "l_foot" || name == "r_foot" || name == "l_thigh" || name == "r_thigh")
                { part = BoneBodyPartType.Legs; radius = .12f; }
                else continue;
                Points[Count++] = new BodyPoint { Bone = i, Part = part, Radius = radius };
            }
            return Count > 0;
        }

        public bool Sample(float dt)
        {
            var visuals = Agent.AgentVisuals;
            var entity = visuals?.GetEntity();
            if (entity == null) { LastFailure = "no_entity"; return false; }
            if (Agent.IsFadingOut()) { LastFailure = "fading_out"; return false; }
            // Equipment/LOD or scene rebuild may replace the skeleton; discard the cache.
            var current = visuals.GetSkeleton();
            if (current == null || current.Pointer != Skeleton.Pointer) { LastFailure = "skeleton_replaced"; return false; }
            Frame = entity.GetGlobalFrame();
            for (int i = 0; i < Count; i++)
            {
                BodyPoint point = Points[i];
                Vec3 position = Frame.TransformToParent(Skeleton.GetBoneEntitialFrameWithIndex(point.Bone)).origin;
                if (!InteractionMath.IsFinite(position)) { LastFailure = "bone_not_finite"; return false; }
                point.Velocity = _sampled ? InteractionMath.ClampMagnitude((position - point.World) * (1 / dt), 15) : Vec3.Zero;
                point.World = position;
                Points[i] = point;
            }
            _sampled = true;
            return true;
        }

        public static bool Hostile(Agent agent, Agent other) =>
            agent.Team != null && other.Team != null ? agent.Team.IsEnemyOf(other.Team) : agent.IsEnemyOf(other);
        public static bool Eligible(Agent agent, Agent player, bool ownedPhysicalFall = false) =>
            EligibleReason(agent, player, ownedPhysicalFall) == null;
        // Why an agent is not taken by the physics (null when it is).
        public static string EligibleReason(Agent agent, Agent player, bool ownedPhysicalFall = false)
        {
            if (agent == null || agent == player) return "self";
            if (!agent.IsHuman) return "not_human";
            if (agent.MountAgent != null || agent.RiderAgent != null) return "mounted";
            if (agent.State != AgentState.Active && agent.State != AgentState.Killed && agent.State != AgentState.Unconscious) return "state_" + agent.State;
            // Teams decide: the native IsEnemy is false for an agent without a
            // controller (the sandbox dummies), which left them untracked.
            if (!Hostile(agent, player)) return "not_enemy";
            if (agent.IsUsingGameObject || agent.IsInLadderQueue) return "using_object";
            // Scripted movement and combat modes belong to the AI; an agent
            // without one (a sandbox dummy) has none to respect.
            if (agent.Controller == AgentControllerType.AI)
            {
                if (agent.GetScriptedFlags() != Agent.AIScriptedFrameFlags.None) return "scripted_" + agent.GetScriptedFlags();
                if (agent.GetScriptedCombatFlags() != Agent.AISpecialCombatModeFlags.None) return "scripted_combat";
            }
            if (agent.State != AgentState.Active) return null;
            for (int channel = 0; channel < 2; channel++)
            {
                var action = agent.GetCurrentActionType(channel);
                if (action == Agent.ActionCodeType.Mount || action == Agent.ActionCodeType.Dismount ||
                    (action == Agent.ActionCodeType.Fall && !ownedPhysicalFall) || (action >= Agent.ActionCodeType.JumpStart && action <= Agent.ActionCodeType.JumpEndHard) ||
                    (action >= Agent.ActionCodeType.Sit && action <= Agent.ActionCodeType.Rear)) return "action_" + action;
            }
            return null;
        }
    }
}
