using System;
using System.IO;
using System.Runtime.InteropServices;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Phase A spike: loads ErwinNativeBridge.dll into erwin.exe's process and
    /// calls its InstallHook() export. The bridge installs
    /// ECX::SetSynchronizeModelCallback so erwin invokes our native callback
    /// whenever it runs a Complete Compare / Resolve Differences synchronize.
    ///
    /// Log output: %TEMP%\erwin-native-bridge.log
    ///
    /// This is a one-way install per process - once loaded the DLL stays
    /// resident. Call Install() once at add-in startup (safe to call twice,
    /// idempotent via HMODULE caching).
    /// </summary>
    internal static class NativeBridgeService
    {
        private const string BridgeDllName = "ErwinNativeBridge.dll";

        private static IntPtr _bridgeModule = IntPtr.Zero;
        private static bool _installed;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryW(string fileName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, [MarshalAs(UnmanagedType.LPStr)] string procName);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int InstallHookFn();

        /// <summary>
        /// Returns the absolute path where ErwinNativeBridge.dll is expected to live.
        /// Mirrors the existing "same folder as managed DLL" install layout.
        /// </summary>
        private static string ResolveBridgePath()
        {
            var asmLoc = typeof(NativeBridgeService).Assembly.Location;
            var baseDir = string.IsNullOrEmpty(asmLoc)
                ? AppContext.BaseDirectory
                : (Path.GetDirectoryName(asmLoc) ?? AppContext.BaseDirectory);
            return Path.Combine(baseDir, BridgeDllName);
        }

        /// <summary>
        /// Install the native SyncModelCallback hook. Returns true on success.
        /// Idempotent - repeat calls are no-ops.
        /// </summary>
        public static bool Install(Action<string> log = null)
        {
            if (_installed) return true;

            try
            {
                string path = ResolveBridgePath();
                if (!File.Exists(path))
                {
                    log?.Invoke($"NativeBridge: {BridgeDllName} not found at '{path}' - skipping hook install.");
                    return false;
                }

                _bridgeModule = LoadLibraryW(path);
                if (_bridgeModule == IntPtr.Zero)
                {
                    int err = Marshal.GetLastWin32Error();
                    log?.Invoke($"NativeBridge: LoadLibraryW('{path}') failed (Win32 err 0x{err:X}).");
                    return false;
                }

                IntPtr proc = GetProcAddress(_bridgeModule, "InstallHook");
                if (proc == IntPtr.Zero)
                {
                    log?.Invoke($"NativeBridge: InstallHook export not found in {BridgeDllName}.");
                    return false;
                }

                var install = Marshal.GetDelegateForFunctionPointer<InstallHookFn>(proc);
                int rc = install();
                log?.Invoke($"NativeBridge: InstallHook returned {rc} (0=OK, 1=ECX missing, 2=symbol missing).");
                _installed = (rc == 0);
                return _installed;
            }
            catch (Exception ex)
            {
                log?.Invoke($"NativeBridge: Install() threw: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }
    }
}
