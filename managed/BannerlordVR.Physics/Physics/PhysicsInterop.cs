using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using BannerlordVR.Interop;

namespace BannerlordVR.VR.Physics
{
    // The physics module runs beside an unmodified BannerlordVR release.
    // Controller input goes through the release's own wrapper (trigger/grip
    // swap, toggle grips) by reflection, falling back to the raw export; the
    // haptics and overlay calls hit the release's already loaded native DLL.
    internal static class PhysicsInterop
    {
        private delegate int InputWrapper(out BvrInputState state, bool battleGrip);
        private static InputWrapper _input;
        private static bool _resolved;

        public static int GetInputState(out BvrInputState state)
        {
            if (!_resolved)
            {
                _resolved = true;
                try
                {
                    Type native = typeof(BvrInputState).Assembly.GetType("BannerlordVR.Interop.NativeMethods");
                    MethodInfo method = native?.GetMethod("bvr_get_input_state", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
                        null, new[] { typeof(BvrInputState).MakeByRefType(), typeof(bool) }, null);
                    if (method != null) _input = (InputWrapper)Delegate.CreateDelegate(typeof(InputWrapper), method);
                }
                catch (Exception ex) { PhysicsLog.Error("BannerlordVR input wrapper unavailable; using the raw native input.", ex); }
                PhysicsLog.Info("Controller input: raw native grip and trigger" + (_input != null ? ", trigger/grip swap followed from BannerlordVR." : "."));
            }
            // The release's wrapper consumes the grip for its own grab/holster
            // handling (it read 0 in VR), so the physics keeps the raw grip and
            // trigger values, swapped only if the release swaps them.
            int status = bvr_get_input_state(out state);
            if (status == 0 && SwapTriggerGrip())
            {
                float g = state.Left.Grip; state.Left.Grip = state.Left.Trigger; state.Left.Trigger = g;
                g = state.Right.Grip; state.Right.Grip = state.Right.Trigger; state.Right.Trigger = g;
            }
            return status;
        }

        private static PropertyInfo _swap;
        private static bool SwapTriggerGrip()
        {
            try
            {
                if (_swap == null)
                    _swap = typeof(BvrInputState).Assembly.GetType("BannerlordVR.Interop.NativeMethods")?
                        .GetProperty("SwapTriggerGrip", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                return _swap != null && (bool)_swap.GetValue(null);
            }
            catch { return false; }
        }

        public static void Haptic(int hand, float amplitude, float seconds)
        {
            try { bvr_trigger_haptic(hand, amplitude, seconds); }
            catch (Exception) { }
        }

        public static bool OverlayVisible()
        {
            try { return bvr_overlay_visible() != 0; }
            catch (Exception) { return false; }
        }

        // The ragdoll bridge DLL sits next to this assembly; DllImport alone
        // would search the game's folders, not the module's.
        public static bool LoadPhysicsNative()
        {
            try
            {
                string dir = Path.GetDirectoryName(new Uri(Assembly.GetExecutingAssembly().CodeBase).LocalPath);
                string path = Path.Combine(dir, "BannerlordVR.Physics.Native.dll");
                if (!File.Exists(path)) { PhysicsLog.Info("Physics native bridge missing at " + path); return false; }
                if (LoadLibraryW(path) == IntPtr.Zero)
                { PhysicsLog.Info("Physics native bridge LoadLibrary failed, win32 error " + Marshal.GetLastWin32Error()); return false; }
                return true;
            }
            catch (Exception ex) { PhysicsLog.Error("Physics native bridge could not load.", ex); return false; }
        }

        [DllImport("BannerlordVR.Native.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int bvr_get_input_state(out BvrInputState state);
        [DllImport("BannerlordVR.Native.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int bvr_trigger_haptic(int hand, float amplitude, float durationSeconds);
        [DllImport("BannerlordVR.Native.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int bvr_overlay_visible();
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryW(string path);
    }
}
