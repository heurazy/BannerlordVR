using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR.Physics
{
    // A severed limb hidden in the skeleton itself: the engine keeps one
    // 0x100-byte record per bone (BannerlordVR.Physics.Native,
    // skeleton_bones.cpp). The frame it draws the bone with is found in that
    // record by matching GetBoneEntitialFrameWithIndex; every bone of the cut
    // limb then gets its frame shrunk to nothing at the joint, each tick, so
    // the skin bound to them (and any armour skinned to them) collapses into
    // the stump. Whether the engine keeps the written frame until it draws is
    // measured and logged (LIMB_HIDE).
    internal static class LimbHide
    {
        [DllImport("BannerlordVR.Physics.Native.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int bvr_skeleton_bone_record(UIntPtr skeleton, int bone, byte[] output, int bytes);
        [DllImport("BannerlordVR.Physics.Native.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int bvr_skeleton_write_record(UIntPtr skeleton, int bone, int offset, float[] values, int count);

        private const int RecordBytes = 0x100;
        private const float Shrink = .001f, HoldSeconds = 30, Tolerance = .003f;
        private sealed class Cut
        {
            public Agent Agent; public Skeleton Skeleton; public sbyte Joint; public sbyte[] Bones;
            public float Until, Started; public int Kept, Overwritten, PositionKept, PositionOverwritten, Writes; public bool Reported;
        }
        private static readonly List<Cut> Cuts = new List<Cut>();
        // Offset of the drawn frame (a 4x4 MatrixFrame, s f u origin) in a
        // bone record, once found; -2 when the search failed.
        private static int _frameOffset = -1;
        private static bool _broken;
        private static readonly byte[] Buffer = new byte[RecordBytes];

        public static void Begin(Agent agent, sbyte bone)
        {
            if (!EnemyPhysicsSettings.LimbHide || _broken || agent?.AgentVisuals == null) return;
            try
            {
                Skeleton skeleton = agent.AgentVisuals.GetSkeleton();
                if (skeleton == null) return;
                var bones = new List<sbyte>();
                int count = skeleton.GetBoneCount();
                for (sbyte b = 0; b < count; b++)
                    for (sbyte up = b; up >= 0; up = skeleton.GetParentBoneIndex(up))
                        if (up == bone) { bones.Add(b); break; }
                float now = Mission.Current?.CurrentTime ?? 0;
                Cuts.Add(new Cut { Agent = agent, Skeleton = skeleton, Joint = bone, Bones = bones.ToArray(), Started = now, Until = now + HoldSeconds });
                PhysicsLog.Info("LIMB_HIDE begin agent=" + agent.Index + " joint=" + skeleton.GetBoneName(bone) + " bones=" + bones.Count);
            }
            catch (Exception ex) { Fail("begin", ex); }
        }

        public static void Tick()
        {
            if (Cuts.Count == 0 || _broken) return;
            float now = Mission.Current?.CurrentTime ?? 0;
            for (int i = Cuts.Count - 1; i >= 0; i--)
            {
                Cut cut = Cuts[i];
                try
                {
                    if (now > cut.Until || cut.Agent.AgentVisuals == null || !cut.Agent.AgentVisuals.IsValid() ||
                        cut.Agent.AgentVisuals.GetSkeleton()?.Pointer != cut.Skeleton.Pointer)
                    {
                        Report(cut, "ended");
                        Cuts.RemoveAt(i); continue;
                    }
                    if (_frameOffset == -1) Discover(cut);
                    if (_frameOffset < 0) { Cuts.RemoveAt(i); continue; }
                    Hide(cut);
                    if (!cut.Reported && now - cut.Started > 1.5f) Report(cut, "1.5s");
                }
                catch (Exception ex) { Fail("tick", ex); return; }
            }
        }

        // Bone record layout, read in game on 22 Oct 2026 (a head record):
        // 0x04 drawn frame rotation (quaternion x y z w), 0x14 its position
        // (entity space, matched against GetBoneEntitialFrameWithIndex), 0x20
        // a float at FLT_MAX; 0x30 local rotation, 0x40 local position (w 1),
        // 0x50 a 1 then zeros, likely the local scale; 0x60 a 4x4 matrix
        // (the rest frame). The drawn frame has no scale, so the limb is
        // shrunk through the local scale and its drawn position put at the
        // joint; each field read back next tick says whether the engine kept it.
        private const int DrawnPosition = 0x14, LocalScale = 0x50;
        private static void Discover(Cut cut)
        {
            int read = bvr_skeleton_bone_record(cut.Skeleton.Pointer, cut.Joint, Buffer, RecordBytes);
            MatrixFrame e = cut.Skeleton.GetBoneEntitialFrameWithIndex(cut.Joint);
            bool position = read == RecordBytes && Near(DrawnPosition, e.origin);
            bool scale = read == RecordBytes && Math.Abs(BitConverter.ToSingle(Buffer, LocalScale) - 1) < .05f;
            _frameOffset = position && scale ? DrawnPosition : -2;
            PhysicsLog.Info(string.Format(CultureInfo.InvariantCulture,
                "LIMB_HIDE layout check: drawn position at 0x14 {0}, local scale at 0x50 = {1:G4} {2}",
                position ? "matches" : "does not match", read == RecordBytes ? BitConverter.ToSingle(Buffer, LocalScale) : float.NaN,
                _frameOffset > 0 ? "- hiding on" : "- hiding off"));
        }

        private static bool Near(int offset, Vec3 v) =>
            Math.Abs(BitConverter.ToSingle(Buffer, offset) - v.x) < Tolerance &&
            Math.Abs(BitConverter.ToSingle(Buffer, offset + 4) - v.y) < Tolerance &&
            Math.Abs(BitConverter.ToSingle(Buffer, offset + 8) - v.z) < Tolerance;

        private static readonly float[] One = new float[1], Three = new float[3];
        private static void Hide(Cut cut)
        {
            if (bvr_skeleton_bone_record(cut.Skeleton.Pointer, cut.Joint, Buffer, RecordBytes) < RecordBytes) return;
            float jx = BitConverter.ToSingle(Buffer, DrawnPosition), jy = BitConverter.ToSingle(Buffer, DrawnPosition + 4),
                jz = BitConverter.ToSingle(Buffer, DrawnPosition + 8);
            foreach (sbyte bone in cut.Bones)
            {
                if (bvr_skeleton_bone_record(cut.Skeleton.Pointer, bone, Buffer, RecordBytes) < RecordBytes) continue;
                if (cut.Writes > 0)
                {
                    if (BitConverter.ToSingle(Buffer, LocalScale) < Shrink * 3) cut.Kept++; else cut.Overwritten++;
                    float dx = BitConverter.ToSingle(Buffer, DrawnPosition) - jx, dy = BitConverter.ToSingle(Buffer, DrawnPosition + 4) - jy,
                        dz = BitConverter.ToSingle(Buffer, DrawnPosition + 8) - jz;
                    if (bone != cut.Joint) { if (dx * dx + dy * dy + dz * dz < 1e-6f) cut.PositionKept++; else cut.PositionOverwritten++; }
                }
                One[0] = Shrink;
                bvr_skeleton_write_record(cut.Skeleton.Pointer, bone, LocalScale, One, 1);
                if (bone != cut.Joint)
                {
                    Three[0] = jx; Three[1] = jy; Three[2] = jz;
                    bvr_skeleton_write_record(cut.Skeleton.Pointer, bone, DrawnPosition, Three, 3);
                }
            }
            cut.Writes++;
        }

        private static void Report(Cut cut, string when)
        {
            cut.Reported = true;
            PhysicsLog.Info(string.Format(CultureInfo.InvariantCulture,
                "LIMB_HIDE {0} agent={1} bones={2} writes={3} scale kept={4} overwritten={5}; position kept={6} overwritten={7} (kept: the engine left our value until the next tick)",
                when, cut.Agent.Index, cut.Bones.Length, cut.Writes, cut.Kept, cut.Overwritten, cut.PositionKept, cut.PositionOverwritten));
        }

        private static void Fail(string where, Exception ex)
        {
            _broken = true; Cuts.Clear();
            PhysicsLog.Error("LIMB_HIDE " + where + " failed; severed limbs stay drawn.", ex);
        }
    }
}
