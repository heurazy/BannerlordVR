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
    // meshes; a projected decal of the game's own blood material is attached
    // to the struck bone instead (Skeleton.AddComponentToBone), its box
    // facing out of the body at the hit: round for a stab, drawn along the
    // swing for a cut.
    internal static class WoundDecal
    {
        private static readonly string[] Materials = { "blood_decal_2", "blood_terrain_decal_0", "blood_terrain_decal_1" };
        private const int PerAgent = 10, Total = 80;
        private const float StabSize = .07f, CutLength = .16f, CutWidth = .05f, Depth = .16f;
        private struct Wound { public Agent Agent; public Skeleton Skeleton; public sbyte Bone; public Decal Decal; }
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
            foreach (string name in Materials)
            {
                Material m = Material.GetFromResource(name);
                if (m != null) { _material = m; PhysicsLog.Info("WOUND decal material " + name); return m; }
            }
            PhysicsLog.Info("WOUND no blood decal material found; wounds are not drawn.");
            return null;
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
                MatrixFrame boneWorld = agent.AgentVisuals.GetGlobalFrame().TransformToParent(skeleton.GetBoneEntitialFrameWithIndex(bone));
                // Out of the body: from the bone's line to the contact; a hit
                // on the line itself faces back along the blow.
                sbyte child = -1;
                for (sbyte c = 0; c < skeleton.GetBoneChildCount(bone); c++) { child = skeleton.GetBoneChildAtIndex(bone, c); break; }
                Vec3 a = boneWorld.origin;
                Vec3 b = child >= 0 ? agent.AgentVisuals.GetGlobalFrame().TransformToParent(skeleton.GetBoneEntitialFrameWithIndex(child)).origin : a;
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
                float length = cut ? CutLength : StabSize, width = cut ? CutWidth : StabSize;
                float jitter = MBRandom.RandomFloatRanged(.8f, 1.2f);
                var world = new MatrixFrame(new Mat3(along * (length * jitter), side * (width * jitter), normal * Depth), contact);
                MatrixFrame local = boneWorld.TransformToLocalNonOrthogonal(world);
                Decal decal = Decal.CreateDecal();
                if (decal == null || !decal.IsValid) { Fail("CreateDecal returned nothing"); return; }
                decal.SetMaterial(material);
                decal.Frame = local;
                skeleton.AddComponentToBone(bone, decal);
                decal.SetIsVisible(true);
                Wounds.Add(new Wound { Agent = agent, Skeleton = skeleton, Bone = bone, Decal = decal });
                Trim(agent);
                if (!_logged)
                {
                    _logged = true;
                    PhysicsLog.Info("WOUND first decal on agent " + agent.Index + " bone " + skeleton.GetBoneName(bone) + (cut ? " (cut)" : " (stab)"));
                }
            }
            catch (Exception ex) { Fail(ex.Message); }
        }

        // Oldest first: ten on one body, eighty in the battle.
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
            try
            {
                if (w.Agent?.AgentVisuals != null && w.Agent.AgentVisuals.GetSkeleton()?.Pointer == w.Skeleton.Pointer)
                    w.Skeleton.RemoveBoneComponent(w.Bone, w.Decal);
            }
            catch (Exception) { }
        }

        private static void Fail(string why)
        {
            _broken = true;
            PhysicsLog.Info("WOUND decals stopped for the session: " + why);
        }
    }
}
