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
            public Hand(int index) { Grab.HandIndex = index; }
            public readonly BodyPenetration Penetration = new BodyPenetration();
            public readonly CutStroke Cut = new CutStroke();
            public bool Sampled, GripDown, SuppressTrigger;
            public Vec3 Previous, PreviousBase, PreviousTip, Stage, Axis;
            public ItemObject Item;
            public EquipmentIndex Slot;
            public void Reset() { Grab.Release(); Penetration.Release(); Cut.Reset(); Sampled = GripDown = SuppressTrigger = false; Item = null; Slot = EquipmentIndex.None; }
        }
        private static readonly EnemyBody[] Bodies = new EnemyBody[EnemyPhysicsSettings.Capacity];
        private static readonly EnemyBody[] Records = CreateRecords();
        private static readonly Hand[] Hands = { new Hand(0), new Hand(1) };
        private static readonly AdaptivePhysicsBudget Budget = new AdaptivePhysicsBudget(
            EnemyPhysicsSettings.Capacity, EnemyPhysicsSettings.BudgetMilliseconds);
        private static long _workStarted;
        private static Mission _mission;
        private static LivingRagdollMissionLogic _living;
        private static BleedingMissionLogic _bleeding;
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
        public static bool SupportsLivingBody => _living?.Available == true;
        public static bool OwnsLivingPhysics(Agent agent) => _living?.Simulating(agent) == true;
        public static bool HasLivingLease(Agent agent) => _living?.HasLease(agent) == true;
        public static bool BeginLivingGrab(EnemyBody body) => !body.IsCorpse && _living?.KeepHeld(body, true) == true;
        public static void MaintainLivingGrab(EnemyBody body) { if (!body.IsCorpse) _living?.KeepHeld(body); }
        // PhysX steps counted by the scene clock, or -1 when no clock runs.
        public static long PhysicsSteps => _living?.PhysicsSteps ?? -1;
        public static float PartMass(EnemyBody body, sbyte bone) => _living?.PartMass(body.Agent, bone) ?? 5;
        public static bool HandleActiveGrab(EnemyBody body, int hand, BodyPoint point, Vec3 handWorld, MatrixFrame boneWorld, Vec3 contact)
            => !body.IsCorpse && _living != null && _living.HandleActive(body, hand, point, handWorld, boneWorld, contact);
        public static void ReleaseActiveGrab(EnemyBody body, int hand) { if (body != null && !body.IsCorpse) _living?.ReleaseActive(body, hand); }
        public static void ApplyLivingImpact(EnemyBody body, BodyPoint point, Vec3 velocity,
            PushEffect push = PushEffect.None, float hitMass = 2)
        { if (!body.IsCorpse) _living?.Impact(body, point, velocity, push, hitMass); }
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
            foreach (Hand hand in Hands) hand.Reset();
            Array.Clear(Bodies, 0, Bodies.Length);
            foreach (EnemyBody record in Records) record.Clear();
            _mission = null; _player = null; _cursor = 0; _time = -1;
            _living = null;
            _bleeding = null;
            HandlingCombat = false; ActiveBodies = 0;
        }

        public static void Tick(Mission mission, float dt)
        {
            bool measured = false;
            try
            {
                Agent player = mission?.MainAgent;
                if (_failed || !EnemyPhysicsSettings.Enabled || !VrMotionHands.Enabled || !VrSystem.IsActive ||
                    mission == null || mission.MissionEnded || mission.Mode != MissionMode.Battle ||
                    player == null || !player.IsActive() || player.HasMount ||
                    GameNetwork.IsMultiplayer || !VrCameraDriver.HasPose ||
                    VrMissionPhase.UiWantsScreen || VrMissionPhase.UiWantsOverlay || VrHandCalibration.Capturing ||
                    PhysicsInterop.OverlayVisible())
                { Reset(); return; }
                if (_mission != mission || _player != player)
                {
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
                    if (!EnemyBody.Eligible(body.Agent, player, _living?.HasLease(body.Agent) == true) ||
                        (body.Agent.Position - player.Position).Length > EnemyPhysicsSettings.ExitRadius || !body.Sample(dt))
                        Remove(i);
                    else ActiveBodies++;
                }
                ShedUnheldBodies(player);
                Scan(mission, player, dt);
                if (PhysicsInterop.GetInputState(out BvrInputState input) != (int)BvrStatus.Ok)
                { ClearHands(); return; }
                var skeleton = player.AgentVisuals?.GetSkeleton();
                if (skeleton == null || !VrHandSlots.Resolve(player, skeleton, out VrHandSlot main, out VrHandSlot off))
                { ClearHands(); return; }
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
        private static bool Held(EnemyBody body) => Hands[0].Grab.Body == body || Hands[1].Grab.Body == body ||
            Hands[0].Penetration.Body == body || Hands[1].Penetration.Body == body;

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
                if (found || chosen < 0 || !EnemyBody.Eligible(agent, player)) continue;
                // Reuse the record and its arrays after releasing any stale reference.
                EnemyBody candidate = Records[chosen];
                bool occupied = Bodies[chosen] != null;
                Remove(chosen);
                if (candidate.Bind(agent) && candidate.Sample(dt))
                { Bodies[chosen] = candidate; if (!occupied) ActiveBodies++; }
                else { candidate.Clear(); if (occupied) ActiveBodies--; }
            }
            ActiveBodies = 0;
            for (int i = 0; i < Bodies.Length; i++) if (Bodies[i] != null) ActiveBodies++;
        }

        private static void UpdateHand(int index, BvrHandState input, BvrHandState otherInput, VrHandSlot slot, Agent player, float dt, float now)
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
                if (newGrab) TryGrab(hand, pose.origin);
                // Use stage-space displacement to reject locomotion-only punches.
                float gestureSpeed = InteractionMath.GestureSpeed(hand.Stage, stage, hand.Axis, stageAxis,
                    blade ? (tip - basePoint).Length : 0, VrCameraDriver.HandScale, dt);
                bool embedded = blade && hand.Penetration.Tick(basePoint, tip, dt);
                if (embedded && hand.Penetration.LastResistance > 40)
                    PhysicsInterop.Haptic(index, Math.Min(1, hand.Penetration.LastResistance / 600), .02f);
                bool drawing = !embedded && TryNeckCut(index, hand, otherInput, input.Trigger, basePoint, tip,
                    usage, weapon, slot.Equipment, player, gestureSpeed * dt, dt, now);
                if (!embedded && !drawing && !hand.Grab.Held && gestureSpeed >= (blade ? .6f : 1.4f) &&
                    (blade || (usage == null && input.Trigger >= .55f)))
                    TryHit(index, hand, basePoint, tip, usage, weapon, slot.Equipment, player, dt, now, blade);
                else if (!embedded && !drawing && blade && EnemyPhysicsSettings.ActiveRagdoll)
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
            Agent agent = _mission.RayCastForClosestAgentsLimbs(edgeStart, tip, player.Index,
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
                    // The pose fallback supports head/hands; the native living
                    // backend exposes the full admitted body when explicitly enabled.
                    if (!body.IsCorpse && !SupportsLivingBody && p.Part != BoneBodyPartType.Head &&
                        p.Part != BoneBodyPartType.Neck && !name.EndsWith("hand")) continue;
                    float d = (p.World - position).Length;
                    if (d < distance && Visible(position, p.World, body)) { distance = d; best = body; point = i; }
                }
            }
            if (best != null && (!best.IsCorpse || best.HasNativePhysics)) hand.Grab.Begin(best, point, position);
        }

        // Damager.TryHitByPressure: the point pushed slowly into a body along
        // the blade (within penetrationPressureMaxDot 0.2 of its axis) enters
        // without a strike; damage then comes from depth.
        private const float PressureMaxDot = .2f, PressureMinSpeed = .05f;
        private static void TryPressure(int index, Hand hand, Vec3 start, Vec3 tip, WeaponComponentData usage, Agent player, float dt)
        {
            if (!hand.Sampled || dt <= 0 || !BodyPenetration.ProfileFor(usage, true, out PenetrationProfile profile) ||
                !PenetrationJoint.Get(profile).Pierce) return;
            Vec3 axis = tip - start;
            Vec3 motion = tip - hand.PreviousTip;
            float length = motion.Length;
            if (axis.Length < .05f || length / dt < PressureMinSpeed) return;
            if (InteractionMath.Dot(motion * (1 / length), axis.NormalizedCopy()) < 1 - PressureMaxDot) return;
            Agent agent = _mission.RayCastForClosestAgentsLimbs(hand.PreviousTip, tip, player.Index, .02f,
                out float distance, out sbyte bone);
            if (agent == null || bone < 0 || !InteractionMath.IsFinite(distance) || distance < 0 || distance > length) return;
            EnemyBody body = null;
            foreach (EnemyBody candidate in Bodies) if (candidate?.Agent == agent) { body = candidate; break; }
            if (body == null || bone >= body.Skeleton.GetBoneCount()) return;
            Vec3 contact = hand.PreviousTip + motion * (distance / length);
            if (!Visible(hand.PreviousTip, contact, body) || EquipmentBlocks(body, hand.PreviousTip, tip, distance / length, .02f)) return;
            BodyPoint point = body.ReadContact(bone);
            if (point.Part == BoneBodyPartType.None || !InteractionMath.IsFinite(point.World)) return;
            hand.Penetration.Begin(body, point, contact, profile, length / dt, player, true);
            if (hand.Penetration.Body != null) PhysicsInterop.Haptic(index, .3f, .03f);
        }

        private static void TryHit(int index, Hand hand, Vec3 start, Vec3 tip, WeaponComponentData usage,
            MissionWeapon weapon, EquipmentIndex slot, Agent player, float dt, float now, bool blade)
        {
            EnemyBody hit = null; BodyPoint hitPoint = default; float earliest = 2;
            Vec3 contact = Vec3.Zero, velocity = Vec3.Zero;
            int samples = blade ? Math.Min(32, Math.Max(2, (int)Math.Ceiling((tip - start).Length / .06f))) : 1;
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
                Agent agent = _mission.RayCastForClosestAgentsLimbs(previous, current, player.Index,
                    blade ? .025f : .065f, out float distance, out sbyte bone);
                if (agent == null || bone < 0 || !InteractionMath.IsFinite(distance) || distance < 0 || distance > length) continue;
                float fraction = distance / length;
                if (fraction >= earliest) continue;
                EnemyBody body = null;
                foreach (EnemyBody candidate in Bodies) if (candidate?.Agent == agent) { body = candidate; break; }
                if (body == null || now - (index == 0 ? body.LastHitLeft : body.LastHitRight) < .35f ||
                    bone >= body.Skeleton.GetBoneCount()) continue;
                Vec3 c = previous + (current - previous) * fraction;
                if (!Visible(previous, c, body)) continue;
                if (EquipmentBlocks(body, previous, current, fraction, blade ? .025f : .065f))
                {
                    // B&S BrainModuleMelee: a weapon striking an attacking
                    // enemy's weapon or shield parries the attack.
                    bool parried = _living != null && _living.Parry(body, length / dt);
                    PhysicsInterop.Haptic(index, parried ? .6f : .15f, parried ? .05f : .02f);
                    continue;
                }
                BodyPoint point = body.ReadContact(bone);
                if (point.Part == BoneBodyPartType.None || !InteractionMath.IsFinite(point.World)) continue;
                earliest = fraction; hit = body; hitPoint = point; contact = c;
                velocity = InteractionMath.ClampMagnitude((current - previous) * (1 / dt) - point.Velocity, 12);
            }
            if (hit == null) return;
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
            PushEffect push = hit.IsCorpse || _living == null ? PushEffect.None :
                _living.EvaluatePush(hit, hitPoint, velocity, PhysicalHit.Material(usage, thrust));
            PhysicalHit.Apply(player, hit, hitPoint, contact, velocity, weapon, slot, thrust, push);
            if (!hit.IsCorpse) ApplyLivingImpact(hit, hitPoint, velocity, push,
                2 + (weapon.IsEmpty || weapon.Item == null ? 0 : Math.Min(6, weapon.Item.Weight)));
            if ((hit.IsCorpse || EnemyPhysicsSettings.LivingRagdolls || EnemyPhysicsSettings.ActiveRagdoll) &&
                BodyPenetration.ProfileFor(usage, thrust, out PenetrationProfile profile))
                hand.Penetration.Begin(hit, hitPoint, contact, profile, velocity.Length, player);
            PhysicsInterop.Haptic(index, .45f, .035f);
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
