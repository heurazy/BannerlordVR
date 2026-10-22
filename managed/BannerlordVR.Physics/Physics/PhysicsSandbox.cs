using System;
using System.Collections.Generic;
using System.Globalization;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR.Physics
{
    // enemy_physics_sandbox=1: a custom battle becomes a test range. Once the
    // battle starts the player is put on foot, every AI agent is frozen where
    // it stands, and five enemy foot soldiers are lined up in front of the
    // player as passive dummies. The physics leases them normally; they stay
    // paused afterwards instead of rejoining the fight. With
    // enemy_physics_sandbox_duelists (1 by default) that many more enemies
    // are put further out, straight ahead, and left to their AI: they come
    // and attack the player (the only one fighting), to block and parry.
    internal sealed class PhysicsSandbox : MissionLogic
    {
        public const int Dummies = 5;
        private const float Distance = 3, Spacing = 1.4f;
        private bool _placed, _gaveUp;
        private float _began = -1, _nextHold;
        private readonly List<Agent> _dummies = new List<Agent>();
        private readonly HashSet<Agent> _duelists = new HashSet<Agent>();
        private const float DuelDistance = 6;
        private readonly Dictionary<Agent, Vec2> _hold = new Dictionary<Agent, Vec2>();

        public override void OnMissionTick(float dt)
        {
            try { Tick(); }
            catch (Exception ex)
            {
                _gaveUp = true;
                PhysicsLog.Error("Physics sandbox stopped after an error; the battle continues normally.", ex);
            }
        }

        private void Tick()
        {
            if (_gaveUp || Mission.Mode != MissionMode.Battle) return;
            Agent player = Mission.MainAgent;
            if (player == null || !player.IsActive()) return;
            if (Game.Current?.GameType?.GetType().FullName != "TaleWorlds.MountAndBlade.CustomBattle.CustomGame")
            { _gaveUp = true; PhysicsLog.Info("SANDBOX refused: only in a custom battle."); return; }
            if (_began < 0) { _began = Mission.CurrentTime; PhysicsLog.Info("SANDBOX battle started; freezing the AI and dismounting the player."); }
            // A player has no AI to pause; ask for the dismount until it lands.
            if (player.HasMount)
            {
                if (Mission.CurrentTime - _began < 5) player.EventControlFlags |= Agent.EventControlFlag.Dismount;
                else if (!_placed) PhysicsLog.Info("SANDBOX: the player is still mounted after 5 s; the physics stays off on horseback.");
            }
            if (Mission.CurrentTime >= _nextHold)
            {
                _nextHold = Mission.CurrentTime + .1f;
                Freeze(player);
            }
            if (!_placed && !player.HasMount && Mission.CurrentTime - _began > .5f) Place(player);
            TraceDummies();
        }

        // Every AI agent stands still where it is (or where it was placed),
        // except while the physics owns it.
        private void Freeze(Agent player)
        {
            LivingRagdollMissionLogic living = Mission.GetMissionBehavior<LivingRagdollMissionLogic>();
            foreach (Agent agent in Mission.Agents)
            {
                if (agent == player || !agent.IsActive() || agent.IsMount || agent.IsPlayerControlled) continue;
                if (_duelists.Contains(agent)) { Unleash(agent); continue; }
                if (living != null && living.HasLease(agent)) { _leased.Add(agent); continue; }
                // Back from the physics: it stands where the body was left.
                if (_leased.Remove(agent)) _hold[agent] = agent.Position.AsVec2;
                if (!_hold.TryGetValue(agent, out Vec2 at)) { at = agent.Position.AsVec2; _hold[agent] = at; }
                Hold(agent, at);
            }
        }

        // The agent stands on its own spot as its target. A speed limit of 0
        // with a target it could not reach kept its legs walking on the spot
        // (the physics then captured that stride as the pose to hold); with no
        // target at all the engine walked it straight ahead at 3-4 m/s. With
        // its spot as target and no speed limit it simply arrives and idles.
        private readonly HashSet<Agent> _leased = new HashSet<Agent>();
        // Paused AI still steered: it walked on the spot towards an
        // unreachable target, straight ahead without one, and kept a forward
        // input (0,1) at 0.85 m/s towards its own spot. A dummy has no
        // controller at all, so nothing gives it a movement input.
        public static void Hold(Agent agent, Vec2 at)
        {
            if (agent.Controller != AgentControllerType.None) agent.Controller = AgentControllerType.None;
            agent.MovementFlags = 0;
            agent.MovementInputVector = Vec2.Zero;
        }

        private float _nextTrace;
        private void TraceDummies()
        {
            if (!EnemyPhysicsSettings.Trace || Mission.CurrentTime < _nextTrace || _dummies.Count == 0) return;
            _nextTrace = Mission.CurrentTime + 3;
            Agent a = _dummies[0];
            if (a == null || !a.IsActive()) return;
            PhysicsLog.Info(string.Format(CultureInfo.InvariantCulture,
                "SANDBOX dummy {0} velocity={1:F2} input=({2:F2},{3:F2}) flags={4} action0={5} action1={6}",
                a.Index, a.MovementVelocity.Length, a.MovementInputVector.x, a.MovementInputVector.y, a.MovementFlags,
                a.GetCurrentAction(0).GetName(), a.GetCurrentAction(1).GetName()));
        }

        private void Place(Agent player)
        {
            _placed = true;
            Vec3 forward = player.LookDirection; forward.z = 0;
            if (forward.LengthSquared < 1e-4f) forward = Vec3.Forward;
            forward = forward.NormalizedCopy();
            Vec3 side = new Vec3(-forward.y, forward.x, 0);
            var candidates = new List<Agent>();
            foreach (Agent agent in Mission.Agents)
                if (agent.IsActive() && agent != player && !agent.IsPlayerControlled && agent.IsHuman && !agent.HasMount &&
                    player.Team != null && agent.Team != null && agent.Team.IsEnemyOf(player.Team))
                    candidates.Add(agent);
            candidates.Sort((x, y) => (x.Position - player.Position).LengthSquared.CompareTo((y.Position - player.Position).LengthSquared));
            for (int i = 0; i < Math.Min(Dummies, candidates.Count); i++)
            {
                Agent dummy = candidates[i];
                Vec3 at = player.Position + forward * Distance + side * ((i - (Dummies - 1) * .5f) * Spacing);
                at.z = Mission.Scene.GetGroundHeightAtPosition(at);
                if (!InteractionMath.IsFinite(at)) continue;
                dummy.TeleportToPosition(at);
                dummy.LookDirection = -forward;
                _hold[dummy] = at.AsVec2;
                _dummies.Add(dummy);
            }
            int duelists = Math.Min(EnemyPhysicsSettings.SandboxDuelists, Math.Max(0, candidates.Count - _dummies.Count));
            for (int i = 0; i < duelists; i++)
            {
                Agent duelist = candidates[_dummies.Count + i];
                Vec3 at = player.Position + forward * DuelDistance + side * ((i - (duelists - 1) * .5f) * Spacing);
                at.z = Mission.Scene.GetGroundHeightAtPosition(at);
                if (!InteractionMath.IsFinite(at)) continue;
                duelist.TeleportToPosition(at);
                duelist.LookDirection = -forward;
                _duelists.Add(duelist);
                Unleash(duelist);
                PhysicsLog.Info("SANDBOX duelist " + duelist.Index + " (" + (duelist.WieldedWeapon.Item?.StringId ?? "unarmed") + ") " +
                    DuelDistance.ToString("F0", CultureInfo.InvariantCulture) + " m ahead, coming to attack the player");
            }
            PhysicsLog.Info(string.Format(CultureInfo.InvariantCulture,
                "SANDBOX ready: {0} enemy dummies {1:F1} m in front of the player, {2:F1} m apart; all AI frozen. Punch, grab, stab, cut.",
                _dummies.Count, Distance, Spacing));
        }

        // A duelist keeps its AI, out of its formation, alarmed: with every
        // other agent frozen, the player is the enemy it goes for.
        private static void Unleash(Agent agent)
        {
            if (agent.Controller != AgentControllerType.AI) agent.Controller = AgentControllerType.AI;
            if (agent.IsPaused) agent.IsPaused = false;
            agent.SetWatchState(Agent.WatchState.Alarmed);
            if (agent.IsFormationFrameEnabled) agent.SetFormationFrameDisabled();
        }

        public override void OnAgentRemoved(Agent affectedAgent, Agent affectorAgent, AgentState agentState, KillingBlow blow)
        {
            if (_duelists.Remove(affectedAgent)) PhysicsLog.Info("SANDBOX duelist " + affectedAgent.Index + " is down (" + agentState + ").");
            if (_dummies.Remove(affectedAgent))
                PhysicsLog.Info("SANDBOX dummy " + affectedAgent.Index + " is down (" + agentState + "); " + _dummies.Count + " left.");
            _hold.Remove(affectedAgent);
        }
    }
}
