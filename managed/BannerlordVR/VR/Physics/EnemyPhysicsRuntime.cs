using System;
using BannerlordVR.Interop;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR.Physics
{
    public static class EnemyPhysicsRuntime
    {
        private sealed class Hand
        {
            public readonly EnemyGrab Grab = new EnemyGrab();
            public readonly CorpsePenetration Penetration = new CorpsePenetration();
            public bool Sampled, GripDown, SuppressTrigger;
            public Vec3 Previous, PreviousBase, PreviousTip, Stage, Axis;
            public ItemObject Item;
            public EquipmentIndex Slot;
            public void Reset() { Grab.Release(); Penetration.Release(); Sampled = GripDown = SuppressTrigger = false; Item = null; Slot = EquipmentIndex.None; }
        }
        private static readonly EnemyBody[] Bodies = new EnemyBody[EnemyPhysicsSettings.Capacity];
        private static readonly EnemyBody[] Records = CreateRecords();
        private static readonly Hand[] Hands = { new Hand(), new Hand() };
        private static Mission _mission;
        private static Agent _player;
        private static int _cursor;
        private static float _time = -1, _lastLog;
        private static bool _failed;
        public static bool HandlingCombat { get; private set; }
        public static bool OwnsTrigger(bool left)
        {
            return HandlingCombat && Hands[left ? 0 : 1].SuppressTrigger;
        }
        public static int ActiveBodies { get; private set; }
        private static EnemyBody[] CreateRecords()
        {
            var result = new EnemyBody[EnemyPhysicsSettings.Capacity];
            for (int i = 0; i < result.Length; i++) result[i] = new EnemyBody();
            return result;
        }

        public static void Reset()
        {
            foreach (Hand hand in Hands) hand.Reset();
            Array.Clear(Bodies, 0, Bodies.Length);
            foreach (EnemyBody record in Records) record.Clear();
            _mission = null; _player = null; _cursor = 0; _time = -1;
            HandlingCombat = false; ActiveBodies = 0;
        }

        public static void Tick(Mission mission, float dt)
        {
            try
            {
                Agent player = mission?.MainAgent;
                if (_failed || !EnemyPhysicsSettings.Enabled || !VrMotionHands.Enabled || !VrSystem.IsActive ||
                    mission == null || mission.MissionEnded || mission.Mode != MissionMode.Battle ||
                    player == null || !player.IsActive() || player.HasMount ||
                    GameNetwork.IsMultiplayer || !VrCameraDriver.HasPose ||
                    VrMissionPhase.UiWantsScreen || VrMissionPhase.UiWantsOverlay || VrHandCalibration.Capturing ||
                    NativeMethods.bvr_overlay_visible() != 0)
                { Reset(); return; }
                if (_mission != mission || _player != player)
                { Reset(); _mission = mission; _player = player; }
                float now = mission.CurrentTime;
                if (now <= _time) { ClearHands(); return; }
                float elapsed = _time < 0 ? 0 : now - _time;
                _time = now;
                if (!InteractionMath.IsFinite(dt) || dt <= 0 || dt > .1f || elapsed > .1f)
                { Reset(); return; }
                // Disable the first sample: no sweep may start at world origin.
                if (elapsed <= 0) { ClearHands(); return; }
                dt = elapsed;
                HandlingCombat = true;
                ActiveBodies = 0;
                for (int i = 0; i < Bodies.Length; i++)
                {
                    EnemyBody body = Bodies[i];
                    if (body == null) continue;
                    if (!EnemyBody.Eligible(body.Agent, player) ||
                        (body.Agent.Position - player.Position).Length > EnemyPhysicsSettings.ExitRadius || !body.Sample(dt))
                        Remove(i);
                    else ActiveBodies++;
                }
                Scan(mission, player, dt);
                if (NativeMethods.bvr_get_input_state(out BvrInputState input) != (int)BvrStatus.Ok)
                { ClearHands(); return; }
                var skeleton = player.AgentVisuals?.GetSkeleton();
                if (skeleton == null || !VrHandSlots.Resolve(player, skeleton, out VrHandSlot main, out VrHandSlot off))
                { ClearHands(); return; }
                if (player.WieldedWeapon.CurrentUsageItem?.IsRangedWeapon == true ||
                    player.WieldedOffhandWeapon.CurrentUsageItem?.IsRangedWeapon == true)
                { ClearHands(); return; }
                UpdateHand(0, input.Left, main.Controller == VrHand.Left ? main : off, player, dt, now);
                UpdateHand(1, input.Right, main.Controller == VrHand.Right ? main : off, player, dt, now);
                if (now - _lastLog > 10)
                {
                    _lastLog = now;
                    VrLog.Info($"Enemy physics prototype: {ActiveBodies}/{Bodies.Length} nearby bodies; scan budget {EnemyPhysicsSettings.ScanBudget}.");
                }
            }
            catch (Exception ex)
            {
                Reset(); _failed = true;
                VrLog.Error("Enemy physics disabled for this session after an error; all grabs released.", ex);
            }
        }

        private static void ClearHands()
        {
            HandlingCombat = false;
            foreach (Hand h in Hands) h.Reset();
        }
        private static void Remove(int i)
        {
            foreach (Hand hand in Hands)
            {
                if (hand.Grab.Body == Bodies[i]) hand.Grab.Release();
                if (hand.Penetration.Body == Bodies[i]) hand.Penetration.Release();
            }
            Bodies[i] = null;
            Records[i].Clear();
        }
        private static bool Held(EnemyBody body) => Hands[0].Grab.Body == body || Hands[1].Grab.Body == body ||
            Hands[0].Penetration.Body == body || Hands[1].Penetration.Body == body;

        private static void Scan(Mission mission, Agent player, float dt)
        {
            var agents = mission.AllAgents; // Includes corpses, unlike mission.Agents.
            int count = agents.Count;
            int budget = Math.Min(count, EnemyPhysicsSettings.ScanBudget);
            for (int n = 0; n < budget; n++)
            {
                if (_cursor >= count) _cursor = 0;
                Agent agent = agents[_cursor++];
                if (agent == player) continue;
                float distance = (agent.Position - player.Position).LengthSquared;
                if (distance > EnemyPhysicsSettings.Radius * EnemyPhysicsSettings.Radius) continue;
                int chosen = -1; float farthest = distance + .25f;
                bool found = false;
                for (int i = 0; i < Bodies.Length; i++)
                {
                    EnemyBody b = Bodies[i];
                    if (b?.Agent == agent) { found = true; break; }
                    if (b == null) { chosen = i; farthest = float.MaxValue; }
                    else if (farthest != float.MaxValue && !Held(b))
                    {
                        float d = (b.Agent.Position - player.Position).LengthSquared;
                        if (d > farthest) { farthest = d; chosen = i; }
                    }
                }
                if (found || chosen < 0 || !EnemyBody.Eligible(agent, player)) continue;
                // Reuse the record and its arrays after releasing any stale reference.
                EnemyBody candidate = Records[chosen];
                Remove(chosen);
                if (candidate.Bind(agent) && candidate.Sample(dt)) Bodies[chosen] = candidate;
            }
            ActiveBodies = 0;
            for (int i = 0; i < Bodies.Length; i++) if (Bodies[i] != null) ActiveBodies++;
        }

        private static void UpdateHand(int index, BvrHandState input, VrHandSlot slot, Agent player, float dt, float now)
        {
            Hand hand = Hands[index];
            if (input.IsActive == 0 || !slot.Valid) { hand.Reset(); return; }
            MatrixFrame pose = VrCameraDriver.StageToHand(input.GripOrientation, input.GripPosition);
            Vec3 stage = new Vec3(input.GripPosition.X, input.GripPosition.Y, input.GripPosition.Z);
            Vec3 stageAxis = VrMath.ToBannerlord(input.GripOrientation, input.GripPosition).rotation.u;
            MissionWeapon weapon = slot.Equipment == EquipmentIndex.None ? MissionWeapon.Invalid : player.Equipment[slot.Equipment];
            WeaponComponentData usage = weapon.IsEmpty ? null : weapon.CurrentUsageItem;
            Vec3 basePoint = pose.origin, tip = basePoint;
            bool blade = usage != null && usage.IsMeleeWeapon && EnemyPhysicsSettings.WeaponDamage;
            hand.SuppressTrigger = usage == null || blade;
            if (blade)
            {
                var entity = player.GetWeaponEntityFromEquipmentSlot(slot.Equipment);
                if (!entity.IsValid) { hand.Reset(); return; }
                MatrixFrame weaponFrame = entity.GetGlobalFrame();
                basePoint = weaponFrame.origin;
                tip = basePoint + weaponFrame.rotation.u.NormalizedCopy() * Math.Min(2.5f, usage.WeaponLength * .01f);
            }
            bool changed = !hand.Sampled || hand.Item != weapon.Item || hand.Slot != slot.Equipment;
            if (changed || !blade) hand.Penetration.Release();
            if (!InteractionMath.IsFinite(basePoint) || !InteractionMath.IsFinite(tip) ||
                !InteractionMath.IsFinite(pose.rotation.s) || !InteractionMath.IsFinite(pose.rotation.f) || !InteractionMath.IsFinite(pose.rotation.u) ||
                (!changed && ((pose.origin - hand.Previous).Length > .5f || (tip - hand.PreviousTip).Length > 1.5f)))
            { hand.Reset(); return; }
            bool grip = input.Grip >= (hand.GripDown ? .45f : .7f);
            if (!grip || usage != null) hand.Grab.Release();
            bool newGrab = grip && !hand.GripDown && usage == null;
            if (!changed)
            {
                if (newGrab) TryGrab(hand, pose.origin);
                // Use stage-space displacement to reject locomotion-only punches.
                float gestureSpeed = InteractionMath.GestureSpeed(hand.Stage, stage, hand.Axis, stageAxis,
                    blade ? (tip - basePoint).Length : 0, VrCameraDriver.HandScale, dt);
                bool embedded = blade && hand.Penetration.Tick(basePoint, tip, dt);
                if (!embedded && !hand.Grab.Held && gestureSpeed >= (blade ? .6f : 1.4f) &&
                    (blade || (usage == null && input.Trigger >= .55f)))
                    TryHit(index, hand, basePoint, tip, usage, weapon, slot.Equipment, player, dt, now, blade);
                if (hand.Grab.Held) hand.Grab.Tick(pose, dt);
            }
            hand.Previous = pose.origin;
            hand.PreviousBase = basePoint;
            hand.PreviousTip = tip;
            hand.Stage = stage;
            hand.Axis = stageAxis;
            hand.Sampled = true; hand.GripDown = grip; hand.Item = weapon.Item; hand.Slot = slot.Equipment;
        }

        private static bool Visible(Vec3 start, Vec3 end, EnemyBody body)
        {
            float length = (end - start).Length;
            if (length < .001f) return true;
            var ignored = _player.AgentVisuals.GetEntity().WeakEntity;
            if (!_mission.Scene.RayCastForClosestEntityOrTerrainIgnoreEntity(in start, in end, ignored,
                out float distance, out GameEntity entity, .01f)) return true;
            return distance >= length - .06f || (entity != null && entity.Pointer == body.Agent.AgentVisuals.GetEntity().Pointer);
        }

        private static void TryGrab(Hand hand, Vec3 position)
        {
            EnemyBody best = null; int point = -1; float distance = .22f;
            foreach (EnemyBody body in Bodies)
            {
                if (body == null || (!body.IsCorpse && !body.Agent.IsActive())) continue;
                for (int i = 0; i < body.Count; i++)
                {
                    BodyPoint p = body.Points[i];
                    string name = body.Skeleton.GetBoneName(p.Bone) ?? "";
                    // Living grabs currently support head/neck/hands; corpse joints support all admitted points.
                    if (!body.IsCorpse && p.Part != BoneBodyPartType.Head && p.Part != BoneBodyPartType.Neck && !name.EndsWith("hand")) continue;
                    float d = (p.World - position).Length;
                    if (d < distance && Visible(position, p.World, body)) { distance = d; best = body; point = i; }
                }
            }
            if (best != null && (!best.IsCorpse || best.HasNativePhysics)) hand.Grab.Begin(best, point, position);
        }

        private static void TryHit(int index, Hand hand, Vec3 start, Vec3 tip, WeaponComponentData usage,
            MissionWeapon weapon, EquipmentIndex slot, Agent player, float dt, float now, bool blade)
        {
            EnemyBody hit = null; int hitPoint = -1; float earliest = 2;
            Vec3 contact = Vec3.Zero, velocity = Vec3.Zero;
            int samples = blade ? Math.Min(32, Math.Max(2, (int)Math.Ceiling((tip - start).Length / .06f))) : 1;
            foreach (EnemyBody body in Bodies)
            {
                if (body == null || now - (index == 0 ? body.LastHitLeft : body.LastHitRight) < .35f) continue;
                // Until shield/weapon mesh contact is implemented, conservatively preserve guards.
                if (!body.IsCorpse && (body.Agent.WieldedOffhandWeapon.CurrentUsageItem?.IsShield == true ||
                    (body.Agent.GetCurrentActionType(1) >= Agent.ActionCodeType.DefendFist &&
                     body.Agent.GetCurrentActionType(1) <= Agent.ActionCodeType.DefendLeftStaff))) continue;
                for (int s = 0; s <= (blade ? samples : 0); s++)
                {
                    float along = blade ? (float)s / samples : 0;
                    Vec3 previous = hand.PreviousBase + (hand.PreviousTip - hand.PreviousBase) * along;
                    Vec3 current = start + (tip - start) * along;
                    for (int p = 0; p < body.Count; p++)
                    {
                        BodyPoint point = body.Points[p];
                        // Relative sweep accounts for target motion too.
                        Vec3 oldCenter = point.World - point.Velocity * dt;
                        if (InteractionMath.SegmentSphere(previous - oldCenter, current - point.World, Vec3.Zero,
                            point.Radius + (blade ? .025f : .065f), out float fraction) && fraction < earliest)
                        {
                            Vec3 c = previous + (current - previous) * fraction;
                            if (!Visible(previous, c, body)) continue;
                            earliest = fraction; hit = body; hitPoint = p; contact = c;
                            velocity = InteractionMath.ClampMagnitude((current - previous) * (1 / dt) - point.Velocity, 12);
                        }
                    }
                }
            }
            if (hit == null) return;
            if (index == 0) hit.LastHitLeft = now; else hit.LastHitRight = now;
            bool thrust = blade && usage.ThrustDamage > 0 && InteractionMath.Dot(velocity, (tip - start).NormalizedCopy()) > velocity.Length * .7f;
            PhysicalHit.Apply(player, hit, hitPoint, contact, velocity, weapon, slot, thrust);
            if (thrust && hit.IsCorpse) hand.Penetration.Begin(hit, hitPoint, contact);
            NativeMethods.bvr_trigger_haptic(index, .45f, .035f);
        }
    }
}
