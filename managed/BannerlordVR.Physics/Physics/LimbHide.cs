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
            public float Until, Started; public int Kept, Overwritten, Writes; public bool Reported;
        }
        private static readonly List<Cut> Cuts = new List<Cut>();
        // Offset of the drawn frame (a 4x4 MatrixFrame, s f u origin) in a
        // bone record, once found; -2 when the search failed.
        private static int _frameOffset = -1;
        private static bool _broken;
        private static readonly byte[] Buffer = new byte[RecordBytes];
        private static readonly float[] Frame = new float[16];

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

        // The joint bone's record holds the drawn frame: the 16 floats equal
        // to its entity-space frame, rows s f u then origin, 16 bytes each.
        private static void Discover(Cut cut)
        {
            int read = bvr_skeleton_bone_record(cut.Skeleton.Pointer, cut.Joint, Buffer, RecordBytes);
            MatrixFrame e = cut.Skeleton.GetBoneEntitialFrameWithIndex(cut.Joint);
            var sb = new StringBuilder();
            for (int o = 0; o + 4 <= RecordBytes && read > 0; o += 4)
                sb.Append(o % 64 == 0 ? (o == 0 ? "" : " |") + " " + o.ToString("x2") + ":" : " ").Append(BitConverter.ToSingle(Buffer, o).ToString("G4", CultureInfo.InvariantCulture));
            PhysicsLog.Info(string.Format(CultureInfo.InvariantCulture,
                "LIMB_HIDE record read={0} entitial s=({1:F3},{2:F3},{3:F3}) f=({4:F3},{5:F3},{6:F3}) u=({7:F3},{8:F3},{9:F3}) o=({10:F3},{11:F3},{12:F3}) bytes{13}",
                read, e.rotation.s.x, e.rotation.s.y, e.rotation.s.z, e.rotation.f.x, e.rotation.f.y, e.rotation.f.z,
                e.rotation.u.x, e.rotation.u.y, e.rotation.u.z, e.origin.x, e.origin.y, e.origin.z, sb));
            if (read < RecordBytes) { _frameOffset = -2; PhysicsLog.Info("LIMB_HIDE record unreadable (" + read + ")"); return; }
            var found = new StringBuilder();
            for (int o = 4; o + 64 <= RecordBytes; o += 4)
            {
                if (!Near(o, e.rotation.s) || !Near(o + 16, e.rotation.f) || !Near(o + 32, e.rotation.u) || !Near(o + 48, e.origin)) continue;
                found.Append(' ').Append(o.ToString("x2"));
                if (_frameOffset < 0) _frameOffset = o;
            }
            if (_frameOffset < 0)
            {
                _frameOffset = -2;
                // Positions alone, to place the frame by hand from the log.
                var origins = new StringBuilder();
                for (int o = 4; o + 12 <= RecordBytes; o += 4) if (Near(o, e.origin)) origins.Append(' ').Append(o.ToString("x2"));
                PhysicsLog.Info("LIMB_HIDE no matrix frame in the record; origin found at" + (origins.Length > 0 ? origins.ToString() : " none"));
            }
            else PhysicsLog.Info("LIMB_HIDE drawn frame at offset" + found + " (using " + _frameOffset.ToString("x2") + ")");
        }

        private static bool Near(int offset, Vec3 v) =>
            Math.Abs(BitConverter.ToSingle(Buffer, offset) - v.x) < Tolerance &&
            Math.Abs(BitConverter.ToSingle(Buffer, offset + 4) - v.y) < Tolerance &&
            Math.Abs(BitConverter.ToSingle(Buffer, offset + 8) - v.z) < Tolerance;

        // Every bone of the limb: rows shrunk, origin at the joint. A row still
        // shrunk when read back is a frame the engine kept from our last write.
        private static void Hide(Cut cut)
        {
            if (bvr_skeleton_bone_record(cut.Skeleton.Pointer, cut.Joint, Buffer, RecordBytes) < RecordBytes) return;
            float jx = BitConverter.ToSingle(Buffer, _frameOffset + 48), jy = BitConverter.ToSingle(Buffer, _frameOffset + 52),
                jz = BitConverter.ToSingle(Buffer, _frameOffset + 56);
            foreach (sbyte bone in cut.Bones)
            {
                if (bvr_skeleton_bone_record(cut.Skeleton.Pointer, bone, Buffer, RecordBytes) < RecordBytes) continue;
                float rowLength = (float)Math.Sqrt(Sq(_frameOffset) + Sq(_frameOffset + 4) + Sq(_frameOffset + 8));
                if (cut.Writes > 0) { if (rowLength < Shrink * 3) cut.Kept++; else cut.Overwritten++; }
                for (int k = 0; k < 16; k++) Frame[k] = BitConverter.ToSingle(Buffer, _frameOffset + k * 4);
                for (int k = 0; k < 12; k++) if (k % 4 != 3) Frame[k] = rowLength < Shrink * 3 ? Frame[k] : Frame[k] * Shrink;
                Frame[12] = jx; Frame[13] = jy; Frame[14] = jz;
                bvr_skeleton_write_record(cut.Skeleton.Pointer, bone, _frameOffset, Frame, 16);
            }
            cut.Writes++;
        }
        private static float Sq(int offset) { float v = BitConverter.ToSingle(Buffer, offset); return v * v; }

        private static void Report(Cut cut, string when)
        {
            cut.Reported = true;
            PhysicsLog.Info(string.Format(CultureInfo.InvariantCulture,
                "LIMB_HIDE {0} agent={1} writes={2} kept={3} overwritten={4} (kept: the engine left our shrunk frame until the next tick)",
                when, cut.Agent.Index, cut.Writes, cut.Kept, cut.Overwritten));
        }

        private static void Fail(string where, Exception ex)
        {
            _broken = true; Cuts.Clear();
            PhysicsLog.Error("LIMB_HIDE " + where + " failed; severed limbs stay drawn.", ex);
        }
    }
}
