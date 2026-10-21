using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BannerlordVR.VR.Physics
{
    // A severed limb folded into the body in the skeleton itself. The engine
    // keeps one 0x100-byte record per bone (BannerlordVR.Physics.Native,
    // skeleton_bones.cpp); read in game on 22 Oct 2026, a record holds the
    // frame the bone is drawn with at 0x04 (rotation, quaternion w x y z) and
    // 0x14 (position, entity space), then the local frame at 0x30 / 0x40 and
    // the rest frame as a 4x4 matrix at 0x60. The drawn frame has no scale
    // (a local scale of 0.001 was kept by the engine and never drawn, and its
    // derived transforms broke the sound system before a crash), so the limb
    // cannot shrink: it is moved rigidly instead. The head sinks into the
    // chest along the neck; a forearm, hand, calf or foot is laid onto the
    // bone above it, inside its volume. Bannerlord cannot cut a mesh, so the
    // skin between stays stretched, mostly inside the body.
    //
    // The engine rewrites the records of a moving ragdoll after the mission
    // tick: the fold is written in the mission tick and again after the agents'
    // tick (AfterAsyncTickTick), and whether it survives to the next frame's
    // start is counted (LIMB_TUCK).
    internal static class LimbHide
    {
        [DllImport("BannerlordVR.Physics.Native.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int bvr_skeleton_bone_record(UIntPtr skeleton, int bone, byte[] output, int bytes);
        [DllImport("BannerlordVR.Physics.Native.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int bvr_skeleton_write_record(UIntPtr skeleton, int bone, int offset, float[] values, int count);

        private const int RecordBytes = 0x100, DrawnRotation = 0x04, DrawnPosition = 0x14;
        private const float HoldSeconds = 30, Tolerance = .003f, HeadSink = .30f;
        private struct Pose { public float W, X, Y, Z; public Vec3 P; }
        private sealed class Cut
        {
            public Agent Agent; public Skeleton Skeleton; public SliceJoint Kind; public sbyte Root, Parent, Grandparent;
            public sbyte[] Bones; public float Until, Started; public Vec3 Written; public bool HasWritten, Reported;
            public int Writes, Skipped, Survived, Lost;
        }
        private static readonly List<Cut> Cuts = new List<Cut>();
        private static bool _broken, _layoutChecked, _layoutOk;
        private static readonly byte[] Buffer = new byte[RecordBytes];
        private static readonly float[] Values = new float[4];

        public static void Begin(Agent agent, sbyte bone, sbyte parent, SliceJoint kind)
        {
            if (!EnemyPhysicsSettings.LimbHide || _broken || agent?.AgentVisuals == null) return;
            try
            {
                Skeleton skeleton = agent.AgentVisuals.GetSkeleton();
                if (skeleton == null || parent < 0) return;
                var bones = new List<sbyte>();
                int count = skeleton.GetBoneCount();
                for (sbyte b = 0; b < count; b++)
                    for (sbyte up = b; up >= 0; up = skeleton.GetParentBoneIndex(up))
                        if (up == bone) { bones.Add(b); break; }
                float now = Mission.Current?.CurrentTime ?? 0;
                Cuts.Add(new Cut { Agent = agent, Skeleton = skeleton, Kind = kind, Root = bone, Parent = parent,
                    Grandparent = skeleton.GetParentBoneIndex(parent), Bones = bones.ToArray(), Started = now, Until = now + HoldSeconds });
                PhysicsLog.Info("LIMB_TUCK begin agent=" + agent.Index + " joint=" + kind + " root=" + skeleton.GetBoneName(bone) +
                    " onto=" + skeleton.GetBoneName(parent) + " bones=" + bones.Count);
            }
            catch (Exception ex) { Fail("begin", ex); }
        }

        // Mission tick and after the agents' tick: fold every cut limb.
        public static void Tick() => Pass(false);
        public static void LateTick() => Pass(true);

        // Start of the next frame: is the fold written last frame still there?
        public static void FrameStart()
        {
            if (Cuts.Count == 0 || _broken) return;
            try
            {
                foreach (Cut cut in Cuts)
                {
                    if (!cut.HasWritten || !Read(cut, cut.Root, out Pose root)) continue;
                    if ((root.P - cut.Written).LengthSquared < 1e-6f) cut.Survived++; else cut.Lost++;
                }
            }
            catch (Exception ex) { Fail("frame", ex); }
        }

        private static void Pass(bool late)
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
                    if (!_layoutChecked) CheckLayout(cut);
                    if (!_layoutOk) { Cuts.RemoveAt(i); continue; }
                    Fold(cut);
                    if (late && !cut.Reported && now - cut.Started > 1.5f) Report(cut, "1.5s");
                }
                catch (Exception ex) { Fail("tick", ex); return; }
            }
        }

        // The drawn position must match the engine's entity-space bone frame.
        private static void CheckLayout(Cut cut)
        {
            _layoutChecked = true;
            MatrixFrame e = cut.Skeleton.GetBoneEntitialFrameWithIndex(cut.Root);
            _layoutOk = Read(cut, cut.Root, out Pose root) && (root.P - e.origin).Length < Tolerance &&
                Math.Abs(root.W * root.W + root.X * root.X + root.Y * root.Y + root.Z * root.Z - 1) < .01f;
            PhysicsLog.Info("LIMB_TUCK layout " + (_layoutOk ? "matches" : "does not match: folding off"));
        }

        private static void Fold(Cut cut)
        {
            if (!Read(cut, cut.Root, out Pose root) || !Read(cut, cut.Parent, out Pose parent)) return;
            // Still where we put it: the engine has not rewritten the limb.
            if (cut.HasWritten && (root.P - cut.Written).LengthSquared < 1e-6f) { cut.Skipped++; return; }
            Pose target;
            if (cut.Kind == SliceJoint.Neck)
            {
                // Down the neck into the chest.
                Vec3 up = Vec3.Up;
                if (cut.Grandparent >= 0 && Read(cut, cut.Grandparent, out Pose chest))
                {
                    Vec3 d = parent.P - chest.P;
                    if (d.Length > 1e-3f) up = d.NormalizedCopy();
                }
                target = root; target.P = parent.P - up * HeadSink;
            }
            else target = parent; // laid along the bone above, from its start
            // Delta from the limb's root as drawn to its target, applied to the
            // whole limb so it keeps its shape.
            Conjugate(root, out float cw, out float cx, out float cy, out float cz);
            Multiply(target.W, target.X, target.Y, target.Z, cw, cx, cy, cz, out float dw, out float dx, out float dy, out float dz);
            foreach (sbyte bone in cut.Bones)
            {
                if (!Read(cut, bone, out Pose p)) continue;
                Multiply(dw, dx, dy, dz, p.W, p.X, p.Y, p.Z, out float nw, out float nx, out float ny, out float nz);
                float n = (float)Math.Sqrt(nw * nw + nx * nx + ny * ny + nz * nz);
                if (n < 1e-4f || float.IsNaN(n)) continue;
                Vec3 offset = Rotate(dw, dx, dy, dz, p.P - root.P);
                Vec3 position = target.P + offset;
                if (!InteractionMath.IsFinite(position)) continue;
                Values[0] = nw / n; Values[1] = nx / n; Values[2] = ny / n; Values[3] = nz / n;
                bvr_skeleton_write_record(cut.Skeleton.Pointer, bone, DrawnRotation, Values, 4);
                Values[0] = position.x; Values[1] = position.y; Values[2] = position.z;
                bvr_skeleton_write_record(cut.Skeleton.Pointer, bone, DrawnPosition, Values, 3);
                if (bone == cut.Root) { cut.Written = position; cut.HasWritten = true; }
            }
            cut.Writes++;
        }

        private static bool Read(Cut cut, sbyte bone, out Pose pose)
        {
            pose = default;
            if (bvr_skeleton_bone_record(cut.Skeleton.Pointer, bone, Buffer, RecordBytes) < RecordBytes) return false;
            pose.W = BitConverter.ToSingle(Buffer, DrawnRotation); pose.X = BitConverter.ToSingle(Buffer, DrawnRotation + 4);
            pose.Y = BitConverter.ToSingle(Buffer, DrawnRotation + 8); pose.Z = BitConverter.ToSingle(Buffer, DrawnRotation + 12);
            pose.P = new Vec3(BitConverter.ToSingle(Buffer, DrawnPosition), BitConverter.ToSingle(Buffer, DrawnPosition + 4),
                BitConverter.ToSingle(Buffer, DrawnPosition + 8));
            return InteractionMath.IsFinite(pose.P) && !float.IsNaN(pose.W);
        }

        // Hamilton quaternions, w first.
        private static void Conjugate(Pose q, out float w, out float x, out float y, out float z) { w = q.W; x = -q.X; y = -q.Y; z = -q.Z; }
        private static void Multiply(float aw, float ax, float ay, float az, float bw, float bx, float by, float bz,
            out float w, out float x, out float y, out float z)
        {
            w = aw * bw - ax * bx - ay * by - az * bz;
            x = aw * bx + ax * bw + ay * bz - az * by;
            y = aw * by - ax * bz + ay * bw + az * bx;
            z = aw * bz + ax * by - ay * bx + az * bw;
        }
        private static Vec3 Rotate(float w, float x, float y, float z, Vec3 v)
        {
            Multiply(w, x, y, z, 0, v.x, v.y, v.z, out float rw, out float rx, out float ry, out float rz);
            Multiply(rw, rx, ry, rz, w, -x, -y, -z, out float _, out float ox, out float oy, out float oz);
            return new Vec3(ox, oy, oz);
        }

        private static void Report(Cut cut, string when)
        {
            cut.Reported = true;
            PhysicsLog.Info(string.Format(CultureInfo.InvariantCulture,
                "LIMB_TUCK {0} agent={1} joint={2} writes={3} still_ours={4} next_frame survived={5} lost={6} (survived: drawn folded)",
                when, cut.Agent.Index, cut.Kind, cut.Writes, cut.Skipped, cut.Survived, cut.Lost));
        }

        private static void Fail(string where, Exception ex)
        {
            _broken = true; Cuts.Clear();
            PhysicsLog.Error("LIMB_TUCK " + where + " failed; severed limbs stay drawn.", ex);
        }
    }
}
