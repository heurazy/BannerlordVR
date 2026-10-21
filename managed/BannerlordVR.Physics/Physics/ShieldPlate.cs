using System;
using System.Reflection;
using BannerlordVR.VR;
using HarmonyLib;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR.Physics
{
    // BannerlordVR fits an enemy's shield plate from its mesh and places it
    // on the off-hand bone, with or without the usage frame (plate_frame,
    // guessed at run time); the box often missed the shield drawn, and blades
    // stopped in the air or went through. The shield's own weapon entity is
    // where it is drawn: its local bounding box in its global frame gives
    // the plate (the thinnest axis is the normal). VrCollide and the solid
    // shields both read this plate.
    internal static class ShieldPlate
    {
        private static bool _logged;

        public static bool FromEntity(Agent agent, out VrBlade.Plate plate)
        {
            plate = default(VrBlade.Plate);
            if (agent == null || !agent.IsActive()) return false;
            EquipmentIndex slot = agent.GetOffhandWieldedItemIndex();
            if (slot == EquipmentIndex.None || agent.Equipment[slot].IsEmpty) return false;
            WeaponComponentData usage = agent.Equipment[slot].CurrentUsageItem;
            if (usage == null || !usage.IsShield) return false;
            WeakGameEntity entity = agent.GetWeaponEntityFromEquipmentSlot(slot);
            if (!entity.IsValid) return false;
            BoundingBox box = entity.GetLocalBoundingBox();
            if (!InteractionMath.IsFinite(box.min) || !InteractionMath.IsFinite(box.max) || (box.max - box.min).LengthSquared < 1e-4f) return false;
            MatrixFrame frame = entity.GetGlobalFrame();
            Vec3[] axes = { frame.rotation.s, frame.rotation.f, frame.rotation.u };
            float[] half = { (box.max.x - box.min.x) * .5f, (box.max.y - box.min.y) * .5f, (box.max.z - box.min.z) * .5f };
            for (int i = 0; i < 3; i++)
            {
                float length = axes[i].Length;
                if (length < 1e-4f) return false;
                half[i] *= length; axes[i] = axes[i] * (1 / length);
            }
            int n = half[0] <= half[1] && half[0] <= half[2] ? 0 : half[1] <= half[2] ? 1 : 2;
            int a = (n + 1) % 3, b = (n + 2) % 3;
            plate = new VrBlade.Plate
            {
                Valid = true, Centre = frame.TransformToParent((box.min + box.max) * .5f),
                A = axes[a], B = axes[b], N = axes[n], HalfA = half[a], HalfB = half[b], HalfN = Math.Max(.01f, half[n]),
                Bone = agent.Monster != null ? agent.Monster.OffHandItemBoneIndex : (sbyte)-1, Slot = slot
            };
            if (!_logged)
            {
                _logged = true;
                PhysicsLog.Info(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "SHIELD plate from the shield entity: {0:F2} x {1:F2} x {2:F2} m", plate.HalfA * 2, plate.HalfB * 2, plate.HalfN * 2));
            }
            return true;
        }

        [HarmonyPatch]
        internal static class EnemyPlatePatch
        {
            public static MethodBase TargetMethod() => AccessTools.Method(typeof(VrBlade), "TryGetEnemyPlate");

            [HarmonyPostfix]
            public static void Postfix(Agent agent, ref VrBlade.Plate plate, ref bool __result)
            {
                if (!EnemyPhysicsSettings.Enabled || !EnemyPhysicsSettings.EntityShieldPlates) return;
                try
                {
                    if (FromEntity(agent, out VrBlade.Plate fitted)) { plate = fitted; __result = true; }
                }
                catch (Exception) { }
            }
        }
    }
}
