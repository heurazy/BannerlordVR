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
        // The game's twelve blood splatters (terrain decal diffuse maps).
        private const int Variants = 12;
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
        private static readonly List<Material> _materials = new List<Material>();
        private static Mission _mission;
        private static bool _broken, _logged, _searched;

        // At each mission start (loading): the splatters are streamed, and one
        // drawn before it loaded was a flat black square.
        internal static void ForgetMission() { Wounds.Clear(); _mission = null; if (EnemyPhysicsSettings.Wounds) FindMaterials(); }

        public static void Stab(Agent agent, sbyte bone, Vec3 contact, Vec3 direction) => Add(agent, bone, contact, direction, false);
        public static void Cut(Agent agent, sbyte bone, Vec3 contact, Vec3 direction) => Add(agent, bone, contact, direction, true);

        private static Material FindMaterial()
        {
            FindMaterials();
            return _materials.Count == 0 ? null : _materials[MBRandom.RandomInt(_materials.Count)];
        }
        private static void FindMaterials()
        {
            if (_searched) return;
            _searched = true;
            try
            {
                Material source = Material.GetDefaultMaterial();
                if (source == null) { PhysicsLog.Info("WOUND no default material; wounds are not drawn."); return; }
                int loaded = 0;
                for (int i = 0; i < Variants; i++)
                {
                    Texture texture = Texture.GetFromResource("blood_terrain_decal_" + i + "_d");
                    if (texture == null) continue;
                    texture.PreloadTexture(true);
                    if (texture.IsLoaded()) loaded++;
                    Material material = source.CreateCopy();
                    material.SetTexture(Material.MBTextureType.DiffuseMap, texture);
                    material.SetAlphaBlendMode(Material.MBAlphaBlendMode.Modulate);
                    _materials.Add(material);
                }
                PhysicsLog.Info("WOUND " + _materials.Count + " blood splatters, " + loaded + " loaded");
            }
            catch (Exception ex) { Fail(ex.Message); }
        }

        // The quad in its own frame: x along, y across, z out of the body.
        private static Mesh Quad(Material material)
        {
            Mesh mesh = Mesh.CreateMeshWithMaterial(material);
            ManagedMeshEditOperations ops = ManagedMeshEditOperations.Create(mesh);
            Vec3 p0 = new Vec3(-.5f, -.5f), p1 = new Vec3(.5f, -.5f), p2 = new Vec3(.5f, .5f), p3 = new Vec3(-.5f, .5f);
            Vec3 up = new Vec3(0, 0, 1), down = new Vec3(0, 0, -1), white = new Vec3(1, 1, 1);
            Vec2 u0 = new Vec2(0, 0), u1 = new Vec2(1, 0), u2 = new Vec2(1, 1), u3 = new Vec2(0, 1);
            ops.AddTriangle(p0, p1, p2, up, up, up, u0, u1, u2, white, white, white);
            ops.AddTriangle(p0, p2, p3, up, up, up, u0, u2, u3, white, white, white);
            // The back face too: which way the engine culls is not known here.
            ops.AddTriangle(p0, p2, p1, down, down, down, u0, u2, u1, white, white, white);
            ops.AddTriangle(p0, p3, p2, down, down, down, u0, u3, u2, white, white, white);
            ops.FinalizeEditing();
            mesh.RecomputeBoundingBox();
            mesh.Color = 0xFFFFFFFF;
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
                entity.AddMesh(Quad(material), true);
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
