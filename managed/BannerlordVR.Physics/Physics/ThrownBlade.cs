using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR.Physics
{
    // A sword has no thrown usage: BannerlordVR's VrThrow flies a copy of it
    // (Fly: gravity, spin, terrain ray), lets it rest, then glides it back to
    // the holster. B&S throws the item itself, and its tip damager pierces
    // whatever it meets point first (thrown multipliers). Here the flight's
    // blade is swept against the tracked bodies each step; a point-first hit
    // fast enough to enter plants it: the copy is fixed in the struck bone,
    // the pierce deals its deep damage and pushes, an arm disarms, and the
    // blade stays until the weapon is drawn again (VrThrow ends the flight).
    internal static class ThrownBlade
    {
        private sealed class Stuck { public Agent Agent; public sbyte Bone; public MatrixFrame Local; public float At; }
        private static readonly Dictionary<object, Stuck> StuckFlights = new Dictionary<object, Stuck>();
        private static Type _flightType;
        private static FieldInfo _frame, _velocity, _phase, _phaseAt, _itemId, _entity, _flights;
        private static bool _ready, _broken, _logged;
        // Blade axis of the flown copy (the weapon mesh frame): +u from the
        // grip, as for the game's weapon entities.
        private const float PointFirst = .7f, Depth = .18f, MaxPlanted = 30;

        private static bool Ready()
        {
            if (_ready || _broken) return _ready;
            Type throwType = AccessTools.TypeByName("BannerlordVR.VR.VrThrow");
            _flightType = throwType == null ? null : AccessTools.Inner(throwType, "Flight");
            if (_flightType == null) { _broken = true; return false; }
            _frame = AccessTools.Field(_flightType, "Frame"); _velocity = AccessTools.Field(_flightType, "Velocity");
            _phase = AccessTools.Field(_flightType, "Phase"); _phaseAt = AccessTools.Field(_flightType, "PhaseAt");
            _itemId = AccessTools.Field(_flightType, "ItemId"); _entity = AccessTools.Field(_flightType, "Entity");
            _flights = AccessTools.Field(throwType, "_flights");
            _ready = _frame != null && _velocity != null && _phase != null && _phaseAt != null && _itemId != null && _entity != null && _flights != null;
            if (!_ready) { _broken = true; PhysicsLog.Info("Thrown blades: VrThrow's flight is not where it was; thrown swords fly as before."); }
            return _ready;
        }

        [HarmonyPatch]
        internal static class FlyPatch
        {
            public static bool Prepare() => AccessTools.TypeByName("BannerlordVR.VR.VrThrow") != null;
            public static MethodBase TargetMethod() => AccessTools.Method(AccessTools.TypeByName("BannerlordVR.VR.VrThrow"), "Fly");

            [HarmonyPrefix]
            public static void Prefix(object f, out MatrixFrame __state)
            {
                __state = MatrixFrame.Identity;
                if (Ready()) __state = (MatrixFrame)_frame.GetValue(f);
            }

            [HarmonyPostfix]
            public static void Postfix(object f, float now, float dt, MatrixFrame __state)
            {
                if (!_ready || !EnemyPhysicsSettings.Enabled || !EnemyPhysicsSettings.ThrownBladesStick || dt <= 0) return;
                try { Sweep(f, __state, now); }
                catch (Exception ex) { _broken = _ready = false; PhysicsLog.Error("Thrown blade sweep failed; thrown swords fly as before.", ex); }
            }
        }

        private static void Sweep(object f, MatrixFrame before, float now)
        {
            Mission mission = Mission.Current;
            Agent player = mission?.MainAgent;
            if (player == null || StuckFlights.ContainsKey(f)) return;
            MatrixFrame after = (MatrixFrame)_frame.GetValue(f);
            Vec3 velocity = (Vec3)_velocity.GetValue(f);
            string id = (string)_itemId.GetValue(f);
            ItemObject item = id == null ? null : Game.Current?.ObjectManager?.GetObject<ItemObject>(id);
            WeaponComponentData usage = item?.PrimaryWeapon;
            if (usage == null || !usage.IsMeleeWeapon || usage.ThrustDamage <= 0) return;
            float length = Math.Min(2.5f, Math.Max(.2f, usage.WeaponLength * .01f));
            Vec3 axisAfter = after.rotation.u.NormalizedCopy();
            Vec3 tipBefore = before.origin + before.rotation.u.NormalizedCopy() * length, tipAfter = after.origin + axisAfter * length;
            if (!_logged)
            {
                _logged = true;
                PhysicsLog.Info(string.Format(CultureInfo.InvariantCulture,
                    "THROWN_BLADE first flight item={0} length={1:F2} speed={2:F2} point_dot={3:F2}", id, length, velocity.Length,
                    velocity.Length > .01f ? InteractionMath.Dot(velocity.NormalizedCopy(), axisAfter) : 0));
            }
            // The tip's path, then the blade's front half: the first contact.
            if (!EnemyPhysicsRuntime.CastTracked(tipBefore, tipAfter, player, .02f, out EnemyBody body, out BodyPoint point, out float fraction))
            {
                Vec3 midBefore = before.origin + before.rotation.u.NormalizedCopy() * (length * .5f), midAfter = after.origin + axisAfter * (length * .5f);
                if (!EnemyPhysicsRuntime.CastTracked(midBefore, midAfter, player, .02f, out body, out point, out fraction)) return;
                Log("glance", id, body, velocity, 0); return; // flat or edge first: B&S bounces it
            }
            float speed = velocity.Length;
            float dot = speed > .01f ? InteractionMath.Dot(velocity * (1 / speed), axisAfter) : 0;
            PenetrationMaterial material = body.IsCorpse ? PenetrationMaterial.Flesh : BodyPenetration.MaterialFor(body.Agent, point.Part);
            int tier = BodyPenetration.TierOf(item);
            BodyPenetration.ProfileFor(usage, true, out PenetrationProfile profile);
            // Thrown pierces use the arrow modifier rows (B&S thrown items
            // take DamageModifier_PierceArrow) at the impact speed.
            PenetrationModifiers.Row row = PenetrationModifiers.Get(PenetrationProfile.Arrow, material, tier);
            if (dot < PointFirst || !row.Allow || speed < row.MinVelocity) { Log("bounced", id, body, velocity, dot); return; }
            Vec3 contact = tipBefore + (tipAfter - tipBefore) * fraction;
            // Planted: the blade enters along its axis past the contact.
            MatrixFrame planted = after;
            planted.origin = contact + axisAfter * Depth - axisAfter * length;
            MatrixFrame bone = BoneWorld(body.Agent, point.Bone);
            StuckFlights[f] = new Stuck { Agent = body.Agent, Bone = point.Bone, Local = bone.TransformToLocalNonOrthogonal(planted), At = now };
            _frame.SetValue(f, planted);
            _phase.SetValue(f, 1);
            _phaseAt.SetValue(f, now);
            Log("planted", id, body, velocity, dot);
            if (body.IsCorpse) return;
            BloodFx.Burst(BloodFx.Kind.Enter, contact, -axisAfter);
            float damage = PenetrationDamage.DeepDamage(profile, BodyPenetration.ZoneFor(body, point), body.Agent.HealthLimit);
            PushEffect push = EnemyPhysicsRuntime.EvaluateThrownPush(body, point, velocity);
            PhysicalHit.ApplyPenetration(player, body.Agent, point.Bone, point.Part, contact, damage, DamageTypes.Pierce);
            if (body.Agent.IsActive())
            {
                EnemyPhysicsRuntime.ApplyLivingImpact(body, point, velocity, push, 1 + Math.Min(6, item.Weight));
                Disarm.ArmStab(body.Agent, point.Bone, point.Part, body.Skeleton.GetBoneName(point.Bone));
            }
        }

        private static void Log(string what, string id, EnemyBody body, Vec3 velocity, float dot) =>
            PhysicsLog.Info(string.Format(CultureInfo.InvariantCulture, "THROWN_BLADE {0} item={1} agent={2} speed={3:F2} point_dot={4:F2}",
                what, id, body?.Agent?.Index ?? -1, velocity.Length, dot));

        private static MatrixFrame BoneWorld(Agent agent, sbyte bone) =>
            agent.AgentVisuals.GetGlobalFrame().TransformToParent(agent.AgentVisuals.GetSkeleton().GetBoneEntitialFrameWithIndex(bone));

        // A planted blade follows its bone and never rests long enough to
        // glide home; drawing the weapon again ends its flight in VrThrow.
        [HarmonyPatch]
        internal static class TickPatch
        {
            public static bool Prepare() => AccessTools.TypeByName("BannerlordVR.VR.VrThrow") != null;
            public static MethodBase TargetMethod() => AccessTools.Method(AccessTools.TypeByName("BannerlordVR.VR.VrThrow"), "Tick");

            private static readonly List<object> Gone = new List<object>();
            [HarmonyPostfix]
            public static void Postfix()
            {
                if (!_ready || StuckFlights.Count == 0) return;
                try
                {
                    var flights = _flights.GetValue(null) as IList;
                    float now = Mission.Current?.CurrentTime ?? 0;
                    Gone.Clear();
                    foreach (KeyValuePair<object, Stuck> pair in StuckFlights)
                    {
                        Agent agent = pair.Value.Agent;
                        // Left long enough, it comes home by itself after all.
                        if (flights == null || !flights.Contains(pair.Key) || agent == null || agent.State == AgentState.Deleted ||
                            agent.AgentVisuals == null || now - pair.Value.At > MaxPlanted) { Gone.Add(pair.Key); continue; }
                        MatrixFrame world = BoneWorld(agent, pair.Value.Bone).TransformToParent(pair.Value.Local);
                        if (!InteractionMath.IsFinite(world.origin)) continue;
                        _frame.SetValue(pair.Key, world);
                        _phaseAt.SetValue(pair.Key, now);
                        var entity = _entity.GetValue(pair.Key) as TaleWorlds.Engine.GameEntity;
                        entity?.SetGlobalFrame(world);
                    }
                    foreach (object gone in Gone) StuckFlights.Remove(gone);
                }
                catch (Exception ex) { StuckFlights.Clear(); PhysicsLog.Error("Moving a planted thrown blade failed.", ex); }
            }
        }
    }
}
