using System;
using System.Collections.Generic;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR.Physics
{
    // B&S lets a sliced part fly as its own body. Bannerlord cannot cut the
    // mesh, so of a severed head the helmet flies: a copy of its mesh in a new
    // dynamic entity, placed where it was drawn (the bone's entity-space frame
    // times the inverse of its rest frame, the record's matrix at 0x60, which
    // takes the skinned mesh from its bind pose to the head) and thrown with
    // the cut. Built like a dropped item (Mission.SpawnWeaponAux): body flags,
    // a sphere body, AddPhysics, physics on.
    internal static class SeveredProp
    {
        private struct Prop { public GameEntity Entity; public float Until; }
        private static readonly List<Prop> Props = new List<Prop>();
        private static Mission _mission;
        private const int MaxProps = 10;
        private const float Life = 60, Mass = 2.5f, Radius = .13f;
        private static bool _broken;

        public static void ThrowHelmet(Agent victim, MatrixFrame boneEntitial, MatrixFrame restEntitial, Vec3 velocity)
        {
            if (!EnemyPhysicsSettings.SeveredProps || _broken || victim?.AgentVisuals == null) return;
            try
            {
                ItemObject helmet = victim.SpawnEquipment[EquipmentIndex.Head].Item;
                string mesh = helmet?.MultiMeshName;
                Scene scene = Mission.Current?.Scene;
                if (string.IsNullOrEmpty(mesh) || scene == null) return;
                MetaMesh copy = MetaMesh.GetCopy(mesh, false, true);
                if (copy == null) { PhysicsLog.Info("SEVERED no mesh " + mesh); return; }
                if (Mission.Current != _mission) { Props.Clear(); _mission = Mission.Current; }
                while (Props.Count >= MaxProps) { Remove(Props[0]); Props.RemoveAt(0); }

                MatrixFrame global = victim.AgentVisuals.GetGlobalFrame();
                MatrixFrame bind = restEntitial.Inverse();
                MatrixFrame frame = global.TransformToParent(boneEntitial.TransformToParent(bind));
                Vec3 head = global.TransformToParent(boneEntitial).origin;
                GameEntity entity = GameEntity.CreateEmpty(scene, false);
                entity.AddMultiMesh(copy);
                entity.SetGlobalFrame(in frame);
                entity.AddBodyFlags(BodyFlags.Moveable | BodyFlags.DroppedItem, false);
                // The sphere sits on the head, in the mesh's own (bind) space.
                entity.AddSphereAsBody(restEntitial.origin, Radius, BodyFlags.Moveable);
                Vec3 throwVelocity = InteractionMath.ClampMagnitude(velocity * .35f + Vec3.Up * 1.5f, 8);
                Vec3 spin = new Vec3(MBRandom.RandomFloatRanged(-8, 8), MBRandom.RandomFloatRanged(-8, 8), MBRandom.RandomFloatRanged(-4, 4));
                entity.AddPhysics(Mass, restEntitial.origin, null, throwVelocity, spin, Material(), false, -1);
                entity.SetPhysicsStateOnlyVariable(true, true);
                Props.Add(new Prop { Entity = entity, Until = Mission.Current.CurrentTime + Life });
                PhysicsLog.Info(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "SEVERED helmet {0} thrown from ({1:F2},{2:F2},{3:F2}) at {4:F1} m/s", helmet.StringId, head.x, head.y, head.z, throwVelocity.Length));
            }
            catch (Exception ex)
            {
                _broken = true;
                PhysicsLog.Error("SEVERED helmet prop failed; severed heads drop no helmet.", ex);
            }
        }

        public static void Tick()
        {
            if (Props.Count == 0) return;
            if (Mission.Current != _mission) { Props.Clear(); return; }
            float now = Mission.Current.CurrentTime;
            for (int i = Props.Count - 1; i >= 0; i--)
                if (now > Props[i].Until) { Remove(Props[i]); Props.RemoveAt(i); }
        }

        private static void Remove(Prop prop)
        {
            try { if (prop.Entity != null) prop.Entity.Remove(79); } catch (Exception) { }
        }

        private static PhysicsMaterial _material;
        private static bool _materialFound;
        private static PhysicsMaterial Material()
        {
            if (_materialFound) return _material;
            _materialFound = true;
            _material = PhysicsMaterial.GetFromIndex(0);
            for (int i = 0; i < PhysicsMaterial.GetMaterialCount(); i++)
            {
                string name = PhysicsMaterial.GetNameAtIndex(i) ?? "";
                if (name.IndexOf("metal", StringComparison.OrdinalIgnoreCase) >= 0) { _material = PhysicsMaterial.GetFromIndex(i); break; }
            }
            return _material;
        }
    }
}
