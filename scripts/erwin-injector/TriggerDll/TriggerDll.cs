using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

/// <summary>
/// NativeAOT DLL injected into erwin.exe.
/// Uses SetTimer to run COM activation on erwin's UI thread.
/// This ensures WinForms receives input properly.
/// </summary>
public static class TriggerDll
{
    static string logFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EliteSoft", "ErwinAddIn", "trigger.log");

    static void Log(string msg)
    {
        try
        {
            var dir = Path.GetDirectoryName(logFile);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.AppendAllText(logFile, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\r\n");
        }
        catch { }
    }

    // ---- Win32 APIs ----

    [DllImport("ole32.dll")]
    static extern int CoInitializeEx(IntPtr reserved, uint coinit);

    [DllImport("ole32.dll")]
    static extern void CoUninitialize();

    [DllImport("ole32.dll")]
    static extern int CLSIDFromProgID(
        [MarshalAs(UnmanagedType.LPWStr)] string progId,
        out Guid clsid);

    [DllImport("ole32.dll")]
    static extern int CoCreateInstance(
        ref Guid clsid, IntPtr outer, uint context, ref Guid iid, out IntPtr obj);

    [DllImport("user32.dll")]
    static extern IntPtr FindWindowW(
        [MarshalAs(UnmanagedType.LPWStr)] string className,
        IntPtr windowName);

    [DllImport("user32.dll")]
    static extern UIntPtr SetTimer(IntPtr hWnd, UIntPtr nIDEvent, uint uElapse,
        IntPtr lpTimerFunc);

    [DllImport("user32.dll")]
    static extern bool KillTimer(IntPtr hWnd, UIntPtr uIDEvent);

    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("kernel32.dll")]
    static extern uint GetCurrentProcessId();

    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    const uint COINIT_APARTMENTTHREADED = 0x2;
    const uint CLSCTX_INPROC_SERVER = 0x1;
    const uint CLSCTX_LOCAL_SERVER = 0x4;
    const uint CLSCTX_ALL = CLSCTX_INPROC_SERVER | CLSCTX_LOCAL_SERVER;

    static Guid IID_IDispatch = new Guid("00020400-0000-0000-C000-000000000046");
    static Guid IID_NULL = Guid.Empty;

    const int IDISPATCH_RELEASE = 2;
    const int IDISPATCH_GETIDSOFNAMES = 5;
    const int IDISPATCH_INVOKE = 6;

    [StructLayout(LayoutKind.Sequential)]
    struct DISPPARAMS
    {
        public IntPtr rgvarg;
        public IntPtr rgdispidNamedArgs;
        public uint cArgs;
        public uint cNamedArgs;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int QueryInterfaceDelegate(IntPtr self, ref Guid riid, out IntPtr ppv);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int QueryInterface_IID(IntPtr self, ref Guid riid, out IntPtr ppv);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int GetIDsOfNamesDelegate(
        IntPtr self, ref Guid riid,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] names,
        uint cNames, uint lcid,
        [MarshalAs(UnmanagedType.LPArray)] int[] rgDispId);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int InvokeDelegate(
        IntPtr self, int dispIdMember, ref Guid riid, uint lcid, ushort wFlags,
        ref DISPPARAMS pDispParams, IntPtr pVarResult, IntPtr pExcepInfo, IntPtr puArgErr);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate uint ReleaseDelegate(IntPtr self);

    static IntPtr _erwinHwnd = IntPtr.Zero;

    /// <summary>
    /// Called by the injector via CreateRemoteThread.
    /// Sets a timer on erwin's main window so the callback runs on erwin's UI thread.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "Activate")]
    public static int Activate(IntPtr param)
    {
        Log("Activate called");

        // Find erwin's main window in this process
        _erwinHwnd = FindErwinMainWindow();
        if (_erwinHwnd == IntPtr.Zero)
        {
            Log("Could not find erwin main window, falling back to background thread");
            var thread = new Thread(RunOnBackgroundThread);
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return 0;
        }

        Log($"erwin HWND: 0x{_erwinHwnd.ToInt64():X}");

        // Set a one-shot timer on erwin's window.
        // The callback fires on erwin's UI thread via WM_TIMER.
        unsafe
        {
            delegate* unmanaged<IntPtr, uint, UIntPtr, uint, void> callback = &OnTimerCallback;
            SetTimer(_erwinHwnd, (UIntPtr)0xE51F, 100, (IntPtr)callback);
        }

        Log("Timer set on erwin's UI thread, waiting for callback...");
        return 0;
    }

    /// <summary>
    /// Timer callback - runs on erwin's UI thread!
    /// </summary>
    [UnmanagedCallersOnly]
    static void OnTimerCallback(IntPtr hwnd, uint msg, UIntPtr id, uint time)
    {
        KillTimer(hwnd, id);
        Log("Timer callback fired on erwin's UI thread");
        ActivateAddIn();
    }

    static IntPtr FindErwinMainWindow()
    {
        uint myPid = GetCurrentProcessId();
        IntPtr found = IntPtr.Zero;

        EnumWindows((hWnd, lParam) =>
        {
            uint pid;
            GetWindowThreadProcessId(hWnd, out pid);
            if (pid == myPid)
            {
                // Check if this is a top-level visible window (erwin's main window)
                if (IsWindowVisible(hWnd) && GetParent(hWnd) == IntPtr.Zero)
                {
                    found = hWnd;
                    return false; // stop enumeration
                }
            }
            return true;
        }, IntPtr.Zero);

        return found;
    }

    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern IntPtr GetParent(IntPtr hWnd);

    /// <summary>
    /// COM activation logic - creates the add-in and calls Execute().
    /// When called from OnTimerCallback, this runs on erwin's UI thread.
    /// </summary>
    static void ActivateAddIn()
    {
        IntPtr pDispatch = IntPtr.Zero;

        try
        {
            Guid clsid;
            int hr = CLSIDFromProgID("EliteSoft.Erwin.AddIn", out clsid);
            if (hr != 0) { Log("ProgID not found"); return; }

            hr = CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_ALL, ref IID_IDispatch, out pDispatch);
            if (hr != 0 || pDispatch == IntPtr.Zero) { Log($"CoCreateInstance failed: 0x{hr:X8}"); return; }

            // GetIDsOfNames("Execute")
            IntPtr vtable = Marshal.ReadIntPtr(pDispatch);
            IntPtr getIdsPtr = Marshal.ReadIntPtr(vtable, IDISPATCH_GETIDSOFNAMES * IntPtr.Size);
            var getIds = Marshal.GetDelegateForFunctionPointer<GetIDsOfNamesDelegate>(getIdsPtr);

            string[] names = new string[] { "Execute" };
            int[] dispIds = new int[1];
            Guid iidNull = IID_NULL;
            hr = getIds(pDispatch, ref iidNull, names, 1, 0, dispIds);
            if (hr != 0) { Log($"GetIDsOfNames failed: 0x{hr:X8}"); return; }

            // Invoke Execute()
            Log("Invoking Execute on erwin's UI thread...");
            IntPtr invokePtr = Marshal.ReadIntPtr(vtable, IDISPATCH_INVOKE * IntPtr.Size);
            var invoke = Marshal.GetDelegateForFunctionPointer<InvokeDelegate>(invokePtr);

            DISPPARAMS dispParams = new DISPPARAMS();
            const ushort DISPATCH_METHOD = 0x1;
            hr = invoke(pDispatch, dispIds[0], ref iidNull, 0, DISPATCH_METHOD,
                ref dispParams, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

            if (hr == 0)
                Log("Execute completed");
            else
                Log($"Execute failed: 0x{hr:X8}");
        }
        catch (Exception ex)
        {
            Log($"ERROR: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (pDispatch != IntPtr.Zero)
            {
                try
                {
                    IntPtr vtable = Marshal.ReadIntPtr(pDispatch);
                    IntPtr releasePtr = Marshal.ReadIntPtr(vtable, IDISPATCH_RELEASE * IntPtr.Size);
                    var release = Marshal.GetDelegateForFunctionPointer<ReleaseDelegate>(releasePtr);
                    release(pDispatch);
                }
                catch { }
            }
        }
    }

    /// <summary>
    /// Fallback: runs on a background STA thread if erwin's window can't be found.
    /// </summary>
    static void RunOnBackgroundThread()
    {
        try
        {
            CoInitializeEx(IntPtr.Zero, COINIT_APARTMENTTHREADED);
            Thread.Sleep(500);
            ActivateAddIn();
        }
        finally
        {
            CoUninitialize();
        }
    }
}
