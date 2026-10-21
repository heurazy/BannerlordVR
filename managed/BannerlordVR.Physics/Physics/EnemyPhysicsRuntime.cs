using System;
using System.Diagnostics;
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
            // Trace diagnostics, logged every 2 s per hand.
            public float DiagSpeed, DiagNearest = float.MaxValue, DiagBoneGap, DiagGrip, DiagTrigger, DiagNext;
            public int DiagCasts, DiagRayHits, DiagRejected, DiagHits, DiagGrabs;
            public string DiagReject = "-", DiagItem = "-";
            public Hand(int index) { Grab.HandIndex = index; Penetration.HandIndex = index; }
            public readonly BodyPenetration Penetration = new BodyPenetration();
            public readonly CutStroke Cut = new CutStroke();
            public bool Sampled, GripDown, SuppressTrigger;
            // A point resting on a body while the blade pushes (pressure entry).
            public EnemyBody PressBody; public sbyte PressBone; public Vec3 PressLocal; public float PressAge;
            public Vec3 Previous, PreviousBase, PreviousTip, Stage, Axis;
            public ItemObject Item;
            public EquipmentIndex Slot;
            public void Reset() { PressBody = null; Grab.Release(); Penetration.Release(); Cut.Reset(); Sampled = GripDown = SuppressTrigger = false; Item = null; Slot = EquipmentIndex.None; }
        }
        private static readonly EnemyBody[] Bodies = new EnemyBody[EnemyPhysicsSettings.Capacity];
        private static readonly EnemyBody[] Records = CreateRecords();
        private static readonly Hand[] Hands = { new Hand(0), new Hand(1) };
        private static readonly AdaptivePhysicsBudget Budget = new AdaptivePhysicsBudget(
            EnemyPhysicsSettings.Capacity, EnemyPhysicsSettings.BudgetMilliseconds, 4);
        private static long _workStarted;
        private static Mission _mission, _lastMission;
        private static LivingRagdollMissionLogic _living;
        private static BleedingMissionLogic _bleeding;
        private static Agent _player;
        private static int _cursor;
        private static float _time = -1, _lastLog;
        private static readonly System.Collections.Generic.Dictionary<int, string> _refusals = new System.Collections.Generic.Dictionary<int, string>();
        public static bool Tracks(Agent agent)
        {
            foreach (EnemyBody body in Bodies) if (body?.Agent == agent) return true;
            return false;
        }
        private static string _blocked = "start";
        private static bool _failed;
        public static bool HandlingCombat { get; private set; }
        public static bool OwnsTrigger(bool left)
        {
            return HandlingCombat && Hands[left ? 0 : 1].SuppressTrigger;
        }
        public static int ActiveBodies { get; private set; }
        public static bool SupportsLivingBody => _living?.Available == true;
        public static bool OwnsLivingPhysics(Agent agent) => _living?.Simulating(agent) == true;
        public static bool HasLivingLease(Agent agent) => _living?.HasLease(agent) == true;
        public static bool Penetrated(Agent agent)
        {
            if (agent == null) return false;
            foreach (Hand hand in Hands) if (hand.Penetration.Body?.Agent == agent) return true;
            return false;
        }
        public static bool Protects(Agent agent)
        {
            if (agent == null) return false;
            foreach (Hand hand in Hands)
                if (hand.Penetration.Body?.Agent == agent || hand.Grab.Body?.Agent == agent) return true;
            return HasLivingLease(agent);
        }
        public static bool BeginLivingGrab(EnemyBody body) => !body.IsCorpse && _living?.KeepHeld(body, true) == true;
        public static void MaintainLivingGrab(EnemyBody body) { if (!body.IsCorpse) _living?.KeepHeld(body); }
        // PhysX steps counted by the scene clock, or -1 when no clock runs.
        public static long PhysicsSteps => _living?.PhysicsSteps ?? -1;
        // Horizontal gap between a capsule of this radius at a point and the
        // nearest tracked living enemy's capsule (negative when overlapping).
        public static float Clearance(Vec2 at, float radius) => Nearest(at, radius, out _);
        public static System.Collections.Generic.IEnumerable<Agent> TrackedLiving()
        {
            foreach (EnemyBody body in Bodies)
                if (body?.Agent != null && !body.IsCorpse && body.Agent.IsActive()) yield return body.Agent;
        }
        public static float Nearest(Vec2 at, float radius, out Vec2 position)
        {
            float best = float.MaxValue; position = at;
            foreach (EnemyBody body in Bodies)
            {
                Agent agent = body?.Agent;
                // A held body counts too: dropping it from the clearance pulled
                // the view back onto the player's body the moment it was grabbed.
                if (agent == null || body.IsCorpse || !agent.IsActive()) continue;
                float gap = (agent.Position.AsVec2 - at).Length - PlayerReach.Radius(agent) - radius;
                if (gap < best) { best = gap; position = agent.Position.AsVec2; }
            }
            return best;
        }
        public static float PartMass(EnemyBody body, sbyte bone) => _living?.PartMass(body.Agent, bone) ?? 5;
        public static bool HandleActiveGrab(EnemyBody body, int hand, BodyPoint point, Vec3 handWorld, MatrixFrame boneWorld, Vec3 contact)
            => !body.IsCorpse && _living != null && _living.HandleActive(body, hand, point, handWorld, boneWorld, contact);
        public static bool PierceActive(EnemyBody body, int hand, sbyte bone, Vec3 bladePoint, Vec3 axis, bool axialLock,
            Vec3 localAxis, float angleLimit, MatrixFrame boneWorld, Vec3 contact)
            => !body.IsCorpse && _living != null && _living.PierceActive(body, hand, bone, bladePoint, axis, axialLock, localAxis, angleLimit, boneWorld, contact);
        public static void MarkDying(EnemyBody body) { if (body != null && !body.IsCorpse) _living?.Die(body); }
        public static void ReleaseActiveGrab(EnemyBody body, int hand) { if (body != null && !body.IsCorpse) _living?.ReleaseActive(body, hand); }
        public static void ApplyLivingImpact(EnemyBody body, BodyPoint point, Vec3 velocity,
            PushEffect push = PushEffect.None, float hitMass = 2, bool fist = false)
        { if (!body.IsCorpse) _living?.Impact(body, point, velocity, push, hitMass, fist); }
        public static void RecordWound(Agent source, EnemyBody body, BodyPoint point, Vec3 contact,
            DamageTypes type, float healthLost, bool drawCut)
        { _bleeding?.Record(source, body, point, contact, type, healthLost, drawCut); }
        public static void DeepenWound(Agent victim, sbyte bone, float depth) => _bleeding?.Deepen(victim, bone, depth);
        private static EnemyBody[] CreateRecords()
        {
            var result = new EnemyBody[EnemyPhysicsSettings.Capacity];
            for (int i = 0; i < result.Length; i++) result[i] = new EnemyBody();
            return result;
        }

        public static void Reset()
        {
            _living?.ReleaseAll();
            _refusals.Clear();
            foreach (Hand hand in Hands) hand.Reset();
            Array.Clear(Bodies, 0, Bodies.Length);
            foreach (EnemyBody record in Records) record.Clear();
            _mission = null; _player = null; _cursor = 0; _time = -1;
            _living = null;
            _bleeding = null;
            HandlingCombat = false; ActiveBodies = 0;
        }

        private static bool RadialMenuOpen() =>
            TaleWorlds.ScreenSystem.ScreenManager.TopScreen is TaleWorlds.MountAndBlade.View.Screens.MissionScreen screen && screen.IsRadialMenuActive;
        private static bool _loggedInput, _loggedSlots;
        private static void Once(ref bool done, string message) { if (!done) { done = true; PhysicsLog.Info(message); } }
        public static void Tick(Mission mission, float dt)
        {
            bool measured = false;
            try
            {
                Agent player = mission?.MainAgent;
                string blocked = _failed ? "failed_earlier" : !EnemyPhysicsSettings.Enabled ? "enemy_physics_off" :
                    !VrMotionHands.Enabled ? "motion_hands_off" : !VrSystem.IsActive ? "vr_inactive" :
                    mission == null || mission.MissionEnded ? "no_mission" : mission.Mode != MissionMode.Battle ? "mode_" + mission.Mode :
                    player == null || !player.IsActive() ? "no_player" : player.HasMount ? "player_mounted" :
                    GameNetwork.IsMultiplayer ? "multiplayer" : !VrCameraDriver.HasPose ? "no_head_pose" :
                    // The release keys the HUD over the world for the whole mission
                    // (ui_overlay_always), so UiWantsOverlay no longer means a menu:
                    // pause only for an actual menu, the order menu or a wheel.
                    VrMissionPhase.UiWantsScreen ? "ui_screen" : VrMissionPhase.UiMenuOpen ? "menu_open" :
                    VrMissionPhase.OrderMenuOpen ? "order_menu" : RadialMenuOpen() ? "radial_menu" :
                    VrHandCalibration.Capturing ? "hand_calibration" : PhysicsInterop.OverlayVisible() ? "vr_overlay" : null;
                if (blocked != _blocked)
                {
                    PhysicsLog.Info("Enemy physics " + (blocked == null ? "ACTIVE" : "paused: " + blocked) +
                        (player != null ? "; player mounted=" + player.HasMount : ""));
                    _blocked = blocked;
                }
                if (blocked != null) { Reset(); return; }
                if (_mission != mission || _player != player)
                {
                    // A new mission: the kept body handles belong to the old one
                    // (Reset clears _mission on every pause, so track it apart).
                    if (_lastMission != mission) { CorpseVisuals.Forget(); _lastMission = mission; }
                    Reset(); _mission = mission; _player = player;
                    _living = mission.GetMissionBehavior<LivingRagdollMissionLogic>();
                    _bleeding = mission.GetMissionBehavior<BleedingMissionLogic>();
                }
                float now = mission.CurrentTime;
                if (now <= _time) { ClearHands(); return; }
                float elapsed = _time < 0 ? 0 : now - _time;
                _time = now;
                if (!InteractionMath.IsFinite(dt) || dt <= 0 || dt > .1f || elapsed > .1f)
                { Reset(); return; }
                // Disable the first sample: no sweep may start at world origin.
                if (elapsed <= 0) { ClearHands(); return; }
                dt = elapsed;
                measured = true;
                _workStarted = Stopwatch.GetTimestamp();
                HandlingCombat = true;
                ActiveBodies = 0;
                for (int i = 0; i < Bodies.Length; i++)
                {
                    EnemyBody body = Bodies[i];
                    if (body == null) continue;
                    bool leased = _living?.HasLease(body.Agent) == true;
                    if (!EnemyBody.Eligible(body.Agent, player, leased) ||
                        (!leased && (body.Agent.Position - player.Position).Length > EnemyPhysicsSettings.ExitRadius) || !body.Sample(dt))
                        Remove(i);
                    else
                    {
                        ActiveBodies++;
                        // Within reach a corpse stays physical (not filed).
                        if (body.IsCorpse && CorpseKeeper.Keep(body.Agent)) CorpseKeeper.KeepAwake(body, _time);
                        // A blade held against its shield pushes it aside.
                        else if (!body.IsCorpse && _living != null && ShieldBlock.Pressing(body.Agent.Index, out Vec3 press))
                            _living.ShieldPress(body, press);
                    }
                }
                ShedUnheldBodies(player);
                Scan(mission, player, dt);
                if (PhysicsInterop.GetInputState(out BvrInputState input) != (int)BvrStatus.Ok)
                { Once(ref _loggedInput, "Enemy physics: controller input unavailable this frame."); ClearHands(); return; }
                var skeleton = player.AgentVisuals?.GetSkeleton();
                if (skeleton == null || !VrHandSlots.Resolve(player, skeleton, out VrHandSlot main, out VrHandSlot off))
                { Once(ref _loggedSlots, "Enemy physics: hand bones not resolved for the player."); ClearHands(); return; }
                if (player.WieldedWeapon.CurrentUsageItem?.IsRangedWeapon == true ||
                    player.WieldedOffhandWeapon.CurrentUsageItem?.IsRangedWeapon == true)
                { ClearHands(); return; }
                UpdateHand(0, input.Left, input.Right, main.Controller == VrHand.Left ? main : off, player, dt, now);
                UpdateHand(1, input.Right, input.Left, main.Controller == VrHand.Right ? main : off, player, dt, now);
                if (now - _lastLog > 10)
                {
                    _lastLog = now;
                    PhysicsLog.Info(string.Format("Enemy physics: {0}/{1} bodies (adaptive limit {2}); CPU mean {3:F3} ms, p95 {4:F3}, p99 {5:F3}; budget {6:F2} ms. CPU work only, not total frame time.",
                        ActiveBodies, Bodies.Length, Budget.BodyLimit, Budget.Mean,
                        Budget.Percentile(.95), Budget.Percentile(.99), Budget.Milliseconds));
                }
            }
            catch (Exception ex)
            {
                Reset(); _failed = true;
                PhysicsLog.Error("Enemy physics disabled for this session after an error; all grabs released.", ex);
            }
            finally
            {
                if (measured) Budget.Record((Stopwatch.GetTimestamp() - _workStarted) * 1000.0 / Stopwatch.Frequency +
                    (_living?.LastTickCpuMilliseconds ?? 0) + (_bleeding?.LastTickCpuMilliseconds ?? 0));
            }
        }

        private static bool AdmissionTimeRemaining =>
            (Stopwatch.GetTimestamp() - _workStarted) * 1000.0 / Stopwatch.Frequency < Budget.Milliseconds * .35;

        private static void ShedUnheldBodies(Agent player)
        {
            while (ActiveBodies > Budget.BodyLimit)
            {
                int remove = -1; float farthest = -1;
                for (int i = 0; i < Bodies.Length; i++)
                {
                    EnemyBody b = Bodies[i];
                    if (b == null || Held(b)) continue;
                    float distance = (b.Agent.Position - player.Position).LengthSquared;
                    if (distance > farthest) { farthest = distance; remove = i; }
                }
                if (remove < 0) break; // Never drop a held body just to improve a statistic.
                Remove(remove); ActiveBodies--;
            }
        }

        private static void ClearHands()
        {
            HandlingCombat = false;
            foreach (Hand h in Hands) h.Reset();
        }
        private static void Remove(int i)
        {
            if (Bodies[i] != null) _living?.Release(Bodies[i].Agent);
            foreach (Hand hand in Hands)
            {
                if (hand.Grab.Body == Bodies[i]) hand.Grab.Release();
                if (hand.Penetration.Body == Bodies[i]) hand.Penetration.Release();
            }
            Bodies[i] = null;
            Records[i].Clear();
        }
        // A body the physics simulates (thrown, falling, getting up) is held
        // too: shedding it handed it back to its animation mid-fall, at the
        // spot the agent never left.
        private static bool Held(EnemyBody body) => Hands[0].Grab.Body == body || Hands[1].Grab.Body == body ||
            Hands[0].Penetration.Body == body || Hands[1].Penetration.Body == body ||
            (body?.Agent != null && _living != null && _living.HasLease(body.Agent));

        private static void Scan(Mission mission, Agent player, float dt)
        {
            var agents = mission.AllAgents; // Includes corpses, unlike mission.Agents.
            int count = agents.Count;
            int budget = Math.Min(count, EnemyPhysicsSettings.ScanBudget);
            for (int n = 0; n < budget; n++)
            {
                if ((n & 15) == 0 && !AdmissionTimeRemaining) break;
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
                    if (b == null && ActiveBodies < Budget.BodyLimit) { chosen = i; farthest = float.MaxValue; }
                    else if (b != null && farthest != float.MaxValue && !Held(b))
                    {
                        float d = (b.Agent.Position - player.Position).LengthSquared;
                        if (d > farthest) { farthest = d; chosen = i; }
                    }
                }
                if (found || chosen < 0) continue;
                string refused = EnemyBody.EligibleReason(agent, player);
                if (refused != null)
                {
                    // Close agents only: why one within reach is ignored.
                    // Once per agent and reason: a mounted rider logged every
                    // 2 s hid every other refusal.
                    if (EnemyPhysicsSettings.Trace && agent.IsHuman && distance < 25 && refused != "not_enemy" &&
                        (!_refusals.TryGetValue(agent.Index, out string last) || last != refused))
                    {
                        _refusals[agent.Index] = refused;
                        PhysicsLog.Info("Physics ignores agent " + agent.Index + ": " + refused + " controller=" + agent.Controller);
                    }
                    continue;
                }
                // Reuse the record and its arrays after releasing any stale reference.
                EnemyBody candidate = Records[chosen];
                bool occupied = Bodies[chosen] != null;
                Remove(chosen);
                if (candidate.Bind(agent) && candidate.Sample(dt))
                { Bodies[chosen] = candidate; if (!occupied) ActiveBodies++; }
                else
                {
                    string why = "bind_" + candidate.LastFailure;
                    if (EnemyPhysicsSettings.Trace && (!_refusals.TryGetValue(agent.Index, out string last) || last != why))
                    { _refusals[agent.Index] = why; PhysicsLog.Info("Physics could not bind agent " + agent.Index + ": " + candidate.LastFailure); }
                    candidate.Clear(); if (occupied) ActiveBodies--;
                }
            }
            ActiveBodies = 0;
            for (int i = 0; i < Bodies.Length; i++) if (Bodies[i] != null) ActiveBodies++;
        }

        private static void UpdateHand(int index, BvrHandState input, BvrHandState otherInput, VrHandSlot slot, Agent player, float dt, float now)
        {
            Hand hand = Hands[index];
            hand.Penetration.Role = slot.Role;
            hand.Grab.Role = slot.Role;
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
                // The release draws the held weapon as its own skinned visual
                // (VrSkinnedWeapon), not where the game's weapon entity sits;
                // VrBlade measures the blade actually shown in the hand.
                if (VrBlade.TryGet(player, slot, out VrBlade.Segment segment) && segment.Valid)
                {
                    basePoint = segment.GripWorld;
                    tip = segment.TipWorld;
                    // The shown blade may be held back by a joint or by
                    // VrCollide; the physics works from the controller's.
                    PhysicalBlade.LearnBlade(slot.Role, basePoint, tip);
                    PhysicalBlade.ToRaw(slot.Role, ref basePoint, ref tip);
                }
                else
                {
                    var entity = player.GetWeaponEntityFromEquipmentSlot(slot.Equipment);
                    if (!entity.IsValid) { hand.Reset(); return; }
                    MatrixFrame weaponFrame = entity.GetGlobalFrame();
                    basePoint = weaponFrame.origin;
                    tip = basePoint + weaponFrame.rotation.u.NormalizedCopy() * Math.Min(2.5f, usage.WeaponLength * .01f);
                }
            }
            bool changed = !hand.Sampled || hand.Item != weapon.Item || hand.Slot != slot.Equipment;
            if (changed || !blade) { hand.Penetration.Release(); hand.Cut.Reset(); }
            if (!InteractionMath.IsFinite(basePoint) || !InteractionMath.IsFinite(tip) ||
                !InteractionMath.IsFinite(pose.rotation.s) || !InteractionMath.IsFinite(pose.rotation.f) || !InteractionMath.IsFinite(pose.rotation.u) ||
                (!changed && ((pose.origin - hand.Previous).Length > .5f || (tip - hand.PreviousTip).Length > 1.5f)))
            { hand.Reset(); return; }
            bool grip = input.Grip >= (hand.GripDown ? .45f : .7f);
            if (!grip || usage != null) hand.Grab.Release();
            bool newGrab = grip && !hand.GripDown && usage == null;
            if (!changed)
            {
                if (newGrab) { TryGrab(hand, pose.origin); if (hand.Grab.Held) hand.DiagGrabs++; }
                // Use stage-space displacement to reject locomotion-only punches.
                float gestureSpeed = InteractionMath.GestureSpeed(hand.Stage, stage, hand.Axis, stageAxis,
                    blade ? (tip - basePoint).Length : 0, VrCameraDriver.HandScale, dt);
                if (EnemyPhysicsSettings.Trace) Diagnose(index, hand, pose, slot, player, usage, blade, input, gestureSpeed, now);
                bool embedded = blade && hand.Penetration.Tick(basePoint, tip, dt);
                if (embedded && hand.Penetration.LastResistance > 40)
                    PhysicsInterop.Haptic(index, Math.Min(1, hand.Penetration.LastResistance / 600), .02f);
                bool drawing = !embedded && TryNeckCut(index, hand, otherInput, input.Trigger, basePoint, tip,
                    usage, weapon, slot.Equipment, player, gestureSpeed * dt, dt, now);
                // A shield bashes (a blunt B&S item); a fist hits when closed.
                bool shield = usage != null && usage.IsShield;
                // A blade a shield holds back strikes nothing behind it.
                bool held = blade && ShieldBlock.Blocked(slot.Role);
                if (!held && !embedded && !drawing && !hand.Grab.Held && gestureSpeed >= (blade ? .6f : 1.4f) &&
                    (blade || shield || (usage == null && input.Trigger >= .55f)))
                    TryHit(index, hand, basePoint, tip, usage, weapon, slot.Equipment, player, dt, now, blade);
                else if (!held && !embedded && !drawing && blade && EnemyPhysicsSettings.ActiveRagdoll)
                    TryPressure(index, hand, basePoint, tip, usage, player, dt);
                if (hand.Grab.Held) hand.Grab.Tick(pose, dt);
            }
            hand.Previous = pose.origin;
            hand.PreviousBase = basePoint;
            hand.PreviousTip = tip;
            hand.Stage = stage;
            hand.Axis = stageAxis;
            hand.Sampled = true; hand.GripDown = grip; hand.Item = weapon.Item; hand.Slot = slot.Equipment;
        }

        // This path intentionally has no minimum strike speed or strike cooldown:
        // a cut is a continuous draw, while TryHit handles ballistic impacts.
        private static bool TryNeckCut(int index, Hand hand, BvrHandState otherInput, float trigger,
            Vec3 start, Vec3 tip, WeaponComponentData usage, MissionWeapon weapon, EquipmentIndex slot,
            Agent player, float trackedTravel, float dt, float now)
        {
            EnemyGrab hold = Hands[1 - index].Grab;
            EnemyBody body = hold.Body;
            bool sword = usage != null && (usage.WeaponClass == WeaponClass.Dagger ||
                usage.WeaponClass == WeaponClass.OneHandedSword || usage.WeaponClass == WeaponClass.TwoHandedSword);
            if (!EnemyPhysicsSettings.NeckCuts || !EnemyPhysicsSettings.WeaponDamage || !sword ||
                usage.SwingDamageType != DamageTypes.Cut || usage.SwingDamage <= 0 ||
                !InteractionMath.IsFinite(trigger) || trigger < .55f ||
                otherInput.IsActive == 0 || !InteractionMath.IsFinite(otherInput.Grip) || otherInput.Grip < .45f || body == null || body.IsCorpse ||
                !body.Agent.IsActive() || hold.Point < 0 || hold.Point >= body.Count)
            { hand.Cut.Reset(); return false; }
            MatrixFrame otherPose = VrCameraDriver.StageToHand(otherInput.GripOrientation, otherInput.GripPosition);
            if (!InteractionMath.IsFinite(otherPose.origin) || !InteractionMath.IsFinite(otherPose.rotation.s) ||
                !InteractionMath.IsFinite(otherPose.rotation.f) || !InteractionMath.IsFinite(otherPose.rotation.u) ||
                (otherPose.origin - body.Points[hold.Point].World).Length > EnemyPhysicsSettings.BreakDistance)
            { hand.Cut.Reset(); return false; }
            BoneBodyPartType heldPart = body.Points[hold.Point].Part;
            EquipmentIndex otherSlot = Hands[1 - index].Slot;
            if ((heldPart != BoneBodyPartType.Head && heldPart != BoneBodyPartType.Neck) ||
                (otherSlot != EquipmentIndex.None && player.Equipment[otherSlot].CurrentUsageItem != null))
            { hand.Cut.Reset(); return false; }
            Vec3 axis = (tip - start).NormalizedCopy();
            Vec3 edgeStart = start + axis * .08f;
            Agent agent = CastLimbs(edgeStart, tip, player.Index,
                .015f, out float distance, out sbyte bone);
            float length = (tip - edgeStart).Length;
            if (agent != body.Agent || bone < 0 || bone >= body.Skeleton.GetBoneCount() ||
                !InteractionMath.IsFinite(distance) || distance < 0 || distance > length)
            { hand.Cut.Reset(); return false; }
            BodyPoint point = body.ReadContact(bone);
            Vec3 contact = edgeStart + axis * distance;
            if (point.Part != BoneBodyPartType.Neck || !Visible(edgeStart, contact, body) ||
                EquipmentBlocks(body, edgeStart, tip, distance / Math.Max(.001f, length), .015f))
            { hand.Cut.Reset(); return false; }
            if (hand.Cut.Sample(agent.Index, start - point.World, axis, trackedTravel, now))
            {
                Vec3 velocity = (start - hand.PreviousBase) * (1 / dt) - point.Velocity;
                PhysicalHit.ApplyDrawCut(player, body, point, contact, velocity, weapon, slot);
                if (index == 0) body.LastHitLeft = now; else body.LastHitRight = now;
                PhysicsInterop.Haptic(index, .35f, .045f);
            }
            return true; // Reserve this continuous contact instead of also applying impact damage.
        }

        private static bool Visible(Vec3 start, Vec3 end, EnemyBody body)
        {
            float length = (end - start).Length;
            if (length < .001f) return true;
            var ignored = _player.AgentVisuals.GetEntity().WeakEntity;
            if (!_mission.Scene.RayCastForClosestEntityOrTerrainIgnoreEntity(in start, in end, ignored,
                out float distance, out GameEntity entity, .01f)) return true;
            return distance >= length - .06f || (entity != null && entity.Pointer == CorpseVisuals.EntityPointer(body.Agent));
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
                    // The pose fallback supports head/hands; the native living
                    // backend exposes the full admitted body when explicitly enabled.
                    if (!body.IsCorpse && !SupportsLivingBody && p.Part != BoneBodyPartType.Head &&
                        p.Part != BoneBodyPartType.Neck && !name.EndsWith("hand")) continue;
                    float d = (p.World - position).Length;
                    if (d < distance && Visible(position, p.World, body)) { distance = d; best = body; point = i; }
                }
            }
            if (best == null) return;
            // The engine puts a corpse's ragdoll to sleep once it settles; a
            // hand closing on it wakes it, as any B&S body is physical.
            // A corpse the engine filed is never woken: activating its ragdoll
            // crashed the engine (TaleWorlds.Native+0x1D3A10 read 0x2BE0).
            if (best.IsCorpse && !best.HasNativePhysics && (Filed(best) || !EnemyPhysicsSettings.KeepNearCorpses)) return;
            if (best.IsCorpse && !best.HasNativePhysics && best.Skeleton.GetCurrentRagdollState() == RagdollState.Disabled)
            {
                if (best.Skeleton.IsFrozen()) best.Skeleton.Freeze(false);
                // The agent path (start_ragdoll_as_corpse) is what the engine
                // ticks for a dead agent; Skeleton.ActivateRagdoll alone stayed
                // NeedsActivation forever on a corpse.
                // Only while its visuals still answer (not filed): the start
                // goes through the mission's ragdoll routine (0x1805d34e0).
                bool live = best.Agent.AgentVisuals != null && best.Agent.AgentVisuals.IsValid();
                if (live) best.Agent.StartRagdollAsCorpse();
                RagdollState started = best.Skeleton.GetCurrentRagdollState();
                if (started == RagdollState.Disabled) best.Skeleton.ActivateRagdoll();
                if (EnemyPhysicsSettings.Trace)
                    PhysicsLog.Info("GRAB corpse agent=" + best.Agent.Index + " ragdoll woken visuals=" + (live ? "valid" : "filed") +
                        " after_agent_start=" + started + " now=" + best.Skeleton.GetCurrentRagdollState());
            }
            if (!best.IsCorpse || best.HasNativePhysics || best.Skeleton.GetCurrentRagdollState() != RagdollState.Disabled)
                hand.Grab.Begin(best, point, position);
        }

        // Damager.TryHitByPressure: a point pushed into a body along the blade
        // (within penetrationPressureMaxDot 0.2 of its axis) enters without a
        // strike once the contact's pressure reaches the damager's
        // penetrationPressureForceCurve at the contact speed, times the
        // material's pressureForceMultiplier. The point rests on the surface
        // while the hand pushes; damage then comes from depth.
        private const float PressureMaxDot = .2f, PressLateral = .06f, PressTimeout = 2;
        private static void TryPressure(int index, Hand hand, Vec3 start, Vec3 tip, WeaponComponentData usage, Agent player, float dt)
        {
            if (!hand.Sampled || dt <= 0 || !BodyPenetration.ProfileFor(usage, true, out PenetrationProfile profile) ||
                !PenetrationJoint.Get(profile).Pierce) { hand.PressBody = null; return; }
            Vec3 axis = tip - start;
            if (axis.Length < .05f) { hand.PressBody = null; return; }
            Vec3 axisN = axis.NormalizedCopy();
            Vec3 motion = tip - hand.PreviousTip;
            float length = motion.Length;
            EnemyBody body = hand.PressBody;
            sbyte bone = hand.PressBone;
            Vec3 contact;
            if (body == null || body.Agent == null || !body.Agent.IsActive() || bone >= body.Skeleton.GetBoneCount())
            {
                hand.PressBody = null;
                if (length < 1e-4f || InteractionMath.Dot(motion * (1 / length), axisN) < 1 - PressureMaxDot) return;
                Agent agent = CastLimbs(hand.PreviousTip, tip, player.Index, .02f, out float distance, out bone);
                if (agent == null || bone < 0 || !InteractionMath.IsFinite(distance) || distance < 0 || distance > length) return;
                body = null;
                foreach (EnemyBody candidate in Bodies) if (candidate?.Agent == agent) { body = candidate; break; }
                if (body == null || bone >= body.Skeleton.GetBoneCount()) return;
                contact = hand.PreviousTip + motion * (distance / length);
                if (!Visible(hand.PreviousTip, contact, body) || EquipmentBlocks(body, hand.PreviousTip, tip, distance / length, .02f)) return;
                MatrixFrame first = body.Frame.TransformToParent(body.Skeleton.GetBoneEntitialFrameWithIndex(bone));
                hand.PressBody = body; hand.PressBone = bone; hand.PressAge = 0;
                hand.PressLocal = first.TransformToLocalNonOrthogonal(contact);
            }
            else
            {
                MatrixFrame boneWorld = body.Frame.TransformToParent(body.Skeleton.GetBoneEntitialFrameWithIndex(bone));
                contact = boneWorld.TransformToParent(hand.PressLocal);
                hand.PressAge += dt;
            }
            float overshoot = InteractionMath.Dot(tip - contact, axisN);
            Vec3 lateral = (tip - contact) - axisN * overshoot;
            if (overshoot < -.02f || lateral.Length > PressLateral || hand.PressAge > PressTimeout) { hand.PressBody = null; return; }
            BodyPoint point = body.ReadContact(bone);
            if (point.Part == BoneBodyPartType.None || !InteractionMath.IsFinite(point.World)) { hand.PressBody = null; return; }
            int tier = BodyPenetration.TierOf(hand.Item);
            PenetrationMaterial material = body.IsCorpse ? PenetrationMaterial.Flesh : BodyPenetration.MaterialFor(body.Agent, point.Part);
            PenetrationModifiers.Row row = PenetrationModifiers.Get(profile, material, tier);
            float relativeSpeed = (motion * (1 / dt) - point.Velocity).Length;
            float pressure = PenetrationJoint.Pressure(InteractionMath.Dot(motion, axisN) / dt, overshoot, hand.Item?.Weight ?? 1.5f);
            float threshold = PenetrationJoint.PressureThreshold(profile, relativeSpeed) * row.PressureForceMultiplier;
            if (pressure < threshold) return;
            hand.PressBody = null;
            if (EnemyPhysicsSettings.Trace)
                PhysicsLog.Info(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "PRESSURE reached pressure={0:F2} threshold={1:F2} overshoot={2:F3} relative_speed={3:F2} material={4}",
                    pressure, threshold, overshoot, relativeSpeed, material));
            hand.Penetration.Begin(body, point, contact, profile, relativeSpeed, player, true, tier);
            if (hand.Penetration.Body != null) PhysicsInterop.Haptic(index, .3f, .03f);
        }

        // Where the physics thinks the hand is, against the character's own hand
        // bone, plus what the hand is doing; validates the pose conversion of
        // the BannerlordVR release and shows which gate stops a contact.
        private static void Diagnose(int index, Hand hand, MatrixFrame pose, VrHandSlot slot, Agent player,
            WeaponComponentData usage, bool blade, BvrHandState input, float speed, float now)
        {
            hand.DiagSpeed = Math.Max(hand.DiagSpeed, speed);
            hand.DiagGrip = Math.Max(hand.DiagGrip, input.Grip);
            hand.DiagTrigger = Math.Max(hand.DiagTrigger, input.Trigger);
            foreach (EnemyBody body in Bodies)
                if (body != null) hand.DiagNearest = Math.Min(hand.DiagNearest, (body.Agent.Position + Vec3.Up - pose.origin).Length);
            Skeleton skeleton = player.AgentVisuals?.GetSkeleton();
            if (skeleton != null && slot.Bone >= 0 && slot.Bone < skeleton.GetBoneCount())
            {
                Vec3 bone = player.AgentVisuals.GetEntity().GetGlobalFrame().TransformToParent(skeleton.GetBoneEntitialFrameWithIndex(slot.Bone)).origin;
                hand.DiagBoneGap = (bone - pose.origin).Length;
            }
            hand.DiagItem = usage == null ? "empty" : (usage.WeaponClass + (blade ? "/blade" : ""));
            if (blade && VrBlade.TryGet(player, slot, out VrBlade.Segment seg) && seg.Valid)
                hand.DiagItem += string.Format(System.Globalization.CultureInfo.InvariantCulture, "/vrblade_len={0:F2}", seg.Length);
            if (now < hand.DiagNext) return;
            hand.DiagNext = now + 2;
            PhysicsLog.Info(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "HAND {0} item={1} pose=({2:F2},{3:F2},{4:F2}) gap_to_hand_bone_m={5:F2} max_speed={6:F2} max_grip={7:F2} max_trigger={8:F2} nearest_body_m={9:F2} casts={10} ray_hits={11} rejected={12} last_reject={13} hits={14} grabs={15}",
                index == 0 ? "L" : "R", hand.DiagItem, pose.origin.x, pose.origin.y, pose.origin.z, hand.DiagBoneGap, hand.DiagSpeed,
                hand.DiagGrip, hand.DiagTrigger, hand.DiagNearest == float.MaxValue ? -1 : hand.DiagNearest,
                hand.DiagCasts, hand.DiagRayHits, hand.DiagRejected, hand.DiagReject, hand.DiagHits, hand.DiagGrabs));
            hand.DiagSpeed = hand.DiagGrip = hand.DiagTrigger = 0; hand.DiagNearest = float.MaxValue;
            hand.DiagCasts = hand.DiagRayHits = hand.DiagRejected = hand.DiagHits = hand.DiagGrabs = 0; hand.DiagReject = "-";
        }
        // Contact query used by every gesture. The engine's closest-limb ray
        // returned nothing in VR against frozen sandbox dummies at 45 cm, so
        // the tracked bodies are also asked one by one, then tested against
        // their sampled bone spheres.
        public static string LastCastMethod { get; private set; } = "-";
        // A thrown blade's sweep: the tracked body and limb it meets first.
        public static bool CastTracked(Vec3 from, Vec3 to, Agent player, float thickness, out EnemyBody body, out BodyPoint point, out float fraction)
        {
            body = null; point = default; fraction = 1;
            if (_mission == null || player == null) return false;
            float length = (to - from).Length;
            if (length < 1e-5f) return false;
            Agent agent = CastLimbs(from, to, player.Index, thickness, out float distance, out sbyte bone);
            if (agent == null || bone < 0 || !InteractionMath.IsFinite(distance) || distance > length) return false;
            foreach (EnemyBody candidate in Bodies) if (candidate?.Agent == agent) { body = candidate; break; }
            if (body == null || bone >= body.Skeleton.GetBoneCount()) return false;
            point = body.ReadContact(bone);
            fraction = distance / length;
            return point.Part != BoneBodyPartType.None;
        }
        public static PushEffect EvaluateThrownPush(EnemyBody body, BodyPoint point, Vec3 velocity) =>
            body.IsCorpse || _living == null ? PushEffect.None : _living.EvaluatePush(body, point, velocity, PushMaterial.Pierce);

        // Tested on its drawn bones: a filed corpse (no engine limbs) or a
        // body whose ragdoll the physics drives.
        private static bool DrawnOnly(EnemyBody body) => Filed(body) || (_living != null && _living.HasLease(body.Agent));
        private static bool Filed(EnemyBody body) => body.IsCorpse && (body.Agent.AgentVisuals == null || !body.Agent.AgentVisuals.IsValid());

        private static Agent CastLimbs(Vec3 from, Vec3 to, int excluded, float thickness, out float distance, out sbyte bone)
        {
            // A body that is physical (struck, held, pierced) is drawn where its
            // ragdoll is, while the engine's limbs stay with the hidden agent:
            // a swing through empty air hit an enemy a metre away. Such bodies
            // are tested on their drawn bones only.
            // The drawn capsules answer first for every tracked body: the
            // engine's limbs are a second body, off the drawn one.
            Agent drawnBest = null; float drawnDistance = float.MaxValue; sbyte drawnBone = -1;
            bool anyDrawn = false;
            foreach (EnemyBody body in Bodies)
            {
                // Living agents only: BodyOf reads the agent's visuals, which a
                // corpse may no longer have (a thrown blade among corpses
                // crashed in native code).
                if (body?.Agent == null || body.Agent.Index == excluded || body.IsCorpse || !body.Agent.IsActive()) continue;
                if (!DrawnBody.Cast(body.Agent, from, to, thickness, out float d, out sbyte b, out bool hit)) continue;
                anyDrawn = true;
                if (hit && d < drawnDistance) { drawnBest = body.Agent; drawnDistance = d; drawnBone = b; }
            }
            if (anyDrawn)
            {
                Agent corpse = CastFiled(from, to, excluded, thickness, out float cd, out sbyte cb);
                if (corpse != null && cd < drawnDistance) { drawnBest = corpse; drawnDistance = cd; drawnBone = cb; }
                distance = drawnBest == null ? 0 : drawnDistance; bone = drawnBone;
                if (drawnBest != null) LastCastMethod = "drawn_capsules";
                return drawnBest;
            }
            Agent agent = _mission.RayCastForClosestAgentsLimbs(from, to, excluded, thickness, out distance, out bone);
            if (agent != null && bone >= 0)
            {
                EnemyBody hitBody = null;
                foreach (EnemyBody candidate in Bodies) if (candidate?.Agent == agent) { hitBody = candidate; break; }
                if (hitBody == null || !DrawnOnly(hitBody)) { LastCastMethod = "closest_limbs"; return agent; }
            }
            float length = (to - from).Length;
            Agent best = null; float bestDistance = float.MaxValue; sbyte bestBone = -1;
            foreach (EnemyBody body in Bodies)
            {
                if (body?.Agent == null || body.Agent.Index == excluded) continue;
                if (!DrawnOnly(body))
                {
                    if (_mission.RayCastForGivenAgentsLimbs(from, to, body.Agent.Index, thickness, out float d, out sbyte b) &&
                        b >= 0 && InteractionMath.IsFinite(d) && d < bestDistance)
                    { best = body.Agent; bestDistance = d; bestBone = b; LastCastMethod = "given_limbs"; }
                    continue;
                }
                if (length < 1e-5f) continue;
                for (int i = 0; i < body.Count; i++)
                {
                    BodyPoint point = body.Points[i];
                    float helmet = point.Part == BoneBodyPartType.Head ? HelmetVolume.HeadMargin(body.Agent) : 0;
                    if (!InteractionMath.SegmentSphere(from, to, point.World, point.Radius + helmet + thickness, out float t)) continue;
                    float d = t * length;
                    if (d < bestDistance) { best = body.Agent; bestDistance = d; bestBone = point.Bone; LastCastMethod = "bone_spheres"; }
                }
            }
            distance = best == null ? 0 : bestDistance; bone = bestBone;
            return best;
        }

        // Corpses (and anything not living) are tested on their kept bone
        // spheres, never through BannerlordVR's visuals reads.
        private static Agent CastFiled(Vec3 from, Vec3 to, int excluded, float thickness, out float distance, out sbyte bone)
        {
            float length = (to - from).Length;
            Agent best = null; distance = float.MaxValue; bone = -1;
            if (length < 1e-5f) return null;
            foreach (EnemyBody body in Bodies)
            {
                if (body?.Agent == null || body.Agent.Index == excluded || (!body.IsCorpse && body.Agent.IsActive())) continue;
                for (int i = 0; i < body.Count; i++)
                {
                    BodyPoint point = body.Points[i];
                    if (!InteractionMath.SegmentSphere(from, to, point.World, point.Radius + thickness, out float t)) continue;
                    if (t * length < distance) { best = body.Agent; distance = t * length; bone = point.Bone; }
                }
            }
            return best;
        }

        private static void Reject(Hand hand, string reason) { hand.DiagRejected++; hand.DiagReject = reason; }

        private static void TryHit(int index, Hand hand, Vec3 start, Vec3 tip, WeaponComponentData usage,
            MissionWeapon weapon, EquipmentIndex slot, Agent player, float dt, float now, bool blade)
        {
            hand.DiagCasts++;
            EnemyBody hit = null; BodyPoint hitPoint = default; float earliest = 2, hitAlong = 0; bool hitCooled = false;
            Vec3 contact = Vec3.Zero, velocity = Vec3.Zero;
            int samples = blade ? Math.Min(32, Math.Max(2, (int)Math.Ceiling((tip - start).Length / .06f))) : 1;
            // A shield's face is about 0.22 m round its grip, a fist 6.5 cm.
            float thickness = blade ? .025f : usage != null && usage.IsShield ? .22f : .065f;
            for (int s = 0; s <= (blade ? samples : 0); s++)
            {
                float along = blade ? (float)s / samples : 0;
                Vec3 previous = hand.PreviousBase + (hand.PreviousTip - hand.PreviousBase) * along;
                Vec3 current = start + (tip - start) * along;
                float length = (current - previous).Length;
                if (length < 1e-5f) continue;
                // Query the engine's real limb colliders and returned bone, not
                // guessed spheres. Closest-agent selection also prevents hitting
                // an admitted enemy through a nearer ally or excluded rider.
                Agent agent = CastLimbs(previous, current, player.Index,
                    thickness, out float distance, out sbyte bone);
                if (agent == null || bone < 0 || !InteractionMath.IsFinite(distance) || distance < 0 || distance > length) continue;
                hand.DiagRayHits++; hand.DiagReject = "via_" + LastCastMethod;
                float fraction = distance / length;
                if (fraction >= earliest) continue;
                EnemyBody body = null;
                foreach (EnemyBody candidate in Bodies) if (candidate?.Agent == agent) { body = candidate; break; }
                if (body == null) { Reject(hand, "agent_not_tracked"); continue; }
                if (bone >= body.Skeleton.GetBoneCount()) { Reject(hand, "bone_range"); continue; }
                // A point driven along the blade is a new pierce attempt: B&S
                // tries penetration on every contact. The cooldown only spaces
                // impacts, so a first glancing touch no longer blocks the stab
                // that follows it.
                BodyPoint early = body.ReadContact(bone);
                Vec3 sampleVelocity = (current - previous) * (1 / dt) - early.Velocity;
                Vec3 bladeAxis = (tip - start).NormalizedCopy();
                bool piercing = blade && along >= .8f && usage.ThrustDamage > 0 &&
                    InteractionMath.Dot(sampleVelocity, bladeAxis) > sampleVelocity.Length * .7f;
                bool cooled = now - (index == 0 ? body.LastHitLeft : body.LastHitRight) < .35f;
                if (cooled && !piercing) { Reject(hand, "cooldown"); continue; }
                Vec3 c = previous + (current - previous) * fraction;
                if (!Visible(previous, c, body)) { Reject(hand, "occluded"); continue; }
                // A blade lying in or across the shield is on the shield, not the
                // body behind it: the whole blade is tested, not only this
                // sample's path (one shield touch was counted in a whole test).
                if (EquipmentBlocks(body, previous, current, fraction, thickness) ||
                    (blade && BladeMetShield(body.Agent, hand.PreviousBase, hand.PreviousTip, start, tip, thickness)))
                {
                    // B&S BrainModuleMelee: a weapon striking an attacking
                    // enemy's weapon or shield parries the attack.
                    Reject(hand, "equipment");
                    bool parried = _living != null && _living.Parry(body, length / dt);
                    // Heavy weapons push the blocker through the block.
                    if (_living != null && !body.IsCorpse && usage != null && !usage.IsShield)
                        parried |= _living.BlockTransfer(body, (current - previous) * (1 / dt), PhysicalHit.Material(usage, false),
                            2 + (weapon.IsEmpty || weapon.Item == null ? 0 : Math.Min(6, weapon.Item.Weight)));
                    PhysicsInterop.Haptic(index, parried ? .6f : .15f, parried ? .05f : .02f);
                    continue;
                }
                BodyPoint point = body.ReadContact(bone);
                if (point.Part == BoneBodyPartType.None || !InteractionMath.IsFinite(point.World)) continue;
                earliest = fraction; hit = body; hitPoint = point; contact = c; hitAlong = along; hitCooled = cooled;
                velocity = InteractionMath.ClampMagnitude((current - previous) * (1 / dt) - point.Velocity, 12);
            }
            if (hit == null) return;
            hand.DiagHits++;
            if (index == 0) hit.LastHitLeft = now; else hit.LastHitRight = now;
            bool thrust = blade && usage.ThrustDamage > 0 && InteractionMath.Dot(velocity, (tip - start).NormalizedCopy()) > velocity.Length * .7f;
            // B&S dismemberment: a fast cut across a joint slices and kills.
            if (blade && !thrust && !hit.IsCorpse && EnemyPhysicsSettings.ActiveRagdoll)
            {
                BodyPenetration.ProfileFor(usage, false, out PenetrationProfile sliceProfile);
                bool twoHanded = usage.WeaponFlags.HasAnyFlag(WeaponFlags.NotUsableWithOneHand);
                if (DismemberFx.TrySlice(player, hit, contact, start, tip, velocity, sliceProfile, twoHanded, false))
                {
                    PhysicsInterop.Haptic(index, .8f, .06f);
                    return;
                }
            }
            // Blade & Sorcery push: level from impact speed and material, effect
            // from the human hit reaction table and the enemy's current action.
            // B&S pushes only from a contact that deals damage (the material's
            // minVelocity): slow touches at 0.3-2 m/s staggered the enemy.
            float pushMin = usage == null ? PunchImpact.For(BodyPenetration.MaterialFor(hit.Agent, hitPoint.Part)).MinVelocity : 0;
            if (blade && !hit.IsCorpse)
            {
                BodyPenetration.ProfileFor(usage, thrust, out PenetrationProfile pushProfile);
                pushMin = PenetrationModifiers.Get(pushProfile, BodyPenetration.MaterialFor(hit.Agent, hitPoint.Part),
                    BodyPenetration.TierOf(weapon.Item)).DamageMinVelocity;
            }
            PushEffect push = hit.IsCorpse || _living == null || velocity.Length < pushMin ? PushEffect.None :
                _living.EvaluatePush(hit, hitPoint, velocity, PhysicalHit.Material(usage, thrust));
            // A thrust that enters is not an impact kill: its damage comes from
            // depth and skewering, and lands when the blade is pulled out.
            // A thrust enters through its point (B&S pierce damagers sit at the
            // tip), never through the middle of a blade swept into the body.
            bool atPoint = !blade || hitAlong >= .8f;
            bool enters = thrust && atPoint && !hit.IsCorpse && EnemyPhysicsSettings.ActiveRagdoll &&
                BodyPenetration.ProfileFor(usage, true, out PenetrationProfile entering) && PenetrationJoint.Get(entering).Pierce &&
                velocity.Length >= PenetrationJoint.MinVelocity(entering, BodyPenetration.MaterialFor(hit.Agent, hitPoint.Part), BodyPenetration.TierOf(weapon.Item));
            // Inside the impact cooldown only an actual entry counts.
            if (hitCooled && !enters) return;
            // DamageModifier minVelocity: a slower contact (a blade resting or
            // dragged against a body) deals no damage; it still pushes.
            bool damages = true;
            if (blade && !hit.IsCorpse)
            {
                BodyPenetration.ProfileFor(usage, thrust, out PenetrationProfile damageProfile);
                PenetrationModifiers.Row damageRow = PenetrationModifiers.Get(damageProfile,
                    BodyPenetration.MaterialFor(hit.Agent, hitPoint.Part), BodyPenetration.TierOf(weapon.Item));
                damages = velocity.Length >= damageRow.DamageMinVelocity;
            }
            if (!enters && damages) PhysicalHit.Apply(player, hit, hitPoint, contact, velocity, weapon, slot, thrust, push);
            // A blade under the material's first push speed only touches: B&S
            // resolves it as a plain contact. Its impulses lifted a body
            // over a blade drawn slowly across the head.
            bool pushes = usage == null || velocity.Length > PushReaction.Thresholds(PhysicalHit.Material(usage, thrust))[0];
            if (!hit.IsCorpse && pushes) ApplyLivingImpact(hit, hitPoint, velocity, push,
                2 + (weapon.IsEmpty || weapon.Item == null ? 0 : Math.Min(6, weapon.Item.Weight)), usage == null);
            if ((hit.IsCorpse || EnemyPhysicsSettings.LivingRagdolls || EnemyPhysicsSettings.ActiveRagdoll) &&
                (!thrust || atPoint) && BodyPenetration.ProfileFor(usage, thrust, out PenetrationProfile profile))
                hand.Penetration.Begin(hit, hitPoint, contact, profile, velocity.Length, player, false, BodyPenetration.TierOf(weapon.Item));
            if (usage == null)
            {
                // Damager_Punch intensity (2 to 9 m/s) drives the effect.
                float intensity = PunchImpact.Intensity(velocity.Length);
                PhysicsInterop.Haptic(index, .2f + .8f * intensity, .02f + .04f * intensity);
            }
            else PhysicsInterop.Haptic(index, .45f, .035f);
        }

        // A blade that met the shield anywhere on its way this tick: a fast
        // blow crosses the plate between two ticks, and by the time a limb is
        // hit the blade is already behind it (3 parries counted in a test).
        private static bool BladeMetShield(Agent agent, Vec3 prevBase, Vec3 prevTip, Vec3 baseNow, Vec3 tipNow, float radius)
        {
            EquipmentIndex off = agent.GetOffhandWieldedItemIndex();
            if (off == EquipmentIndex.None || agent.Equipment[off].IsEmpty || agent.Equipment[off].CurrentUsageItem?.IsShield != true) return false;
            Vec3 prevMid = (prevBase + prevTip) * .5f, midNow = (baseNow + tipNow) * .5f;
            return ItemBlocks(agent, off, baseNow, tipNow, 1, radius) || ItemBlocks(agent, off, prevBase, prevTip, 1, radius) ||
                ItemBlocks(agent, off, prevTip, tipNow, 1, radius) || ItemBlocks(agent, off, prevMid, midNow, 1, radius) ||
                ItemBlocks(agent, off, prevBase, baseNow, 1, radius);
        }

        private static bool EquipmentBlocks(EnemyBody body, Vec3 start, Vec3 end, float bodyFraction, float radius)
        {
            if (body.IsCorpse) return false;
            return ItemBlocks(body.Agent, body.Agent.GetOffhandWieldedItemIndex(), start, end, bodyFraction, radius) ||
                ItemBlocks(body.Agent, body.Agent.GetPrimaryWieldedItemIndex(), start, end, bodyFraction, radius);
        }

        private static bool ItemBlocks(Agent agent, EquipmentIndex slot, Vec3 start, Vec3 end, float bodyFraction, float radius)
        {
            if (slot == EquipmentIndex.None) return false;
            WeaponComponentData item = agent.Equipment[slot].CurrentUsageItem;
            if (item == null || (!item.IsShield && !item.IsMeleeWeapon)) return false;
            // A shield is where BannerlordVR draws it (its plate); the weapon
            // entity's box was 0.93 m away, and blows went past the shield.
            if (item.IsShield && VrBlade.TryGetEnemyPlate(agent, out VrBlade.Plate plate) && plate.Valid)
            {
                Vec3 a = plate.A.NormalizedCopy(), b = plate.B.NormalizedCopy(), n = plate.N.NormalizedCopy();
                Vec3 ls = new Vec3(InteractionMath.Dot(start - plate.Centre, a), InteractionMath.Dot(start - plate.Centre, b), InteractionMath.Dot(start - plate.Centre, n));
                Vec3 le = new Vec3(InteractionMath.Dot(end - plate.Centre, a), InteractionMath.Dot(end - plate.Centre, b), InteractionMath.Dot(end - plate.Centre, n));
                Vec3 half = new Vec3(plate.HalfA, plate.HalfB, Math.Max(plate.HalfN, ShieldPlate.MinHalfThickness));
                return InteractionMath.SegmentBox(ls, le, -half, half, radius, out float hit) && hit <= bodyFraction;
            }
            WeakGameEntity entity = agent.GetWeaponEntityFromEquipmentSlot(slot);
            if (!entity.IsValid) return false;
            MatrixFrame frame = entity.GetGlobalFrame();
            BoundingBox bounds = entity.GetLocalBoundingBox();
            // Some modded items have no usable bounds. Preserve a held shield
            // conservatively in that case; never invent a weapon mesh.
            if (!InteractionMath.IsFinite(bounds.min) || !InteractionMath.IsFinite(bounds.max) ||
                (bounds.max - bounds.min).LengthSquared < 1e-6f) return item.IsShield;
            float scale = Math.Min(frame.rotation.s.Length, Math.Min(frame.rotation.f.Length, frame.rotation.u.Length));
            if (scale < .001f) return item.IsShield;
            return InteractionMath.SegmentBox(frame.TransformToLocalNonOrthogonal(start),
                frame.TransformToLocalNonOrthogonal(end), bounds.min, bounds.max, radius / scale,
                out float fraction) && fraction <= bodyFraction;
        }
    }
}
