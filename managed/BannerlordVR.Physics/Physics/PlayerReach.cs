using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR.Physics
{
    // BannerlordVR walks the player's agent under the headset by teleporting
    // it each tick (VrRoomscale.Step). Stepped into an enemy's capsule, the
    // engine pushes the agent back out and the view goes with it; past
    // roomscale_recentre_m (0.55 m) of lead the view is pulled back onto the
    // body. Next to an enemy the body therefore stops at the capsules'
    // contact while the head and hands lean on into reach, as a B&S player
    // leans past its locomotion capsule.
    internal static class PlayerReach
    {
        private const float Near = .5f;

        // Lead the head may take over the body near an enemy.
        public static float Recentre(float configured)
        {
            float reach = EnemyPhysicsSettings.CloseReach;
            if (reach <= configured || !EnemyPhysicsSettings.Enabled) return configured;
            Agent player = Mission.Current?.MainAgent;
            if (player == null || !player.IsActive()) return configured;
            return EnemyPhysicsRuntime.Clearance(player.Position.AsVec2, Radius(player)) < Near ? reach : configured;
        }

        // Stick locomotion stops at the capsules' contact; the stick then
        // walks the view on into reach (at walking speed, up to CloseReach),
        // the body staying where the engine stopped it.
        private const float LeanSpeed = 1.5f, Contact = .08f;
        public static void StickLean(Agent player, float dt)
        {
            try
            {
                float reach = EnemyPhysicsSettings.CloseReach;
                if (reach <= 0 || player == null || !player.IsActive() || dt <= 0 || dt > .1f) return;
                Vec2 input = player.MovementInputVector;
                if (input.LengthSquared < .04f) return;
                Vec2 at = player.Position.AsVec2;
                if (EnemyPhysicsRuntime.Nearest(at, Radius(player), out Vec2 enemy) > Contact) return;
                Vec3 look = player.LookDirection;
                Vec2 forward = new Vec2(look.x, look.y);
                Vec2 toEnemy = enemy - at;
                if (forward.Length < 1e-3f || toEnemy.Length < 1e-3f) return;
                forward = forward.Normalized(); toEnemy = toEnemy.Normalized();
                Vec2 right = new Vec2(forward.y, -forward.x);
                Vec2 move = right * input.x + forward * input.y;
                float amount = Math.Min(1, move.Length);
                if (amount < 1e-3f || Vec2.DotProduct(move.Normalized(), toEnemy) < .3f) return;
                Vec3 offset = VrCameraDriver.RoomOffset;
                float lead = new Vec2(offset.x, offset.y).Length;
                if (lead >= reach) return;
                Vec2 dir = move.Normalized();
                float step = Math.Min(reach - lead, LeanSpeed * amount * dt);
                Vec3 local = VrCameraDriver.RoomRotation.TransformToLocal(new Vec3(dir.x * step, dir.y * step, 0));
                local.z = 0;
                VrCameraDriver.ConsumeRoomOffset(-local);
            }
            catch (Exception ex)
            {
                PhysicsLog.Error("Stick lean failed.", ex);
            }
        }

        public static float Radius(Agent agent) =>
            agent?.Monster != null && agent.Monster.BodyCapsuleRadius > 0 ? agent.Monster.BodyCapsuleRadius * agent.AgentScale : .37f;

        [HarmonyPatch]
        internal static class StepPatch
        {
            public static MethodBase TargetMethod() =>
                AccessTools.Method(AccessTools.TypeByName("BannerlordVR.VR.VrRoomscale"), "Step");

            public struct Before { public bool Valid; public Vec3 Position; public Vec3 Offset; }

            [HarmonyPrefix]
            public static void Prefix(Agent agent, out Before __state)
            {
                __state = default(Before);
                if (agent == null || EnemyPhysicsSettings.CloseReach <= 0 || !EnemyPhysicsSettings.Enabled) return;
                __state = new Before { Valid = true, Position = agent.Position, Offset = VrCameraDriver.RoomOffset };
            }

            [HarmonyPostfix]
            public static void Postfix(Agent agent, Before __state)
            {
                try
                {
                    if (!__state.Valid || agent == null || !agent.IsActive()) return;
                    Vec3 after = agent.Position;
                    if ((after - __state.Position).AsVec2.LengthSquared < 1e-8f) return;
                    float radius = Radius(agent);
                    float clearance = EnemyPhysicsRuntime.Clearance(after.AsVec2, radius);
                    if (clearance >= 0 || clearance >= EnemyPhysicsRuntime.Clearance(__state.Position.AsVec2, radius)) return;
                    // Into an enemy: the body stays at the contact, the
                    // headset keeps the lead it would have consumed.
                    agent.TeleportToPosition(__state.Position);
                    Vec3 offset = VrCameraDriver.RoomOffset;
                    VrCameraDriver.ConsumeRoomOffset(offset - __state.Offset);
                }
                catch (Exception ex)
                {
                    PhysicsLog.Error("Close reach step failed; roomscale walks as BannerlordVR does.", ex);
                }
            }
        }

        // VrRoomscale.Tick reads RecentreAt once, for the recentre pull.
        [HarmonyPatch]
        internal static class RecentrePatch
        {
            public static MethodBase TargetMethod() =>
                AccessTools.Method(AccessTools.TypeByName("BannerlordVR.VR.VrRoomscale"), "Tick");

            public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                FieldInfo field = AccessTools.Field(AccessTools.TypeByName("BannerlordVR.VR.VrRoomscale"), "RecentreAt");
                MethodInfo hook = AccessTools.Method(typeof(PlayerReach), nameof(Recentre));
                int replaced = 0;
                foreach (CodeInstruction instruction in instructions)
                {
                    yield return instruction;
                    if (field != null && instruction.LoadsField(field)) { replaced++; yield return new CodeInstruction(OpCodes.Call, hook); }
                }
                if (replaced == 0) PhysicsLog.Info("Close reach: VrRoomscale.Tick has no RecentreAt read; the view recentres at its configured lead.");
            }
        }
    }
}
