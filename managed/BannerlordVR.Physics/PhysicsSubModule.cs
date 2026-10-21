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

        // A severed limb is folded again after the agents' tick, and checked at
        // the next frame's start (LimbHide).
        protected override void AfterAsyncTickTick(float dt)
        {
            try { BannerlordVR.VR.Physics.LimbHide.LateTick(); } catch (Exception) { }
        }
        protected override void OnApplicationTick(float dt)
        {
            try { BannerlordVR.VR.Physics.LimbHide.FrameStart(); } catch (Exception) { }
        }

        public override void OnMissionBehaviorInitialize(Mission mission)
        {
            try { BannerlordVR.VR.Physics.LimbHide.ForgetAll(); } catch (Exception) { }
            base.OnMissionBehaviorInitialize(mission);
            try
            {
                if (EnemyPhysicsSettings.LivingRagdolls || EnemyPhysicsSettings.RuntimeProbe)
                    mission.AddMissionBehavior(new LivingRagdollMissionLogic());
                if (EnemyPhysicsSettings.Bleeding)
                    mission.AddMissionBehavior(new BleedingMissionLogic());
                if (EnemyPhysicsSettings.Sandbox)
                    mission.AddMissionBehavior(new PhysicsSandbox());
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

    // One damage system per blade: with the physics owning weapon damage,
    // the release's physical strikes are skipped. Switching VrPhysicalCombat
    // off is not enough (VrMelee's instant hits still strike) and its panel
    // turns it back on and saves combat=1.
    [HarmonyPatch(typeof(VrPhysicalCombat), "Strike")]
    internal static class ReleaseStrikePatch
    {
        private static bool _logged;
        [HarmonyPrefix]
        public static bool Prefix()
        {
            if (!EnemyPhysicsSettings.Enabled || !EnemyPhysicsSettings.WeaponDamage) return true;
            if (!_logged) { _logged = true; PhysicsLog.Info("BannerlordVR physical strikes skipped: the enemy physics owns blade damage."); }
            return false;
        }
    }

    // Blows the physics did not deal never reach an enemy it is holding: a
    // blade inside the body, a grip, or a body in physics mode. Covers the
    // engine attacks VrMelee starts from swing gestures.
    [HarmonyPatch(typeof(Agent), nameof(Agent.RegisterBlow))]
    internal static class ProtectHeldVictimPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(Agent __instance, Blow blow)
        {
            if (PhysicalHit.DeliveringOwnBlow || !EnemyPhysicsSettings.Enabled) return true;
            Agent player = Mission.Current?.MainAgent;
            if (player == null || blow.OwnerId != player.Index || !EnemyPhysicsRuntime.Protects(__instance)) return true;
            if (EnemyPhysicsSettings.Trace)
                PhysicsLog.Info("Foreign blow refused on held enemy " + __instance.Index + " (" + blow.InflictedDamage + " damage).");
            return false;
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
