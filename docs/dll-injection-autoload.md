# DLL Injection Auto-Load Mechanism

## Problem

erwin Data Modeler does not support auto-loading 3rd-party add-ins. Users must manually navigate **Tools > Add-Ins > Elite Soft Erwin Add-In** every time. This menu uses XTP Toolkit (Codejock) which blocks all automation approaches (SendKeys, UIAutomation, keyboard simulation).

SCAPI is an InprocServer32 COM object - it only sees models in the calling process. Any external process (VBScript, PowerShell) creates SCAPI in its own process and sees zero models.

## Solution: 3-Layer DLL Injection

```
[1] ErwinInjector.exe (Injector - runs externally)
         |
         | CreateRemoteThread + LoadLibraryW
         v
[2] TriggerDll.dll (NativeAOT native DLL - runs inside erwin.exe)
         |
         | CoCreateInstance + IDispatch::Invoke("Execute")
         v
[3] EliteSoft.Erwin.AddIn (COM add-in, .NET runtime - runs inside erwin.exe)
```

## Layer 1: ErwinInjector.exe (Injector)

**Location:** `scripts/erwin-injector/Program.cs`

1. Finds `erwin.exe` in the current user session
2. Opens the process with `PROCESS_ALL_ACCESS`
3. Allocates memory in erwin via `VirtualAllocEx`
4. Writes TriggerDll.dll path to that memory
5. Calls `CreateRemoteThread` with `LoadLibraryW` to load TriggerDll into erwin
6. Waits for DLL to load, then finds `Activate` export offset:
   - Loads TriggerDll locally to get `Activate` address
   - Calculates offset from DLL base
   - Finds TriggerDll's base address in erwin's module list
   - Computes remote `Activate` address
7. Calls `CreateRemoteThread` targeting remote `Activate` address

## Layer 2: TriggerDll.dll (NativeAOT Bridge)

**Location:** `scripts/erwin-injector/TriggerDll/TriggerDll.cs`

Compiled with `<PublishAot>true</PublishAot>` - produces a pure native DLL with no .NET runtime dependency.

**Why NativeAOT?** LoadLibraryW can only load native DLLs. A managed .NET DLL cannot be loaded this way.

**Why not use .NET COM Interop inside TriggerDll?** NativeAOT strips COM Interop support. `Type.GetTypeFromProgID()` throws `NotSupportedException`. Must use raw Win32 COM APIs.

### Activate() function flow:
1. Creates an STA thread (COM requires STA for UI)
2. `CoInitializeEx` - initializes COM
3. `CLSIDFromProgID("EliteSoft.Erwin.AddIn")` - looks up add-in's CLSID
4. `CoCreateInstance` - creates the add-in COM object, gets IDispatch pointer
5. `IDispatch::GetIDsOfNames("Execute")` - resolves DISPID via vtable
6. `IDispatch::Invoke(DISPID)` - calls Execute() on the add-in

All COM calls use direct vtable access (marshal function pointers from the IDispatch vtable).

## Layer 3: EliteSoft.Erwin.AddIn (.NET COM Add-In)

**Location:** `ErwinAddIn.cs`

When `Execute()` is called via injection:
- License validation
- SCAPI connection (works because we're inside erwin.exe)
- `Application.Run(form)` starts a message pump (required for injection path)
- `MB_TOPMOST | MB_SETFOREGROUND` flags on all MessageBox calls for visibility
- Splash screen with `TopMost = true` (removed after form shown)

## Key Technical Details

### Why DLL Injection is Necessary

| Approach | Result |
|----------|--------|
| VBScript COM | Runs in cscript.exe - SCAPI sees no models |
| PowerShell COM | Runs in powershell.exe - same problem |
| SendKeys/keybd_event | XTP ribbon ignores keyboard input |
| UIAutomation | XTP controls not exposed to UIA |
| DLL Injection | Runs inside erwin.exe - SCAPI sees all models |

### Message Pump Requirement

When the add-in is loaded via injection (not via erwin's menu), it runs on a background STA thread with no message loop. Without `Application.Run()`, WinForms windows become "Not Responding". The `Application.Run(form)` call blocks the STA thread and pumps messages until the form closes.

### Dialog Visibility

Dialogs created on injection threads appear behind erwin's window. Solution: use native `MessageBoxW` with `MB_TOPMOST | MB_SETFOREGROUND` flags instead of WinForms `MessageBox.Show`. For forms, set `TopMost = true` initially, then remove after `Shown` event.

### Build Requirements

TriggerDll NativeAOT publish requires:
- Visual Studio C++ Build Tools (link.exe)
- `vswhere.exe` in PATH (add `C:\Program Files (x86)\Microsoft Visual Studio\Installer`)
- Target: `net10.0-windows`, `win-x64`, `PublishAot=true`

### Csproj Exclusion

ErwinAddIn.csproj must exclude `scripts/**/*.cs` from compilation:
```xml
<DefaultItemExcludes>$(DefaultItemExcludes);scripts\**\*.cs</DefaultItemExcludes>
```
Otherwise SDK-style project includes TriggerDll/ErwinInjector source files and causes duplicate assembly attribute errors.

## Integration with Autostart Watcher

The autostart-watcher.ps1 (WMI event subscription) detects erwin.exe and waits for a model to open. Once detected, it should run the injector instead of keyboard simulation. The injector handles everything from there.

## File Map

| File | Purpose |
|------|---------|
| `scripts/erwin-injector/Program.cs` | Injector executable |
| `scripts/erwin-injector/ErwinInjector.csproj` | Injector project (net10.0) |
| `scripts/erwin-injector/TriggerDll/TriggerDll.cs` | NativeAOT bridge DLL |
| `scripts/erwin-injector/TriggerDll/TriggerDll.csproj` | NativeAOT project |
| `ErwinAddIn.cs` | COM add-in entry point |
| `scripts/autostart-watcher.ps1` | WMI process watcher |
| `scripts/erwin-launcher.ps1` | Manual launcher (legacy) |
