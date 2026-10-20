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
    // paused afterwards instead of rejoining the fight.
    internal sealed class PhysicsSandbox : MissionLogic
    {
        public const int Dummies = 5;
        private const float Distance = 3, Spacing = 1.4f;
        private bool _placed, _gaveUp;
        private float _began = -1, _nextHold;
        private readonly List<Agent> _dummies = new List<Agent>();
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
                _nextHold = Mission.CurrentTime + .25f;
                Freeze(player);
            }
            if (!_placed && !player.HasMount && Mission.CurrentTime - _began > .5f) Place(player);
        }

        // Every AI agent stands still where it is (or where it was placed),
        // except while the physics owns it.
        private void Freeze(Agent player)
        {
            LivingRagdollMissionLogic living = Mission.GetMissionBehavior<LivingRagdollMissionLogic>();
            foreach (Agent agent in Mission.Agents)
            {
                if (agent == player || !agent.IsActive() || !agent.IsAIControlled || agent.IsMount) continue;
                if (living != null && living.HasLease(agent)) continue;
                if (!_hold.TryGetValue(agent, out Vec2 at)) { at = agent.Position.AsVec2; _hold[agent] = at; }
                agent.SetIsAIPaused(true);
                agent.SetMaximumSpeedLimit(0, false);
                agent.SetTargetPosition(at);
                agent.MovementInputVector = Vec2.Zero;
            }
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
                if (agent.IsActive() && agent.IsAIControlled && agent.IsHuman && !agent.HasMount &&
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
            PhysicsLog.Info(string.Format(CultureInfo.InvariantCulture,
                "SANDBOX ready: {0} enemy dummies {1:F1} m in front of the player, {2:F1} m apart; all AI frozen. Punch, grab, stab, cut.",
                _dummies.Count, Distance, Spacing));
        }

        public override void OnAgentRemoved(Agent affectedAgent, Agent affectorAgent, AgentState agentState, KillingBlow blow)
        {
            if (_dummies.Remove(affectedAgent))
                PhysicsLog.Info("SANDBOX dummy " + affectedAgent.Index + " is down (" + agentState + "); " + _dummies.Count + " left.");
            _hold.Remove(affectedAgent);
        }
    }
}
