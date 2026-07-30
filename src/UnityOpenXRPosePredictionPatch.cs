using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace OpenXRUnderswingFix {
    internal static class UnityOpenXRPosePredictionPatch {
        private const uint PageExecuteReadWrite = 0x40;
        private const uint ThreadSuspendResume = 0x0002;
        private const long NanosecondsPerSecond = 1_000_000_000;

        private static readonly object Sync = new object();

        private static readonly byte[] PoseTimesSignature = {
            0x49, 0xB9, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
            0xFF, 0x7F, 0x48, 0x89, 0x51, 0x20, 0x4D, 0x3B,
            0xC1, 0x4A, 0x8D, 0x04, 0x02, 0x48, 0x0F, 0x44,
            0xC2, 0x48, 0x89, 0x41, 0x28, 0xC3
        };

        private static Timer retryTimer;
        private static Timer refreshTimer;
        private static IntPtr patchAddress;
        private static long displayPeriod;
        private static bool useFixedDisplayPeriod;

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

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenThread(
            uint desiredAccess,
            bool inheritHandle,
            uint threadId);

        [DllImport("kernel32.dll")]
        private static extern uint SuspendThread(IntPtr thread);

        [DllImport("kernel32.dll")]
        private static extern uint ResumeThread(IntPtr thread);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        internal static void Enable() {
            lock (Sync) {
                displayPeriod = GetDisplayPeriod();
                if (!TryApply()) {
                    retryTimer = new Timer(Retry, null, 250, 250);
                }
            }
        }

        internal static void Disable() {
            lock (Sync) {
                refreshTimer?.Dispose();
                refreshTimer = null;
                retryTimer?.Dispose();
                retryTimer = null;

                if (patchAddress == IntPtr.Zero) {
                    return;
                }

                try {
                    using Process process = Process.GetCurrentProcess();
                    WriteBytes(process, patchAddress, PoseTimesSignature);
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

        private static void QueueRefreshRateCheck(object _) {
            lock (Sync) {
                if (refreshTimer == null) {
                    return;
                }
            }

            IPA.Utilities.Async.UnityMainThreadTaskScheduler.Factory.StartNew(UpdateDisplayPeriod);
        }

        private static void UpdateDisplayPeriod() {
            long updatedDisplayPeriod = GetDisplayPeriod();
            lock (Sync) {
                if (!useFixedDisplayPeriod ||
                    refreshTimer == null ||
                    updatedDisplayPeriod == 0 ||
                    updatedDisplayPeriod == displayPeriod) {
                    return;
                }

                if (patchAddress == IntPtr.Zero) {
                    displayPeriod = updatedDisplayPeriod;
                    if (TryApply()) {
                        retryTimer?.Dispose();
                        retryTimer = null;
                    }

                    return;
                }

                try {
                    using Process process = Process.GetCurrentProcess();
                    WriteBytes(process, patchAddress, CreatePatch(updatedDisplayPeriod));
                    displayPeriod = updatedDisplayPeriod;
                    Plugin.Log.Info($"updated to {NanosecondsPerSecond / (double)displayPeriod:0.##}hz");
                } catch (Exception ex) {
                    Plugin.Log.Warn($"refresh update failed: {ex.Message}");
                }
            }
        }

        private static long GetDisplayPeriod() {
            float refreshRate = Convert.ToSingle(Type
                .GetType("UnityEngine.XR.XRDevice, UnityEngine.VRModule")
                ?.GetProperty("refreshRate")
                ?.GetValue(null));
            return refreshRate <= 0
                ? 0
                : (long)Math.Round(NanosecondsPerSecond / (double)refreshRate);
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

            useFixedDisplayPeriod = runtimeName.IndexOf(
                "SteamVR",
                StringComparison.OrdinalIgnoreCase) >= 0;
            if (useFixedDisplayPeriod && refreshTimer == null) {
                refreshTimer = new Timer(QueueRefreshRateCheck, null, 1000, 1000);
            }

            if (useFixedDisplayPeriod && displayPeriod == 0) {
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

                IntPtr address = IntPtr.Add(runtimeModule.BaseAddress, match);
                WriteBytes(
                    process,
                    address,
                    CreatePatch(useFixedDisplayPeriod ? displayPeriod : 0));
                patchAddress = address;

                long patchOffset = address.ToInt64() - runtimeModule.BaseAddress.ToInt64();
                string prediction = useFixedDisplayPeriod
                    ? $"{NanosecondsPerSecond / (double)displayPeriod:0.##}hz"
                    : "render time";
                Plugin.Log.Info($"patched {runtimeName} at +0x{patchOffset:X} ({prediction})");
            } catch (Exception ex) {
                Plugin.Log.Warn($"patch failed: {ex.Message}");
            }

            return true;
        }

        private static byte[] CreatePatch(long fixedDisplayPeriod) {
            byte[] patch = {
                0x48, 0x89, 0x51, 0x20,
                0x48, 0xB8, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x48, 0x01, 0xD0, 0x49, 0xFF, 0xC0,
                0x48, 0x0F, 0x48, 0xC2, 0x48, 0x89, 0x41, 0x28,
                0xC3, 0xCC
            };

            Array.Copy(BitConverter.GetBytes(fixedDisplayPeriod), 0, patch, 6, sizeof(long));
            return patch;
        }

        private static void WriteBytes(Process process, IntPtr address, byte[] bytes) {
            UIntPtr size = new UIntPtr((uint)bytes.Length);
            List<IntPtr> suspendedThreads = SuspendOtherThreads(process);
            try {
                if (!VirtualProtect(address, size, PageExecuteReadWrite, out uint oldProtection)) {
                    throw new InvalidOperationException(
                        $"VirtualProtect failed: {Marshal.GetLastWin32Error()}");
                }

                try {
                    Marshal.Copy(bytes, 0, address, bytes.Length);
                    if (!FlushInstructionCache(process.Handle, address, size)) {
                        throw new InvalidOperationException(
                            $"FlushInstructionCache failed: {Marshal.GetLastWin32Error()}");
                    }
                } finally {
                    if (!VirtualProtect(address, size, oldProtection, out _)) {
                        Plugin.Log.Warn($"VirtualProtect restore failed: {Marshal.GetLastWin32Error()}");
                    }
                }
            } finally {
                ResumeThreads(suspendedThreads);
            }
        }

        private static List<IntPtr> SuspendOtherThreads(Process process) {
            uint currentThread = GetCurrentThreadId();
            var suspendedThreads = new List<IntPtr>();

            foreach (ProcessThread thread in process.Threads) {
                if (thread.Id == currentThread) {
                    continue;
                }

                IntPtr handle = OpenThread(ThreadSuspendResume, false, (uint)thread.Id);
                if (handle == IntPtr.Zero) {
                    continue;
                }

                if (SuspendThread(handle) == uint.MaxValue) {
                    CloseHandle(handle);
                    continue;
                }

                suspendedThreads.Add(handle);
            }

            return suspendedThreads;
        }

        private static void ResumeThreads(IEnumerable<IntPtr> threads) {
            foreach (IntPtr thread in threads) {
                ResumeThread(thread);
                CloseHandle(thread);
            }
        }
    }
}
