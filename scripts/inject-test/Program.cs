using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

/// <summary>
/// Injects TriggerDll.dll into erwin.exe, then calls its exported "Activate" function.
/// TriggerDll runs CoCreateInstance("EliteSoft.Erwin.AddIn") + Execute() inside erwin process.
///
/// Usage: ErwinInjector.exe [TriggerDll.dll path]
///   If no argument, looks for TriggerDll.dll next to this exe.
///
/// Exit codes: 0 = success, 1 = erwin not found, 2 = TriggerDll not found,
///             3 = process open failed, 4 = injection failed, 5 = Activate call failed
/// </summary>
class Program
{
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr addr, uint size, uint type, uint protect);
    [DllImport("kernel32.dll")] static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr addr, byte[] buffer, uint size, out int written);
    [DllImport("kernel32.dll")] static extern IntPtr CreateRemoteThread(IntPtr hProcess, IntPtr attr, uint stackSize, IntPtr startAddr, IntPtr param, uint flags, out uint threadId);
    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string name);
    [DllImport("kernel32.dll")] static extern IntPtr GetProcAddress(IntPtr hModule, string name);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle, uint ms);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr LoadLibraryW(string path);
    [DllImport("kernel32.dll")] static extern bool FreeLibrary(IntPtr hModule);

    const uint PROCESS_ALL_ACCESS = 0x1F0FFF;
    const uint MEM_COMMIT = 0x1000;
    const uint MEM_RESERVE = 0x2000;
    const uint PAGE_READWRITE = 0x04;

    static IntPtr InjectDll(IntPtr hProcess, string dllPath)
    {
        IntPtr kernel32 = GetModuleHandle("kernel32.dll");
        IntPtr loadLibAddr = GetProcAddress(kernel32, "LoadLibraryW");

        byte[] dllPathBytes = Encoding.Unicode.GetBytes(dllPath + "\0");
        IntPtr remoteAddr = VirtualAllocEx(hProcess, IntPtr.Zero, (uint)dllPathBytes.Length, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        if (remoteAddr == IntPtr.Zero) return IntPtr.Zero;

        int written;
        WriteProcessMemory(hProcess, remoteAddr, dllPathBytes, (uint)dllPathBytes.Length, out written);

        uint threadId;
        IntPtr hThread = CreateRemoteThread(hProcess, IntPtr.Zero, 0, loadLibAddr, remoteAddr, 0, out threadId);
        if (hThread == IntPtr.Zero) return IntPtr.Zero;

        WaitForSingleObject(hThread, 10000);
        CloseHandle(hThread);
        return remoteAddr;
    }

    static int Main(string[] args)
    {
        int mySession = Process.GetCurrentProcess().SessionId;
        var erwin = Process.GetProcessesByName("erwin")
            .FirstOrDefault(p => p.SessionId == mySession);

        if (erwin == null)
        {
            Console.Error.WriteLine("erwin not running in current session");
            return 1;
        }

        // Resolve TriggerDll path
        string triggerDll;
        if (args.Length > 0 && System.IO.File.Exists(args[0]))
        {
            triggerDll = System.IO.Path.GetFullPath(args[0]);
        }
        else
        {
            triggerDll = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TriggerDll.dll");
        }

        if (!System.IO.File.Exists(triggerDll))
        {
            Console.Error.WriteLine($"TriggerDll not found: {triggerDll}");
            return 2;
        }

        string triggerModuleName = System.IO.Path.GetFileName(triggerDll);

        IntPtr hProcess = OpenProcess(PROCESS_ALL_ACCESS, false, erwin.Id);
        if (hProcess == IntPtr.Zero)
        {
            Console.Error.WriteLine("Cannot open erwin process");
            return 3;
        }

        // Step 1: Inject TriggerDll into erwin
        var result = InjectDll(hProcess, triggerDll);
        if (result == IntPtr.Zero)
        {
            Console.Error.WriteLine("DLL injection failed");
            CloseHandle(hProcess);
            return 4;
        }

        Thread.Sleep(800); // Wait for DLL to load

        // Step 2: Calculate Activate offset by loading DLL locally
        IntPtr localModule = LoadLibraryW(triggerDll);
        if (localModule == IntPtr.Zero)
        {
            Console.Error.WriteLine("Cannot load TriggerDll locally");
            CloseHandle(hProcess);
            return 5;
        }

        IntPtr localActivate = GetProcAddress(localModule, "Activate");
        if (localActivate == IntPtr.Zero)
        {
            Console.Error.WriteLine("Activate export not found");
            FreeLibrary(localModule);
            CloseHandle(hProcess);
            return 5;
        }

        long offset = localActivate.ToInt64() - localModule.ToInt64();
        FreeLibrary(localModule);

        // Step 3: Find TriggerDll base in erwin's modules
        erwin.Refresh();
        var triggerModule = erwin.Modules.Cast<ProcessModule>()
            .FirstOrDefault(m => m.ModuleName.Equals(triggerModuleName, StringComparison.OrdinalIgnoreCase));

        if (triggerModule == null)
        {
            Console.Error.WriteLine("TriggerDll not found in erwin modules");
            CloseHandle(hProcess);
            return 5;
        }

        IntPtr remoteActivate = new IntPtr(triggerModule.BaseAddress.ToInt64() + offset);

        // Step 4: Call Activate in erwin
        uint threadId;
        IntPtr hThread = CreateRemoteThread(hProcess, IntPtr.Zero, 0, remoteActivate, IntPtr.Zero, 0, out threadId);
        if (hThread == IntPtr.Zero)
        {
            Console.Error.WriteLine("CreateRemoteThread for Activate failed");
            CloseHandle(hProcess);
            return 5;
        }

        WaitForSingleObject(hThread, 15000);
        CloseHandle(hThread);
        CloseHandle(hProcess);

        return 0;
    }
}
