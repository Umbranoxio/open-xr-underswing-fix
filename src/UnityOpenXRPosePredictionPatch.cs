using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace OpenXRUnderswingFix {
    internal static class UnityOpenXRPosePredictionPatch {
        private const uint PageExecuteReadWrite = 0x40;
        private const int DynamicPoseTimeSourceOffset = 27;
        private const byte DynamicPoseTimeSource = 0x41;
        private const byte PredictedDisplayTimeSource = 0x51;

        private static readonly object Sync = new object();

        private static readonly byte[] PoseTimesSignature = {
            0x49, 0xB9, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
            0xFF, 0x7F, 0x48, 0x89, 0x51, 0x20, 0x4D, 0x3B,
            0xC1, 0x4A, 0x8D, 0x04, 0x02, 0x48, 0x0F, 0x44,
            0xC2, 0x48, 0x89, 0x41, 0x28, 0xC3
        };

        private static Timer retryTimer;
        private static IntPtr patchAddress;

        [DllImport("UnityOpenXR", EntryPoint = "NativeConfig_GetRuntimeName")]
        private static extern bool GetRuntimeName(out IntPtr runtimeName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(
            IntPtr process,
            IntPtr baseAddress,
            byte[] buffer,
            int size,
            IntPtr bytesRead);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualProtect(
            IntPtr address,
            UIntPtr size,
            uint newProtection,
            out uint oldProtection);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FlushInstructionCache(
            IntPtr process,
            IntPtr address,
            UIntPtr size);

        internal static void Enable() {
            lock (Sync) {
                if (TryApply()) {
                    return;
                }

                retryTimer = new Timer(Retry, null, 250, 250);
            }
        }

        internal static void Disable() {
            lock (Sync) {
                retryTimer?.Dispose();
                retryTimer = null;

                if (patchAddress == IntPtr.Zero) {
                    return;
                }

                try {
                    using Process process = Process.GetCurrentProcess();
                    WriteByte(process, patchAddress, DynamicPoseTimeSource);
                    patchAddress = IntPtr.Zero;
                } catch (Exception ex) {
                    Plugin.Log.Warn($"restore failed: {ex.Message}");
                }
            }
        }

        private static void Retry(object _) {
            lock (Sync) {
                if (retryTimer == null || !TryApply()) {
                    return;
                }

                retryTimer.Dispose();
                retryTimer = null;
            }
        }

        private static bool TryApply() {
            if (patchAddress != IntPtr.Zero) {
                return true;
            }

            string runtimeName;
            try {
                if (!GetRuntimeName(out IntPtr name) || name == IntPtr.Zero) {
                    return false;
                }

                runtimeName = Marshal.PtrToStringAnsi(name);
            } catch (DllNotFoundException) {
                return false;
            } catch (EntryPointNotFoundException) {
                return false;
            }

            try {
                using Process process = Process.GetCurrentProcess();
                ProcessModule runtimeModule = process.Modules.Cast<ProcessModule>()
                    .FirstOrDefault(module => string.Equals(
                        module.ModuleName,
                        "UnityOpenXR.dll",
                        StringComparison.OrdinalIgnoreCase));

                if (runtimeModule == null) {
                    return false;
                }

                byte[] moduleBytes = new byte[runtimeModule.ModuleMemorySize];
                if (!ReadProcessMemory(
                    process.Handle,
                    runtimeModule.BaseAddress,
                    moduleBytes,
                    moduleBytes.Length,
                    IntPtr.Zero)) {
                    throw new InvalidOperationException(
                        $"ReadProcessMemory failed: {Marshal.GetLastWin32Error()}");
                }

                int match = -1;
                int matches = 0;
                for (int offset = 0; offset <= moduleBytes.Length - PoseTimesSignature.Length; offset++) {
                    int index = 0;
                    while (index < PoseTimesSignature.Length &&
                        moduleBytes[offset + index] == PoseTimesSignature[index]) {
                        index++;
                    }

                    if (index == PoseTimesSignature.Length) {
                        match = offset;
                        matches++;
                    }
                }

                if (matches != 1) {
                    Plugin.Log.Warn($"pattern sig matched {matches} times");
                    return true;
                }

                IntPtr address = IntPtr.Add(
                    runtimeModule.BaseAddress,
                    match + DynamicPoseTimeSourceOffset);
                if (Marshal.ReadByte(address) != DynamicPoseTimeSource) {
                    Plugin.Log.Warn("pattern sig changed");
                    return true;
                }

                WriteByte(process, address, PredictedDisplayTimeSource);
                patchAddress = address;

                long patchOffset = address.ToInt64() - runtimeModule.BaseAddress.ToInt64();
                Plugin.Log.Info($"patched {runtimeName} at +0x{patchOffset:X}");
            } catch (Exception ex) {
                Plugin.Log.Warn($"patch failed: {ex.Message}");
            }

            return true;
        }

        private static void WriteByte(Process process, IntPtr address, byte value) {
            UIntPtr size = new UIntPtr(1u);
            if (!VirtualProtect(address, size, PageExecuteReadWrite, out uint oldProtection)) {
                throw new InvalidOperationException(
                    $"VirtualProtect failed: {Marshal.GetLastWin32Error()}");
            }

            try {
                Marshal.WriteByte(address, value);
                if (!FlushInstructionCache(process.Handle, address, size)) {
                    throw new InvalidOperationException(
                        $"FlushInstructionCache failed: {Marshal.GetLastWin32Error()}");
                }
            } finally {
                if (!VirtualProtect(address, size, oldProtection, out _)) {
                    Plugin.Log.Warn($"VirtualProtect restore failed: {Marshal.GetLastWin32Error()}");
                }
            }
        }
    }
}
