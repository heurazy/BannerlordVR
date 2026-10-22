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
    // B&S holds an item by whichever hands grab it, each where it grabbed:
    // let go with one and the other carries it; grab again anywhere along it.
    // BannerlordVR draws the weapon from the main fist and only steers it
    // through the off hand: with the main trigger let go while the off hand
    // held the shaft the weapon was kept but stayed on the empty main fist.
    //
    // From the moment the main hand lets go while the off hand holds, the
    // weapon is placed here from the hands that hold it: each hold is the
    // weapon's frame in that hand, taken when the hand closed on it.
    //  - off hand only: the weapon rides the off hand;
    //  - main hand only (the off hand let go after a regrab): it rides the
    //    main hand where it was taken, on the blade if that is where;
    //  - both: from the main hold, turned about the main hand's point on the
    //    shaft so that the off hand's point on it goes through the off hand.
    // Both hands are then drawn where the controllers are (the two-hand
    // steering of the hands is suspended). The weapon is dropped as before
    // once neither hand holds it, and a weapon change ends it all.
    internal static class WeaponHold
    {
        private static bool _active, _mainHolds, _offHolds, _broken, _haveLast;
        private static string _item;
        private static MatrixFrame _inMain, _inOff, _last;
        private static Vec3 _mainPoint, _offPoint; // weapon-local points on the shaft
        private static FieldInfo _mainLetGo, _held, _entity, _world;
        private static PropertyInfo _gripping;
        private const float RegrabReach = .15f;

        private static bool Ready()
        {
            if (_mainLetGo != null) return true;
            Type grab = AccessTools.TypeByName("BannerlordVR.VR.VrGrab");
            Type two = AccessTools.TypeByName("BannerlordVR.VR.VrTwoHand");
            Type skinned = AccessTools.TypeByName("BannerlordVR.VR.VrSkinnedWeapon");
            _mainLetGo = AccessTools.Field(grab, "_mainLetGo");
            _gripping = AccessTools.Property(two, "Gripping");
            _held = AccessTools.Field(skinned, "_held");
            _entity = AccessTools.Field(skinned, "_entity");
            _world = AccessTools.Field(AccessTools.Inner(skinned, "Held"), "World");
            return _mainLetGo != null && _gripping != null && _held != null && _entity != null && _world != null;
        }

        private static bool MainLetGo => (EquipmentIndex)_mainLetGo.GetValue(null) != EquipmentIndex.None;
        private static bool OffGrips => ((VrHand?)_gripping.GetValue(null)) == VrHandSlots.OffController;

        private static void End(string why)
        {
            if (_active) PhysicsLog.Info("WEAPON_HOLD ended: " + why);
            _active = _mainHolds = _offHolds = false; _item = null;
        }

        [HarmonyPatch]
        private static class DrivePatch
        {
            private static MethodBase Target => AccessTools.Method(AccessTools.TypeByName("BannerlordVR.VR.VrSkinnedWeapon"), "Drive");
            public static bool Prepare() => EnemyPhysicsSettings.FreeHold && Target != null;
            public static MethodBase TargetMethod() => Target;
            public static void Postfix(Agent agent, VrHandSlot slot, MatrixFrame handWorld, bool __result)
            {
                if (_broken || !__result || slot.Role != HandRole.Main || agent == null || !agent.IsMainAgent) return;
                try
                {
                    if (!Ready()) { _broken = true; PhysicsLog.Info("WEAPON_HOLD unavailable: BannerlordVR internals not found."); return; }
                    object held = (_held.GetValue(null) as Array)?.GetValue((int)HandRole.Main);
                    GameEntity entity = (_entity.GetValue(null) as Array)?.GetValue((int)HandRole.Main) as GameEntity;
                    if (held == null || entity == null) { End("no weapon shown"); return; }
                    EquipmentIndex index = agent.GetPrimaryWieldedItemIndex();
                    string item = index == EquipmentIndex.None ? null : agent.Equipment[index].Item?.StringId;
                    if (item == null) { End("weapon gone"); _haveLast = false; return; }
                    MatrixFrame shown = (MatrixFrame)_world.GetValue(held);
                    if (!TryOffHand(agent, out MatrixFrame offHand)) { End("off hand not posed"); _last = shown; _haveLast = true; return; }
                    if (_active && item != _item) End("weapon changed");
                    bool mainLetGo = MainLetGo, offGrips = OffGrips;
                    if (!_active)
                    {
                        if (mainLetGo && offGrips && _haveLast)
                        {
                            _active = true; _item = item; _offHolds = true; _mainHolds = false;
                            TakeOff(offHand, _last);
                            PhysicsLog.Info("WEAPON_HOLD " + item + ": the main hand let go, the off hand carries it");
                        }
                        else { _last = shown; _haveLast = true; return; }
                    }
                    MatrixFrame weapon = _last;
                    // The main hand closes on it again (VrGrab clears its let-go).
                    if (!_mainHolds && !mainLetGo)
                    {
                        _mainHolds = true;
                        if (Reach(handWorld.origin, _last) <= RegrabReach) TakeMain(handWorld, _last);
                        else TakeMain(handWorld, shown); // out of reach: back in the fist
                        PhysicsLog.Info("WEAPON_HOLD " + item + ": the main hand took hold again");
                    }
                    else if (_mainHolds && mainLetGo) { _mainHolds = false; PhysicsLog.Info("WEAPON_HOLD " + item + ": the main hand let go"); }
                    if (!_offHolds && offGrips) { _offHolds = true; TakeOff(offHand, _last); }
                    else if (_offHolds && !offGrips) _offHolds = false;
                    if (!_mainHolds && !_offHolds) { End("both hands let go"); _last = shown; return; }

                    if (_mainHolds)
                    {
                        weapon = handWorld.TransformToParent(in _inMain);
                        if (_offHolds) weapon = Steer(weapon, offHand.origin);
                    }
                    else weapon = offHand.TransformToParent(in _inOff);
                    weapon.rotation.Orthonormalize();
                    _world.SetValue(held, weapon);
                    entity.SetGlobalFrame(in weapon);
                    _last = weapon;
                }
                catch (Exception ex)
                {
                    _broken = true; End("error");
                    PhysicsLog.Error("Free weapon hold failed; weapons are held by the main hand only.", ex);
                }
            }
        }

        // While the weapon is placed here the hands follow their controllers.
        [HarmonyPatch]
        private static class AdjustOffPatch
        {
            private static MethodBase Target => AccessTools.Method(AccessTools.TypeByName("BannerlordVR.VR.VrTwoHand"), "AdjustOff");
            public static bool Prepare() => EnemyPhysicsSettings.FreeHold && Target != null;
            public static MethodBase TargetMethod() => Target;
            public static bool Prefix() => !_active;
        }

        [HarmonyPatch]
        private static class AdjustMainPatch
        {
            private static MethodBase Target => AccessTools.Method(AccessTools.TypeByName("BannerlordVR.VR.VrTwoHand"), "AdjustMain");
            public static bool Prepare() => EnemyPhysicsSettings.FreeHold && Target != null;
            public static MethodBase TargetMethod() => Target;
            public static void Prefix(ref MatrixFrame world, out MatrixFrame __state) => __state = world;
            // The original still records the fist for the off hand's grab.
            public static void Postfix(ref MatrixFrame world, MatrixFrame __state) { if (_active) world = __state; }
        }

        private static bool TryOffHand(Agent agent, out MatrixFrame offHand)
        {
            offHand = MatrixFrame.Identity;
            Skeleton skeleton = agent.AgentVisuals?.GetSkeleton();
            if (skeleton == null || !VrHandSlots.Resolve(agent, skeleton, out VrHandSlot _, out VrHandSlot off) || !off.Valid || off.Bone < 0) return false;
            offHand = agent.AgentVisuals.GetGlobalFrame().TransformToParent(skeleton.GetBoneEntitialFrameWithIndex(off.Bone));
            return true;
        }

        private static Vec3 Axis(MatrixFrame weapon) => weapon.rotation.u.NormalizedCopy();

        // The weapon-local point of the shaft nearest a hand.
        private static Vec3 ShaftPoint(MatrixFrame weapon, Vec3 hand)
        {
            Vec3 axis = Axis(weapon);
            Vec3 on = weapon.origin + axis * Vec3.DotProduct(hand - weapon.origin, axis);
            return weapon.TransformToLocal(on);
        }

        private static float Reach(Vec3 hand, MatrixFrame weapon)
        {
            Vec3 axis = Axis(weapon);
            Vec3 d = hand - weapon.origin;
            return (d - axis * Vec3.DotProduct(d, axis)).Length;
        }

        private static void TakeMain(MatrixFrame hand, MatrixFrame weapon)
        {
            _inMain = hand.TransformToLocal(in weapon);
            _mainPoint = ShaftPoint(weapon, hand.origin);
        }

        private static void TakeOff(MatrixFrame hand, MatrixFrame weapon)
        {
            _inOff = hand.TransformToLocal(in weapon);
            _offPoint = ShaftPoint(weapon, hand.origin);
        }

        // Turns the weapon about the main hand's point on it so that the off
        // hand's point on it lies towards the off hand.
        private static MatrixFrame Steer(MatrixFrame weapon, Vec3 offHand)
        {
            Vec3 pivot = weapon.TransformToParent(_mainPoint);
            Vec3 from = weapon.TransformToParent(_offPoint) - pivot;
            Vec3 to = offHand - pivot;
            if (from.Length < .08f || to.Length < .08f) return weapon;
            from.Normalize(); to.Normalize();
            Vec3 axis = Vec3.CrossProduct(from, to);
            float sin = axis.Length, cos = Vec3.DotProduct(from, to);
            if (sin < 1e-4f) return weapon;
            axis *= 1 / sin;
            float angle = (float)Math.Atan2(sin, cos);
            weapon.rotation.s = Rotate(weapon.rotation.s, axis, angle);
            weapon.rotation.f = Rotate(weapon.rotation.f, axis, angle);
            weapon.rotation.u = Rotate(weapon.rotation.u, axis, angle);
            weapon.origin = pivot + Rotate(weapon.origin - pivot, axis, angle);
            return weapon;
        }

        private static Vec3 Rotate(Vec3 v, Vec3 axis, float angle)
        {
            float c = (float)Math.Cos(angle), s = (float)Math.Sin(angle);
            return v * c + Vec3.CrossProduct(axis, v) * s + axis * (Vec3.DotProduct(axis, v) * (1 - c));
        }
    }
}
