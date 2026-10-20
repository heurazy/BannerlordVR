using System;
using BannerlordVR.VR;
using BannerlordVR.VR.Physics;
using HarmonyLib;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.View.Screens;

namespace BannerlordVR.Physics
{
    // Enemy physics as its own module, loaded after an unmodified BannerlordVR
    // release. It reads the release's public VR state (hand poses, config,
    // mission phase) and adds only its own behaviours and two Harmony patches.
    public sealed class PhysicsSubModule : MBSubModuleBase
    {
        public const string HarmonyId = "com.bannerlordvr.physics";
        private Harmony _harmony;

        protected override void OnSubModuleLoad()
        {
            base.OnSubModuleLoad();
            PhysicsLog.Initialize();
            PhysicsLog.Info("OnSubModuleLoad; BannerlordVR " + typeof(VrSystem).Assembly.GetName().Version);
            try
            {
                _harmony = new Harmony(HarmonyId);
                _harmony.PatchAll(typeof(PhysicsSubModule).Assembly);
                PhysicsLog.Info("Harmony patches applied.");
            }
            catch (Exception ex) { PhysicsLog.Error("Harmony patches failed; enemy physics stays off.", ex); }
            if (EnemyPhysicsSettings.LivingRagdolls || EnemyPhysicsSettings.RuntimeProbe)
            {
                if (PhysicsInterop.LoadPhysicsNative()) NativeRagdollBridge.Initialize();
            }
        }

        public override void OnMissionBehaviorInitialize(Mission mission)
        {
            base.OnMissionBehaviorInitialize(mission);
            try
            {
                if (EnemyPhysicsSettings.LivingRagdolls || EnemyPhysicsSettings.RuntimeProbe)
                    mission.AddMissionBehavior(new LivingRagdollMissionLogic());
                if (EnemyPhysicsSettings.Bleeding)
                    mission.AddMissionBehavior(new BleedingMissionLogic());
                if (EnemyPhysicsSettings.RuntimeProbe && EnemyPhysicsSettings.CadenceProbe)
                    mission.AddMissionBehavior(new PhysicsCadenceProbe());
            }
            catch (Exception ex) { PhysicsLog.Error("Enemy physics behaviours could not be added to this mission.", ex); }
        }
    }

    // Same seam the release ticks its hands on; after it, so this frame's
    // hand poses are already in place.
    [HarmonyPatch(typeof(MissionScreen), "AfterMissionTick", new[] { typeof(Mission), typeof(float) })]
    internal static class PhysicsTickPatch
    {
        [HarmonyPostfix]
        [HarmonyAfter("com.bannerlordvr.mod")]
        public static void Postfix(Mission __0, float __1)
        {
            try
            {
                EnemyPhysicsRuntime.Tick(__0, __1);
            }
            catch (Exception ex)
            {
                PhysicsLog.Error("Enemy physics tick failed; resetting.", ex);
                try { EnemyPhysicsRuntime.Reset(); } catch { }
            }
        }
    }

    // While a VR hand closes a fist or draws a blade, its trigger belongs to
    // the physics, not to the game's attack key.
    [HarmonyPatch(typeof(VrHolster), "TriggerKeyAllowed")]
    internal static class TriggerOwnershipPatch
    {
        [HarmonyPostfix]
        public static void Postfix(bool left, ref bool __result)
        {
            if (__result && EnemyPhysicsRuntime.OwnsTrigger(left)) __result = false;
        }
    }
}
