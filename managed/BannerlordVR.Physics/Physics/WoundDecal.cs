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
    // meshes, and its decals only project on terrain (a decal on the struck
    // bone drew nothing in game, 23 Oct 2026). A small quad of the game's
    // blood texture is laid on the surface instead and attached to the
    // struck bone (Skeleton.AddMeshToBone, as items are): round for a stab,
    // drawn along the swing for a cut.
    internal static class WoundDecal
    {
        private static readonly string[] Textures = { "blood21_splatter", "blood_7_sprite", "blood_9_spirte", "blood_terrain_decal_0_d" };
        private static readonly string[] BaseMaterials = { "tableau_with_alpha_mask", "vertex_color_blend_mat", "vertex_color_mat" };
        private const int PerAgent = 10, Total = 80;
        private const float StabSize = .07f, CutLength = .16f, CutWidth = .05f, Lift = .012f;
        private struct Wound { public Agent Agent; public Skeleton Skeleton; public Mesh Mesh; }
        private static readonly List<Wound> Wounds = new List<Wound>();
        private static Material _material;
        private static bool _broken, _logged, _searched;

        internal static void ForgetMission() { Wounds.Clear(); }

        public static void Stab(Agent agent, sbyte bone, Vec3 contact, Vec3 direction) => Add(agent, bone, contact, direction, false);
        public static void Cut(Agent agent, sbyte bone, Vec3 contact, Vec3 direction) => Add(agent, bone, contact, direction, true);

        private static Material FindMaterial()
        {
            if (_searched) return _material;
            _searched = true;
            Texture texture = null; string textureName = null;
            foreach (string name in Textures)
            {
                texture = Texture.GetFromResource(name);
                if (texture != null) { textureName = name; break; }
            }
            Material source = null; string sourceName = null;
            foreach (string name in BaseMaterials)
            {
                source = Material.GetFromResource(name);
                if (source != null) { sourceName = name; break; }
            }
            if (texture == null || source == null)
            {
                PhysicsLog.Info("WOUND no blood texture (" + (textureName ?? "none") + ") or base material (" + (sourceName ?? "none") + "); wounds are not drawn.");
                return null;
            }
            _material = source.CreateCopy();
            _material.SetTexture(Material.MBTextureType.DiffuseMap, texture);
            _material.SetAlphaBlendMode(Material.MBAlphaBlendMode.Modulate);
            PhysicsLog.Info("WOUND mesh material " + sourceName + " with texture " + textureName);
            return _material;
        }

        private static void Add(Agent agent, sbyte bone, Vec3 contact, Vec3 direction, bool cut)
        {
            if (_broken || !EnemyPhysicsSettings.Wounds || agent?.AgentVisuals == null || bone < 0 || !InteractionMath.IsFinite(contact)) return;
            try
            {
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
                float half = (cut ? CutLength : StabSize) * jitter * .5f, halfWidth = (cut ? CutWidth : StabSize) * jitter * .5f;
                // Bone space, lifted off the surface against z-fighting.
                Vec3 centre = contact + normal * Lift;
                Vec3 p0 = Local(boneWorld, centre - along * half - side * halfWidth), p1 = Local(boneWorld, centre + along * half - side * halfWidth);
                Vec3 p2 = Local(boneWorld, centre + along * half + side * halfWidth), p3 = Local(boneWorld, centre - along * half + side * halfWidth);
                Mesh mesh = Mesh.CreateMeshWithMaterial(material);
                if (mesh == null) { Fail("CreateMeshWithMaterial returned nothing"); return; }
                UIntPtr handle = mesh.LockEditDataWrite();
                Vec2 u0 = new Vec2(0, 0), u1 = new Vec2(1, 0), u2 = new Vec2(1, 1), u3 = new Vec2(0, 1);
                const uint white = 0xFFFFFFFF;
                // Both faces: which way the engine culls is not known here.
                mesh.AddTriangle(p0, p1, p2, u0, u1, u2, white, handle);
                mesh.AddTriangle(p0, p2, p3, u0, u2, u3, white, handle);
                mesh.AddTriangle(p0, p2, p1, u0, u2, u1, white, handle);
                mesh.AddTriangle(p0, p3, p2, u0, u3, u2, white, handle);
                mesh.UnlockEditDataWrite(handle);
                mesh.ComputeNormals();
                mesh.UpdateBoundingBox();
                skeleton.AddMeshToBone(mesh.Pointer, bone);
                Wounds.Add(new Wound { Agent = agent, Skeleton = skeleton, Mesh = mesh });
                Trim(agent);
                if (!_logged)
                {
                    _logged = true;
                    PhysicsLog.Info("WOUND first mesh on agent " + agent.Index + " bone " + skeleton.GetBoneName(bone) + (cut ? " (cut)" : " (stab)"));
                }
            }
            catch (Exception ex) { Fail(ex.Message); }
        }

        private static Vec3 Local(MatrixFrame boneWorld, Vec3 world) => boneWorld.TransformToLocalNonOrthogonal(world);

        // Oldest first: ten on one body, eighty in the battle. A bone's mesh
        // cannot be taken off alone; a trimmed one is hidden.
        private static void Trim(Agent agent)
        {
            int mine = 0;
            for (int i = Wounds.Count - 1; i >= 0; i--) if (Wounds[i].Agent == agent && ++mine > PerAgent) Hide(i);
            while (Wounds.Count > Total) Hide(0);
        }
        private static void Hide(int i)
        {
            Wound w = Wounds[i];
            Wounds.RemoveAt(i);
            try { w.Mesh?.SetVisibilityMask((VisibilityMaskFlags)0); } catch (Exception) { }
        }

        private static void Fail(string why)
        {
            _broken = true;
            PhysicsLog.Info("WOUND meshes stopped for the session: " + why);
        }
    }
}
