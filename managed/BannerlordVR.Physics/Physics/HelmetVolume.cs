using System;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR.Physics
{
    // BannerlordVR's body obstacles (VrPhysicalCombat.Fill) are capsules on
    // the bones: the head's wraps the bare head, so a blade drawn against an
    // enemy went into the helmet before it met anything. B&S helmets are
    // colliders on the head part. With a head armour on, the head and neck
    // capsules grow by the helmet's thickness, for VrCollide and the
    // physics' drawn-bone tests alike.
    internal static class HelmetVolume
    {
        public const float Margin = .06f;

        public static float HeadMargin(Agent agent)
        {
            if (!EnemyPhysicsSettings.HelmetVolume || agent == null || !agent.IsHuman) return 0;
            try { return agent.SpawnEquipment != null && !agent.SpawnEquipment[EquipmentIndex.Head].IsEmpty ? Margin : 0; }
            catch (Exception) { return 0; }
        }

        [HarmonyPatch]
        internal static class FillPatch
        {
            private static Type _body, _capsule;
            private static FieldInfo _count, _capsules, _radius, _part;

            public static bool Prepare() => AccessTools.TypeByName("BannerlordVR.VR.VrPhysicalCombat") != null;
            public static MethodBase TargetMethod() => AccessTools.Method(AccessTools.TypeByName("BannerlordVR.VR.VrPhysicalCombat"), "Fill");

            [HarmonyPostfix]
            public static void Postfix(object body, Agent agent)
            {
                try
                {
                    float margin = HeadMargin(agent);
                    if (margin <= 0 || body == null) return;
                    if (_body == null)
                    {
                        _body = body.GetType();
                        _count = AccessTools.Field(_body, "Count"); _capsules = AccessTools.Field(_body, "Capsules");
                        _capsule = AccessTools.Inner(AccessTools.TypeByName("BannerlordVR.VR.VrPhysicalCombat"), "Capsule");
                        _radius = AccessTools.Field(_capsule, "Radius"); _part = AccessTools.Field(_capsule, "Part");
                    }
                    if (_count == null || _capsules == null || _radius == null || _part == null) return;
                    int count = (int)_count.GetValue(body);
                    var capsules = (Array)_capsules.GetValue(body);
                    for (int i = 0; i < count && i < capsules.Length; i++)
                    {
                        object capsule = capsules.GetValue(i);
                        var part = (BoneBodyPartType)_part.GetValue(capsule);
                        if (part != BoneBodyPartType.Head && part != BoneBodyPartType.Neck) continue;
                        _radius.SetValue(capsule, (float)_radius.GetValue(capsule) + (part == BoneBodyPartType.Head ? margin : margin * .5f));
                        capsules.SetValue(capsule, i);
                    }
                }
                catch (Exception) { }
            }
        }
    }
}
