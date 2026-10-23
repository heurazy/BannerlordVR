using System;
using System.Collections.Generic;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR.Physics
{
    // Blade & Sorcery EffectModuleReveal / RevealDecal: every cut and stab
    // leaves a wound where it landed, on the skin or the armour, that stays
    // and moves with the body. Bannerlord has no reveal mask on its skinned
    // meshes, its blood decals are terrain decals, and a mesh put on a bone
    // with Skeleton.AddMeshToBone was not drawn (both tried in game, 23 Oct
    // 2026). A small quad of the game's blood texture is built the way
    // BannerlordVR builds the meshes it draws (default material copy,
    // ManagedMeshEditOperations, FinalizeEditing, on a dynamic scene entity)
    // and that entity is put back on the struck bone every frame, after the
    // engine posed the skeleton: round for a stab, drawn along the swing for
    // a cut.
    internal static class WoundDecal
    {
        // The game's blood splatters are terrain decals: their colour maps are
        // dark, the blood in the alpha the default material ignores (drawn
        // black in game). BannerlordVR's own drawn meshes are plain_white
        // under vertex colours; a wound is drawn that way, as a blob of
        // blood with a ragged edge.
        private const int RimPoints = 12;
        // BannerlordVR colours its meshes with Mesh.Color (ARGB) over vertex
        // colours: the blood red is the mesh's, the vertices only shade it,
        // the core darker than the rim.
        private const uint Blood = 0xFF9A0E0E;
        private static readonly Vec3 Core = new Vec3(.55f, .55f, .55f), Rim = new Vec3(1, 1, 1);
        private const int PerAgent = 10, Total = 60;
        private const float StabSize = .07f, CutLength = .16f, CutWidth = .05f;
        // The contact is on the body's collision capsule, under the armour:
        // the wound is laid out on the armour's surface (a mail sleeve or a
        // plate covered it in game).
        private static float Lift(Agent agent, BoneBodyPartType part)
        {
            if (part == BoneBodyPartType.Head && agent.SpawnEquipment != null && !agent.SpawnEquipment[EquipmentIndex.Head].IsEmpty)
                return HelmetVolume.Margin + .01f;
            switch (BodyPenetration.MaterialFor(agent, part))
            {
                case PenetrationMaterial.Leather: return .025f;
                case PenetrationMaterial.Chainmail: return .03f;
                case PenetrationMaterial.Plate: return .045f;
                default: return .015f;
            }
        }
        private struct Wound { public Agent Agent; public sbyte Bone; public MatrixFrame Local; public GameEntity Entity; }
        private static readonly List<Wound> Wounds = new List<Wound>();
        private static Material _material;
        private static Mission _mission;
        private static bool _broken, _logged, _searched;

        // At each mission start (loading), so the texture is in before a hit.
        internal static void ForgetMission() { Wounds.Clear(); _mission = null; if (EnemyPhysicsSettings.Wounds) FindMaterial(); }

        public static void Stab(Agent agent, sbyte bone, Vec3 contact, Vec3 direction) => Add(agent, bone, contact, direction, false);
        public static void Cut(Agent agent, sbyte bone, Vec3 contact, Vec3 direction) => Add(agent, bone, contact, direction, true);

        private static Material FindMaterial()
        {
            if (_searched) return _material;
            _searched = true;
            try
            {
                Material source = Material.GetDefaultMaterial();
                Texture white = Texture.GetFromResource("plain_white");
                if (source == null || white == null) { PhysicsLog.Info("WOUND no default material or plain_white; wounds are not drawn."); return null; }
                white.PreloadTexture(true);
                _material = source.CreateCopy();
                _material.SetTexture(Material.MBTextureType.DiffuseMap, white);
                PhysicsLog.Info("WOUND mesh material default with plain_white under vertex colours");
            }
            catch (Exception ex) { Fail(ex.Message); }
            return _material;
        }

        // A fan in its own frame (x along, y across, z out of the body): a
        // dark core and a ragged rim, its points at random radii; a cut's
        // frame stretches it into a slit.
        private static Mesh Blob(Material material, bool cut)
        {
            Mesh mesh = Mesh.CreateMeshWithMaterial(material);
            ManagedMeshEditOperations ops = ManagedMeshEditOperations.Create(mesh);
            Vec3 up = new Vec3(0, 0, 1), down = new Vec3(0, 0, -1), centre = Vec3.Zero;
            Vec2 uv = new Vec2(.5f, .5f);
            var rim = new Vec3[RimPoints];
            for (int k = 0; k < RimPoints; k++)
            {
                double angle = 2 * Math.PI * k / RimPoints;
                float r = .5f * MBRandom.RandomFloatRanged(cut ? .8f : .6f, 1f);
                rim[k] = new Vec3((float)Math.Cos(angle) * r, (float)Math.Sin(angle) * r, 0);
            }
            for (int k = 0; k < RimPoints; k++)
            {
                Vec3 a = rim[k], b = rim[(k + 1) % RimPoints];
                ops.AddTriangle(centre, a, b, up, up, up, uv, uv, uv, Core, Rim, Rim);
                // The back face too: which way the engine culls is not known here.
                ops.AddTriangle(centre, b, a, down, down, down, uv, uv, uv, Core, Rim, Rim);
            }
            ops.FinalizeEditing();
            mesh.RecomputeBoundingBox();
            mesh.Color = Blood;
            return mesh;
        }

        private static void Add(Agent agent, sbyte bone, Vec3 contact, Vec3 direction, bool cut)
        {
            if (_broken || !EnemyPhysicsSettings.Wounds || agent?.AgentVisuals == null || bone < 0 || !InteractionMath.IsFinite(contact)) return;
            try
            {
                Mission mission = Mission.Current;
                Scene scene = mission?.Scene;
                if (scene == null) return;
                if (mission != _mission) { Wounds.Clear(); _mission = mission; }
                Material material = FindMaterial();
                if (material == null) return;
                Skeleton skeleton = agent.AgentVisuals.GetSkeleton();
                if (skeleton == null || bone >= skeleton.GetBoneCount()) return;
                MatrixFrame visuals = agent.AgentVisuals.GetGlobalFrame();
                MatrixFrame boneWorld = visuals.TransformToParent(skeleton.GetBoneEntitialFrameWithIndex(bone));
                // Out of the body: from the bone's line to the contact; a hit
                // on the line itself faces back along the blow.
                Vec3 a = boneWorld.origin, b = a;
                if (skeleton.GetBoneChildCount(bone) > 0)
                    b = visuals.TransformToParent(skeleton.GetBoneEntitialFrameWithIndex(skeleton.GetBoneChildAtIndex(bone, 0))).origin;
                Vec3 ab = b - a;
                float t = ab.LengthSquared > 1e-6f ? Math.Max(0, Math.Min(1, InteractionMath.Dot(contact - a, ab) / ab.LengthSquared)) : 0;
                Vec3 normal = contact - (a + ab * t);
                if (normal.Length < .02f) normal = -direction;
                if (normal.Length < 1e-4f) return;
                normal.Normalize();
                // Across the surface: a cut is drawn along the blow's path.
                Vec3 along = direction - normal * InteractionMath.Dot(direction, normal);
                if (along.Length < 1e-3f) along = Math.Abs(normal.z) < .9f ? Vec3.Up - normal * normal.z : Vec3.Forward;
                along.Normalize();
                Vec3 side = Vec3.CrossProduct(normal, along);
                float jitter = MBRandom.RandomFloatRanged(.8f, 1.2f);
                float length = (cut ? CutLength : StabSize) * jitter, width = (cut ? CutWidth : StabSize) * jitter;
                BoneBodyPartType part = agent.AgentVisuals.GetBoneTypeData(bone).BodyPartType;
                var world = new MatrixFrame(new Mat3(along * length, side * width, normal), contact + normal * Lift(agent, part));
                MatrixFrame local = boneWorld.TransformToLocalNonOrthogonal(world);
                GameEntity entity = GameEntity.CreateEmptyDynamic(scene, false);
                entity.AddMesh(Blob(material, cut), true);
                entity.SetGlobalFrame(in world);
                Wounds.Add(new Wound { Agent = agent, Bone = bone, Local = local, Entity = entity });
                Trim(agent);
                if (!_logged)
                {
                    _logged = true;
                    PhysicsLog.Info("WOUND first wound on agent " + agent.Index + " bone " + skeleton.GetBoneName(bone) + (cut ? " (cut)" : " (stab)") +
                        " at (" + contact.x.ToString("F2") + "," + contact.y.ToString("F2") + "," + contact.z.ToString("F2") + ")");
                }
            }
            catch (Exception ex) { Fail(ex.Message); }
        }

        // After the agents' tick: each wound back on its bone. A body whose
        // visuals are gone (a filed corpse) keeps its wounds where they are.
        public static void Tick()
        {
            if (_broken || Wounds.Count == 0) return;
            if (Mission.Current != _mission) { Wounds.Clear(); return; }
            try
            {
                foreach (Wound w in Wounds)
                {
                    MBAgentVisuals visuals = w.Agent?.AgentVisuals;
                    if (visuals == null || !visuals.IsValid()) continue;
                    Skeleton skeleton = visuals.GetSkeleton();
                    if (skeleton == null || w.Bone >= skeleton.GetBoneCount()) continue;
                    MatrixFrame frame = visuals.GetGlobalFrame().TransformToParent(skeleton.GetBoneEntitialFrameWithIndex(w.Bone)).TransformToParent(w.Local);
                    w.Entity.SetGlobalFrame(in frame);
                }
            }
            catch (Exception ex) { Fail(ex.Message); }
        }

        // Oldest first: ten on one body, sixty in the battle.
        private static void Trim(Agent agent)
        {
            int mine = 0;
            for (int i = Wounds.Count - 1; i >= 0; i--) if (Wounds[i].Agent == agent && ++mine > PerAgent) Remove(i);
            while (Wounds.Count > Total) Remove(0);
        }
        private static void Remove(int i)
        {
            Wound w = Wounds[i];
            Wounds.RemoveAt(i);
            try { w.Entity?.Remove(79); } catch (Exception) { }
        }

        private static void Fail(string why)
        {
            _broken = true;
            PhysicsLog.Info("WOUND wounds stopped for the session: " + why);
        }
    }
}
