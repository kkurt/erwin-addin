# Elite Soft Meta Add-In - WMI Event-Driven Auto-Start Watcher
# Detects erwin.exe start via WMI event, waits for model to open,
# then activates the add-in via PostMessage(WM_COMMAND, AddinCmdId).
# Runs as a hidden Scheduled Task at user logon.

$erwinName = "erwin.exe"
$mySessionId = (Get-Process -Id $PID).SessionId
$modelCheckIntervalSec = 1
$modelTimeoutSec = 300  # Give up after 5 minutes if no model opened
$fallbackPollSec = 30

# $installDir / injector / triggerDll variables removed 2026-05-26 along
# with the injection fallback path. The watcher now auto-loads the addin
# via PostMessage WM_COMMAND only - no executables to validate at startup.

# Log goes to per-user LOCALAPPDATA so each user on a Machine-scope install
# has their own log (and Program Files is read-only at runtime anyway).
$logDir = Join-Path $env:LOCALAPPDATA "EliteSoft\ErwinAddIn-Logs"
$logFile = Join-Path $logDir "autostart.log"

function Write-Log([string]$msg) {
    $ts = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    $line = "[$ts] $msg"
    try {
        if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }
        Add-Content -Path $logFile -Value $line -ErrorAction SilentlyContinue
    } catch { }
}

function Get-MyErwin {
    return Get-Process -Name "erwin" -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $mySessionId }
}

# Detects "a model is open in erwin" from the main window title. erwin's MFC
# MDI host renders distinct title shapes:
#   maximized child   : "erwin DM - [Model1 : ER_Diagram_164]"   <- brackets
#   restored child    : "erwin DM - Model1"                       <- no brackets
#   Mart-only state   : "erwin DM - Mart://Mart/.../MetaRepo : v17"  <- no model yet!
#   no model          : "erwin Data Modeler" / "erwin DM"          <- no dash
#
# The Mart-only state is the false-positive that bit us 2026-05-25: erwin
# shows the Mart connection path in the title BEFORE any model is actually
# loaded into the MDI client. Posting WM_COMMAND in this state is a no-op
# because erwin's command map hasn't enabled addin commands yet (no active
# MDI child = addin entry is disabled in the standard MFC UPDATE_COMMAND_UI
# cycle).
#
# Detection rules:
#   1. Bracketed form (e.g. "[... : ER_Diagram_164]") = diagram active =>
#      addin command enabled. Safe to PostMessage.
#   2. Dash followed by Mart://-prefixed URL with no brackets => Mart
#      connection only, NO model yet. SKIP.
#   3. Dash followed by anything else (e.g. "erwin DM - Model1") =>
#      restored child with loaded model. Safe.
function Test-ErwinHasModel {
    param([string]$Title)
    if ([string]::IsNullOrWhiteSpace($Title)) { return $false }
    if ($Title -notmatch '^erwin\b.*\s-\s+\S') { return $false }
    if ($Title -match '\[[^\]]+\]')           { return $true }   # bracketed
    if ($Title -match '\s-\s+Mart://')        { return $false }  # Mart-only, no model
    return $true                                                  # restored child
}

# erwin DM r10 Add-In discovery only reads from HKCU. Watcher runs at every
# user's logon (Scheduled Task with INTERACTIVE-group trigger on Machine
# installs, per-user trigger on User installs), so each user gets their
# HKCU populated exactly once at first logon and self-healed on every
# subsequent logon if anything wipes it (profile reset, group policy, manual
# deletion). Hardcoded version 10.10 matches the addin's compile-time target
# - no version discovery needed (HKLM had stale 9.98 leftovers; HKCU was
# empty for fresh users; both were unreliable).
$erwinAddInVersion = "10.10"
# ProgID + menu display name renamed 2026-05-25 from "EliteSoft.Erwin.AddIn"
# + "Elite Soft Erwin Addin" to the names below. Self-heal also removes
# the legacy entry if it lingers from a pre-rename install.
$addInProgId = "EliteSoft.Meta.AddIn"
$addInDisplayName = "Elite Soft Meta Addin"
$legacyAddInDisplayName = "Elite Soft Erwin Addin"

function Register-HKCUAddIn {
    try {
        # First: clean up the legacy entry so we don't show two items in
        # Tools > Add-Ins after upgrade. Idempotent on fresh installs.
        $legacyPath = "HKCU:\SOFTWARE\erwin\Data Modeler\$erwinAddInVersion\Add-Ins\$legacyAddInDisplayName"
        if (Test-Path $legacyPath) {
            Remove-Item -LiteralPath $legacyPath -Recurse -Force -ErrorAction SilentlyContinue
            Write-Log "Legacy HKCU add-in entry '$legacyAddInDisplayName' removed (rename self-heal)"
        }

        $hkcuAddIn = "HKCU:\SOFTWARE\erwin\Data Modeler\$erwinAddInVersion\Add-Ins\$addInDisplayName"
        if (Test-Path $hkcuAddIn) { return }

        New-Item -Path $hkcuAddIn -Force -ErrorAction Stop | Out-Null
        Set-ItemProperty -Path $hkcuAddIn -Name "Menu Identifier" -Value 1 -Type DWord
        Set-ItemProperty -Path $hkcuAddIn -Name "ProgID" -Value $addInProgId -Type String
        Set-ItemProperty -Path $hkcuAddIn -Name "Invoke Method" -Value "Execute" -Type String
        Set-ItemProperty -Path $hkcuAddIn -Name "Invoke EXE" -Value 0 -Type DWord
        Write-Log "HKCU add-in entry written for erwin $erwinAddInVersion (per-user self-heal): '$addInDisplayName' -> $addInProgId"
    } catch {
        Write-Log "HKCU add-in registration failed: $($_.Exception.Message)"
    }
}

function Wait-ForModel {
    # Detect "erwin has a model loaded" by checking the title shape AND,
    # crucially, walking the MFC MDIClient hierarchy. Title-only detection
    # had a false-negative: when a Mart model is opened with the MDI child
    # in RESTORED (not maximized) state, the main frame title shows
    # "erwin DM - Mart://Mart/.../MetaRepo : v17" - identical to the
    # Mart-connected-no-model-yet state, so Test-ErwinHasModel returns
    # false even though the diagram IS open. The MDI client's active
    # child (WM_MDIGETACTIVE) is the source of truth: when a model is
    # loaded erwin always has an active MDI child, regardless of its
    # maximize state. Title check stays as a fast path for the common
    # maximized case.
    $elapsed = 0
    while ($elapsed -lt $modelTimeoutSec) {
        if (-not (Get-MyErwin)) {
            Write-Log "erwin closed while waiting for model"
            return $false
        }

        $erwinProc = Get-MyErwin | Where-Object { Test-ErwinHasModelComplete $_ } | Select-Object -First 1
        if ($erwinProc) {
            Write-Log "Model detected: '$($erwinProc.MainWindowTitle)' (hwnd=0x$('{0:X}' -f $erwinProc.MainWindowHandle.ToInt64()))"
            return $true
        }

        # No model yet. A modal startup dialog (license-expiry warning, Welcome/
        # Start Page) can DISABLE erwin's main frame, blocking both the model
        # load and the add-in command. OK it so startup can proceed (2026-07-13).
        $anyErwin = Get-MyErwin | Select-Object -First 1
        if ($anyErwin) {
            try {
                $dlgTitle = [WatcherWmPoster]::TopDialogTitle([uint32]$anyErwin.Id)
                if ([WatcherWmPoster]::DismissBlockingStartupDialog([uint32]$anyErwin.Id)) {
                    Write-Log "Dismissed a blocking startup dialog (OK): '$dlgTitle'"
                }
            } catch { }
        }

        Start-Sleep -Seconds $modelCheckIntervalSec
        $elapsed += $modelCheckIntervalSec
    }

    Write-Log "Timeout waiting for model ($modelTimeoutSec sec)"
    return $false
}

# WatcherWmPoster - Win32 PostMessage + MDI helpers used by the main loop.
# Hoisted to script-init time (was inline inside the WM_COMMAND post block)
# so Wait-ForModel can also call GetActiveMdiChild for the MDI-existence
# detection that bypasses the title-shape false-negative for restored
# MDI children. The MDI activation pattern mirrors the production code in
# Services/MartMartAutomation.cs:2421-2467 (erwin uses standard MFC
# MDIClient class, FindWindowExW with that class name finds it reliably).
try {
    Add-Type -Language CSharp -TypeDefinition @'
using System; using System.Runtime.InteropServices; using System.Text;
public static class WatcherWmPoster {
    [DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll", SetLastError=true)] public static extern IntPtr SendMessageTimeoutW(
        IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);
    public const uint SMTO_NORMAL          = 0x0;
    public const uint SMTO_ABORTIFHUNG     = 0x2;
    public const uint SMTO_NOTIMEOUTIFNOTHUNG = 0x8;

    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindowExW(IntPtr parent, IntPtr child, string cls, string wnd);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr p, EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder cls, int cap);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr hAfter, int x, int y, int w, int h_, uint flags);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindowEnabled(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint uCmd);
    [DllImport("user32.dll")] public static extern int GetDlgCtrlID(IntPtr h);
    public const uint GW_ENABLEDPOPUP = 6;
    public const uint WM_COMMAND = 0x0111;
    public const int  IDOK = 1;
    public const uint WM_MDIGETACTIVE = 0x0229;
    public const uint WM_MDIACTIVATE  = 0x0222;
    public const uint WM_MDIMAXIMIZE  = 0x0225;
    public const uint WM_CLOSE        = 0x0010;
    public const int  SW_HIDE         = 0;
    public const int  SW_SHOW         = 5;
    public const uint SWP_NOSIZE      = 0x0001;
    public const uint SWP_NOZORDER    = 0x0004;
    public const uint SWP_NOACTIVATE  = 0x0010;
    public const uint SWP_HIDEWINDOW  = 0x0080;

    // Public so PS callers can use it for diagnostics.
    public static IntPtr FindMdiClient(IntPtr mainFrame) {
        if (mainFrame == IntPtr.Zero) return IntPtr.Zero;
        IntPtr direct = FindWindowExW(mainFrame, IntPtr.Zero, "MDIClient", null);
        if (direct != IntPtr.Zero) return direct;
        IntPtr found = IntPtr.Zero;
        EnumChildWindows(mainFrame, (h, _) => {
            var cls = new StringBuilder(64);
            GetClassNameW(h, cls, cls.Capacity);
            if (cls.ToString() == "MDIClient") { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    // Returns active MDI child handle (zero if no MDIClient or no active
    // child). Used as the ground-truth signal for "is a model loaded";
    // erwin always activates a child window when a model is open,
    // regardless of maximize state.
    public static IntPtr GetActiveMdiChild(IntPtr mainFrame) {
        IntPtr client = FindMdiClient(mainFrame);
        if (client == IntPtr.Zero) return IntPtr.Zero;
        IntPtr activeChild;
        SendMessageTimeoutW(client, WM_MDIGETACTIVE, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 500, out activeChild);
        return activeChild;
    }

    // Finds erwin's XTPMainFrame top-level window for the given process.
    public static IntPtr FindMainFrame(uint pid) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, _) => {
            if (!IsWindowVisible(h)) return true;
            uint wpid; GetWindowThreadProcessId(h, out wpid);
            if (wpid != pid) return true;
            var cls = new StringBuilder(64); GetClassNameW(h, cls, cls.Capacity);
            if (cls.ToString() == "XTPMainFrame") { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    // erwin can show a modal startup dialog BEFORE the add-in loads that blocks
    // both the model load and the add-in command, so a human has to click OK:
    //   * license-expiry warning: an OWNERLESS #32770 titled "erwin Data
    //     Modeler" that appears BEFORE the main frame even exists (dump
    //     2026-07-13: owner=0, no XTPMainFrame yet) - a main-frame-relative
    //     lookup can't see it.
    //   * Welcome / Start Page: an owned modal that DISABLES the main frame.
    // Handled in that order. Called each Wait-ForModel iteration (no model is
    // open yet, so any erwin #32770 is a startup blocker - safe to OK). MFC
    // dialogs treat WM_COMMAND(IDOK) as the default OK action. Returns true if
    // it acted (so the caller can log it).
    public static bool DismissBlockingStartupDialog(uint pid) {
        // (a) Any visible #32770 dialog of the erwin process (license warning
        //     is ownerless + pre-main-frame, so match by process + class).
        IntPtr dlg = IntPtr.Zero;
        EnumWindows((h, _) => {
            if (!IsWindowVisible(h)) return true;
            uint wpid; GetWindowThreadProcessId(h, out wpid);
            if (wpid != pid) return true;
            var cls = new StringBuilder(64); GetClassNameW(h, cls, cls.Capacity);
            if (cls.ToString() != "#32770") return true;
            dlg = h;
            return false;
        }, IntPtr.Zero);

        // (b) Otherwise an owned modal (Welcome/Start Page) disabling the main frame.
        if (dlg == IntPtr.Zero) {
            IntPtr main = FindMainFrame(pid);
            if (main == IntPtr.Zero || IsWindowEnabled(main)) return false;
            IntPtr modal = GetWindow(main, GW_ENABLEDPOPUP);
            if (modal == IntPtr.Zero || modal == main || !IsWindow(modal)) return false;
            dlg = modal;
        }

        return ClickDialogOkButton(dlg);
    }

    // Clicks the dialog's OK/Tamam button BY ITS REAL CONTROL ID. erwin's
    // license popup gives OK the id 2 (normally IDCANCEL), not IDOK/1 (dump
    // 2026-07-13), so a fixed WM_COMMAND(IDOK) is a no-op. Find the Button
    // child whose caption is OK/Tamam, read its actual id, and post
    // WM_COMMAND(id) with the button hwnd (the XTP/MFC-safe form). Falls back
    // to trying ids 1 then 2 when no captioned OK button is found.
    private static bool ClickDialogOkButton(IntPtr dlg) {
        IntPtr btn = IntPtr.Zero; int id = 0;
        EnumChildWindows(dlg, (h, _) => {
            var c = new StringBuilder(32); GetClassNameW(h, c, c.Capacity);
            if (c.ToString() != "Button") return true;
            var t = new StringBuilder(64); GetWindowTextW(h, t, t.Capacity);
            string tx = t.ToString().Replace("&", "").Trim();
            if (string.Equals(tx, "OK", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tx, "Tamam", StringComparison.OrdinalIgnoreCase)) {
                btn = h; id = GetDlgCtrlID(h); return false;
            }
            return true;
        }, IntPtr.Zero);
        if (btn != IntPtr.Zero) {
            PostMessageW(dlg, WM_COMMAND, (IntPtr)id, btn);
            return true;
        }
        // Fallback: no captioned OK found - try the two common ids.
        PostMessageW(dlg, WM_COMMAND, (IntPtr)1, IntPtr.Zero);
        PostMessageW(dlg, WM_COMMAND, (IntPtr)2, IntPtr.Zero);
        return true;
    }

    // Reads the title of the top visible #32770 dialog of the erwin process
    // (for logging which startup dialog was dismissed). Empty if none.
    public static string TopDialogTitle(uint pid) {
        string title = "";
        EnumWindows((h, _) => {
            if (!IsWindowVisible(h)) return true;
            uint wpid; GetWindowThreadProcessId(h, out wpid);
            if (wpid != pid) return true;
            var cls = new StringBuilder(64); GetClassNameW(h, cls, cls.Capacity);
            if (cls.ToString() != "#32770") return true;
            var t = new StringBuilder(256); GetWindowTextW(h, t, t.Capacity);
            title = t.ToString();
            return false;
        }, IntPtr.Zero);
        return title;
    }

    public const uint WM_NULL = 0x0000;

    // Block until erwin's UI thread is actually PUMPING messages (or the
    // overall budget runs out). A WM_NULL no-op that comes back means the
    // thread answered, i.e. it is NOT stuck in a long synchronous model
    // parse; SMTO_ABORTIFHUNG returns fast if the thread is genuinely hung.
    // Used to defer the Add-In Manager open until the thread can immediately
    // hide+close it - otherwise a cross-thread ShowWindow(SW_HIDE) is queued
    // unprocessed and the dialog sits VISIBLE over the loading model.
    // Returns true if the thread became responsive, false on budget timeout.
    public static bool WaitForResponsive(IntPtr hwnd, int overallTimeoutMs) {
        if (hwnd == IntPtr.Zero) return false;
        for (int waited = 0; waited < overallTimeoutMs; waited += 250) {
            IntPtr res;
            IntPtr ok = SendMessageTimeoutW(hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 300, out res);
            if (ok != IntPtr.Zero) return true;
            System.Threading.Thread.Sleep(250);
        }
        return false;
    }

    // Find a top-level modal dialog (class #32770) owned by the given
    // process whose title contains the given substring. Returns IntPtr.Zero
    // when no match. Title match is OrdinalIgnoreCase. The class #32770
    // is the standard Win32 dialog class - erwin's MFC dialogs (including
    // Add-In Manager) all use it.
    public static IntPtr FindDialogByProcess(uint pid, string titleSubstr) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, _) => {
            uint wndPid; GetWindowThreadProcessId(h, out wndPid);
            if (wndPid != pid) return true;
            var cls = new StringBuilder(64);
            GetClassNameW(h, cls, cls.Capacity);
            if (cls.ToString() != "#32770") return true;
            if (string.IsNullOrEmpty(titleSubstr)) { found = h; return false; }
            var t = new StringBuilder(256);
            GetWindowTextW(h, t, t.Capacity);
            if (t.ToString().IndexOf(titleSubstr, StringComparison.OrdinalIgnoreCase) >= 0) {
                found = h; return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    // Force erwin's lazy addin activation: post WM_COMMAND 1179 (the
    // Manage-Add-Ins menu command), wait for the resulting #32770 dialog
    // to appear, immediately hide it off-screen + ShowWindow(SW_HIDE),
    // let erwin finish wiring the per-addin cmd id bindings (the work
    // that the dialog's enumeration triggers), then post WM_CLOSE to
    // dismiss it. After this returns, posting WM_COMMAND for the addin's
    // cmd id (1181 in our HKCU.AddinCmdId) dispatches into Execute()
    // reliably even on cold-launch erwin sessions where lazy activation
    // hadn't run yet.
    //
    // Returns: 0 = dialog never appeared (post may have been dropped),
    // 1 = dialog found, hidden, and close posted (activation expected).
    // Total cost ~300-800 ms depending on how fast erwin opens the dialog.
    // Structural verification: an Add-In Manager dialog hosts a list
    // control (typically SysListView32) showing the registered addins.
    // Transient erwin #32770s (error popups, simple alerts) do not, so
    // checking for ANY child window with "List" in its class name is a
    // reliable filter for "this is the Add-In Manager" when the title
    // string is localized and we cannot match it directly.
    public static bool HasAddinManagerStructure(IntPtr dlg) {
        bool hasList = false;
        EnumChildWindows(dlg, (h, _) => {
            var cls = new StringBuilder(64);
            GetClassNameW(h, cls, cls.Capacity);
            if (cls.ToString().IndexOf("List", StringComparison.OrdinalIgnoreCase) >= 0) {
                hasList = true; return false;
            }
            return true;
        }, IntPtr.Zero);
        return hasList;
    }

    public static int TriggerLazyActivation(IntPtr mainFrame, uint erwinPid) {
        // Wait until erwin's UI thread is actually PUMPING before opening the
        // Add-In Manager. On a FILE-double-click cold launch the thread is
        // BUSY parsing the model (incl. "Creating DV Components") for ~20+ s.
        // If we post 1179 during that window the dialog opens VISIBLE but our
        // cross-thread ShowWindow(SW_HIDE) is queued unprocessed, so the
        // Add-In Manager sits in front of the loading model until the thread
        // frees - the user ends up dismissing it by hand (reported 2026-06-15:
        // "welcome closed but Add-In Manager stayed open; I closed it, not the
        // watcher"). Deferring the open until the thread answers a WM_NULL
        // means the subsequent SW_HIDE + WM_CLOSE are processed immediately, so
        // the dialog opens+hides+closes in one tight window and never lingers
        // visible - matching the warm open-then-load flow. The cmd-1181 binding
        // still happens on dialog-open, just after the load settles. 60 s budget
        // covers a big-model + DV-component cold load; if it never settles we
        // fall through and try anyway (no worse than before).
        WaitForResponsive(mainFrame, 60000);

        // 1179 = Manage Add-Ins dialog (from erwin string table +
        // EM_CMP.dll dispatch). Verified 2026-05-26 from the user's
        // wmcmd.log: opening Tools > Add-Ins... was what flipped cmd
        // 1181 into a bound state on subsequent PostMessages.
        PostMessageW(mainFrame, 0x0111, (IntPtr)1179, IntPtr.Zero);

        // Poll up to 2 s for the dialog to appear. STRICT match: try
        // localized title fragments first; if none match, accept a
        // #32770 owned by erwin that has Add-In Manager STRUCTURE (a
        // list control). The OLD "any #32770" fallback sometimes picked
        // a transient unrelated dialog, hid+closed THAT, then the real
        // Add-In Manager opened later and stayed open (user-reported
        // intermittent bug 2026-05-29).
        string[] titles = new[] { "Add-In", "Add-in", "Eklenti" };
        IntPtr dlg = IntPtr.Zero;
        for (int i = 0; i < 40 && dlg == IntPtr.Zero; i++) {
            System.Threading.Thread.Sleep(50);
            foreach (var t in titles) {
                dlg = FindDialogByProcess(erwinPid, t);
                if (dlg != IntPtr.Zero) break;
            }
            if (dlg != IntPtr.Zero) break;
            // Structural fallback: walk erwin's #32770s and accept one
            // with a List child (the addin list). Replaces the old
            // permissive "any #32770" pick.
            EnumWindows((h, _) => {
                uint p; GetWindowThreadProcessId(h, out p);
                if (p != erwinPid) return true;
                var c = new StringBuilder(64);
                GetClassNameW(h, c, c.Capacity);
                if (c.ToString() != "#32770") return true;
                if (HasAddinManagerStructure(h)) { dlg = h; return false; }
                return true;
            }, IntPtr.Zero);
        }
        if (dlg == IntPtr.Zero) return 0;

        // Hide (SW_HIDE only, NO offscreen move): if the close-verify
        // loop below fails we un-hide so the user can dismiss the
        // dialog manually instead of leaving it stranded offscreen
        // (the prior offscreen+SWP_HIDEWINDOW combo did exactly that).
        ShowWindow(dlg, SW_HIDE);

        // Give erwin a moment to finish per-addin CoCreateInstance +
        // cmd id binding. 200 ms is generous; the work is mostly done
        // by the time WM_INITDIALOG returned.
        System.Threading.Thread.Sleep(200);

        // Close-and-verify loop: re-post WM_CLOSE (and IDCANCEL) and poll
        // IsWindow; return the instant the dialog dies. The dialog stays
        // HIDDEN throughout so the user never sees it.
        //
        // Budget raised 2 s -> 30 s (2026-06-15): on a FILE-double-click
        // cold launch the erwin UI thread is BUSY parsing the model (the
        // Welcome screen is still up, title shows "* "), so the Add-In
        // Manager's message pump is starved and our posted closes just sit
        // queued. The old ~2 s budget exhausted DURING the load, un-hid the
        // dialog, and then stopped posting - so when the thread finally
        // resumed there was no fresh WM_CLOSE to process and the dialog
        // stranded over the Welcome screen, blocking it (user-reported). We
        // now keep re-posting until the load finishes and one post lands on
        // the now-pumping dialog. Returns early the moment the dialog dies,
        // so a responsive (warm) thread still closes it in ~100 ms - the
        // normal-launch path is unchanged.
        const int closeBudgetMs = 30000;
        for (int waited = 0; waited < closeBudgetMs; waited += 200) {
            PostMessageW(dlg, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            System.Threading.Thread.Sleep(100);
            if (!IsWindow(dlg)) return 1;
            // IDCANCEL = standard dialog "Cancel" command id; on a
            // #32770 it routes through the same handler as WM_CLOSE
            // but via the dialog manager's command path - useful when
            // WM_CLOSE itself was filtered/queued behind another
            // message.
            PostMessageW(dlg, 0x0111, (IntPtr)2, IntPtr.Zero);
            System.Threading.Thread.Sleep(100);
            if (!IsWindow(dlg)) return 1;
        }

        // Still alive after the full budget - truly stuck. Un-hide so the
        // user can dismiss it manually (better than stranding it hidden).
        ShowWindow(dlg, SW_SHOW);
        return 2;
    }

    // Activate + maximize the active MDI child to flip erwin's
    // UPDATE_COMMAND_UI cycle into "addin command enabled" state for
    // restored-MDI-child cases. Returns 0 = no MDIClient, 1 = no active
    // child, 2 = activated+maximized. Encoded as int because Add-Type
    // compiled C# handles primitives better than out params from PS.
    public static int ActivateAndMaximizeMdiChild(IntPtr mainFrame) {
        IntPtr client = FindMdiClient(mainFrame);
        if (client == IntPtr.Zero) return 0;
        IntPtr activeChild;
        SendMessageTimeoutW(client, WM_MDIGETACTIVE, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 1000, out activeChild);
        if (activeChild == IntPtr.Zero) return 1;
        IntPtr discard;
        SendMessageTimeoutW(client, WM_MDIACTIVATE, activeChild, IntPtr.Zero, SMTO_ABORTIFHUNG, 1000, out discard);
        SendMessageTimeoutW(client, WM_MDIMAXIMIZE, activeChild, IntPtr.Zero, SMTO_ABORTIFHUNG, 1000, out discard);
        return 2;
    }
}
'@ -ErrorAction Stop
} catch {
    Write-Log "Add-Type WatcherWmPoster failed at init: $($_.Exception.Message)"
}

# Combined "is a model loaded" detector. Order:
#   1. Fast title check (Test-ErwinHasModel) - covers the common
#      maximized-MDI-child case via bracket regex; cheap, no Win32 calls.
#   2. MDI hierarchy fallback - the only signal that survives the
#      restored-MDI-child + Mart-only-shaped main title combination
#      (verified 2026-05-26 on Kursat's user: model loaded fully, title
#      stayed "erwin DM - Mart://Mart/Kursat/MetaRepo : v17" with no
#      diagram suffix, Test-ErwinHasModel kept returning false, watcher
#      hung in Wait-ForModel for 12+ minutes).
function Test-ErwinHasModelComplete {
    param($Proc)
    if (-not $Proc) { return $false }
    if (Test-ErwinHasModel $Proc.MainWindowTitle) { return $true }
    $hwnd = $Proc.MainWindowHandle
    if ($hwnd -eq [IntPtr]::Zero) { return $false }
    try {
        return ([WatcherWmPoster]::GetActiveMdiChild($hwnd) -ne [IntPtr]::Zero)
    } catch {
        Write-Log "MDI detection threw (falling back to title-only): $($_.Exception.Message)"
        return $false
    }
}

Write-Log "Watcher started (PID=$PID, Session=$mySessionId)"

# Shared graceful-shutdown channel. installer/install-impl.ps1 signals this
# named Event to ask running watchers to exit cleanly before the install
# tears down COM/files. Replaces the old WMI Win32_Process.Terminate() call
# which triggered SEP's AGR.Terminate!g2 heuristic (`script kills another
# script host` malware pattern). Sleeps in the main loop poll this handle.
$shutdownEventName = 'EliteSoft.ErwinAddIn.Watcher.Shutdown'
$shutdownEvent = $null
try {
    $created = $false
    $shutdownEvent = [System.Threading.EventWaitHandle]::new($false, [System.Threading.EventResetMode]::ManualReset, $shutdownEventName, [ref]$created)
    Write-Log "Shutdown event '$shutdownEventName' acquired (created=$created)"
} catch {
    Write-Log "WARNING: could not open shutdown event '$shutdownEventName': $($_.Exception.Message)"
}

# Mirror HKLM add-in entries to this user's HKCU if missing (erwin DM r10
# requires per-user HKCU entry; HKLM alone does NOT make the add-in appear).
Register-HKCUAddIn

# Signal-then-exit-Stop-Process replaces the old WMI Terminate. New
# duplicates running in parallel are expected to honour the named event
# and quit on their own; only stragglers fall through to Stop-Process.
# This pattern matches install-impl.ps1's shutdown logic so SEP's
# AGR.Terminate heuristic never sees a powershell.exe -> powershell.exe
# Win32_Process.Terminate call from us.
try {
    if ($shutdownEvent) {
        [void]$shutdownEvent.Set()
        # We just signalled including OURSELVES - immediately reset so we
        # don't exit our own main loop. The other instances saw the Set
        # edge at WaitOne() and are exiting.
        Start-Sleep -Milliseconds 200
        [void]$shutdownEvent.Reset()
    }
    $duplicates = @(Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -match 'autostart-watcher' -and $_.ProcessId -ne $PID } |
        Select-Object -ExpandProperty ProcessId)
    foreach ($pidVal in $duplicates) {
        try {
            $alive = Get-Process -Id $pidVal -ErrorAction SilentlyContinue
            if (-not $alive) { continue }   # gracefully exited via event
            Write-Log "Killing duplicate watcher PID=$pidVal (Stop-Process; event did not reach it)"
            Stop-Process -Id $pidVal -Force -ErrorAction Stop
        } catch {
            Write-Log "WARNING: could not stop duplicate watcher PID=${pidVal}: $($_.Exception.Message)"
        }
    }
} catch { Write-Log "duplicate-watcher cleanup threw: $($_.Exception.Message)" }

# Injector pre-flight check removed 2026-05-26 - PostMessage auto-load
# path replaces the injection mechanism. No external binaries needed.

# --- DDL-generator mode (Phase 6, 2026-07-12) -----------------------------
# When the add-in is deployed as the dedicated DDL-generator flavor, the
# worker VM must run erwin unattended: nobody opens erwin or a model. This
# watcher then LAUNCHES erwin itself with a throwaway bootstrap model (a
# read-only blank .erwin) so the add-in command is enabled and loads
# (a model-less erwin ignores the command - S1 spike). The add-in closes the
# bootstrap right after loading, logs into Mart, and starts processing the
# queue. Config lives in HKCU (written by the installer / build-and-run
# -DdlGenerator): DdlGeneratorMode (DWORD), BootstrapModelPath, ErwinExePath.
$watcherCfgPath = 'HKCU:\Software\EliteSoft\ErwinAddIn\Watcher'
$ddlGenMode = $false
$bootstrapPath = ''
$erwinExePath = ''
try {
    if (Test-Path $watcherCfgPath) {
        $p = Get-ItemProperty -Path $watcherCfgPath -ErrorAction SilentlyContinue
        if ($p.DdlGeneratorMode -eq 1) { $ddlGenMode = $true }
        if ($p.BootstrapModelPath) { $bootstrapPath = [string]$p.BootstrapModelPath }
        if ($p.ErwinExePath)       { $erwinExePath  = [string]$p.ErwinExePath }
    }
} catch { Write-Log "DDL-gen config read failed: $($_.Exception.Message)" }

# Resolve erwin.exe: explicit HKCU value first, then the known r10 locations.
function Resolve-ErwinExe {
    if ($erwinExePath -and (Test-Path $erwinExePath)) { return $erwinExePath }
    foreach ($c in @(
        'C:\Program Files\erwin\Data Modeler r10\erwin.exe',
        'C:\Program Files (x86)\erwin\Data Modeler r10\erwin.exe')) {
        if (Test-Path $c) { return $c }
    }
    return $null
}

# Launch erwin with the bootstrap model and wait for our-session erwin to
# appear. Returns $true when it is up, $false on any failure (caller backs off).
function Start-ErwinWithBootstrap {
    $exe = Resolve-ErwinExe
    if (-not $exe) { Write-Log "DDL-gen: erwin.exe not found (HKCU ErwinExePath + known paths) - cannot launch"; return $false }
    if (-not $bootstrapPath -or -not (Test-Path $bootstrapPath)) {
        Write-Log "DDL-gen: bootstrap model not found ('$bootstrapPath') - cannot launch"; return $false
    }
    Write-Log "DDL-gen: launching erwin '$exe' with bootstrap '$bootstrapPath'"
    try { Start-Process -FilePath $exe -ArgumentList ('"' + $bootstrapPath + '"') } catch {
        Write-Log "DDL-gen: erwin launch failed: $($_.Exception.Message)"; return $false
    }
    for ($i = 0; $i -lt 30; $i++) {
        Start-Sleep -Seconds 1
        if (Get-MyErwin) { Write-Log "DDL-gen: erwin present after launch"; return $true }
    }
    Write-Log "DDL-gen: erwin did not appear within 30s after launch"
    return $false
}

if ($ddlGenMode) { Write-Log "DDL-generator mode ON (bootstrap='$bootstrapPath', erwinExe='$erwinExePath')" }

while ($true) {
  # Outer guard: any unhandled exception in the iteration body must NOT kill
  # the watcher. Watcher silently died once on 2026-05-02 and only logon-cycle
  # would have brought it back; we now log the failure and resume the loop so
  # transient WMI / Get-Process / Start-Process glitches can't take us out.
  try {
    # Skip if erwin is already running
    $existing = Get-MyErwin
    if (-not $existing -and $ddlGenMode) {
        # DDL-generator mode: don't wait for a human to open erwin - launch it
        # ourselves with the bootstrap model. On failure, back off and retry.
        if (Start-ErwinWithBootstrap) {
            $detected = $true
        } else {
            Start-Sleep -Seconds 15
            continue
        }
    }
    elseif (-not $existing) {
        # --- WMI Event: wait for erwin.exe to start ---
        $wmiQuery = "SELECT * FROM __InstanceCreationEvent WITHIN 2 WHERE TargetInstance ISA 'Win32_Process' AND TargetInstance.Name = '$erwinName'"
        $detected = $false

        try {
            $eventId = "ErwinStartEvent_$(Get-Random)"
            Register-WmiEvent -Query $wmiQuery -SourceIdentifier $eventId -ErrorAction Stop

            Write-Log "Waiting for erwin.exe (WMI event)..."

            $wmiEvent = Wait-Event -SourceIdentifier $eventId -Timeout $fallbackPollSec
            Unregister-Event -SourceIdentifier $eventId -ErrorAction SilentlyContinue
            Remove-Event -SourceIdentifier $eventId -ErrorAction SilentlyContinue

            if ($wmiEvent) {
                $myErwin = Get-MyErwin
                if ($myErwin) {
                    $detected = $true
                    Write-Log "erwin.exe detected in our session (WMI event)"
                } else {
                    Write-Log "erwin.exe started in different session - ignoring"
                }
            }
        } catch {
            Write-Log "WMI event failed: $_ - falling back to poll"
            Start-Sleep -Seconds $fallbackPollSec
            if (Get-MyErwin) { $detected = $true }
        }

        if (-not $detected) { continue }
    } else {
        Write-Log "erwin.exe already running in our session"
    }

    # --- Wait for model to be opened ---
    Write-Log "Waiting for model to open..."
    if (-not (Wait-ForModel)) {
        # No model opened or erwin closed - resume watching
        Start-Sleep -Seconds 3
        continue
    }

    # --- Remember which erwin PID we're injecting into ---
    $targetErwin = Get-MyErwin | Where-Object { Test-ErwinHasModelComplete $_ } | Select-Object -First 1
    $targetPid = $targetErwin.Id
    Write-Log "Target erwin PID=$targetPid"

    # Model is already detected via window title - no extra delay needed

    # --- Activate add-in: PostMessage WM_COMMAND with saved cmd id ---
    #
    # The cmd id (1181 with one addin registered on r10.10) is dynamic per
    # registration but stable across erwin restarts. It's discovered+
    # persisted by WmCommandLogger in the addin's Execute(): on the first
    # ever manual click the subclass installs; on the second click it
    # captures the wParam and writes it to HKCU\Software\EliteSoft\
    # ErwinAddIn\Watcher\AddinCmdId. The watcher reads it from there.
    #
    # PostMessage WM_COMMAND is the SAME mechanism erwin uses internally
    # when the user clicks Tools > Add-Ins > Elite Soft Meta Addin -
    # plain cross-process Win32 message with scalar args, no memory ops,
    # no thread creation, ZERO AV heuristics.
    #
    # The legacy ErwinInjector.exe + TriggerDll.dll injection fallback was
    # removed 2026-05-26 (SEP SONAR.ProcHijack-flagged + obsolete now).
    # If savedCmdId is missing (fresh install before any manual click),
    # the watcher just logs and waits - user does one manual menu click
    # which persists the id and unblocks every subsequent session.
    $regPath = 'HKCU:\Software\EliteSoft\ErwinAddIn\Watcher'
    $savedCmdId = 0
    try {
        if (Test-Path $regPath) {
            $idVal = (Get-ItemProperty -Path $regPath -Name 'AddinCmdId' -ErrorAction SilentlyContinue).AddinCmdId
            if ($null -ne $idVal) { $savedCmdId = [int]$idVal }
        }
    } catch { Write-Log "Registry lookup failed: $($_.Exception.Message)" }

    $loaded = $false
    if ($savedCmdId -gt 0) {
        # AV-clean primary path: post WM_COMMAND to erwin's main window.
        # erwin's command map dispatches the same way as a manual menu click:
        # CoCreateInstance(EliteSoft.Erwin.AddIn) -> Invoke("Execute") -> addin
        # form appears. No injector binary involved.
        try {
            # WatcherWmPoster type loaded once at script init (hoisted out
            # of the loop). See the Add-Type block immediately after the
            # Wait-ForModel function definition.

            # Pre-flight Start-Sleep removed (was 2 s): with the new
            # single-post + tight-poll retry logic below, posting
            # immediately is safe even when the command map isn't yet
            # ready. If the first post is dropped (command UPDATE_UI
            # disabled), the 20 s poll window expires and the retry
            # post catches it. Happy-path savings: 2 s shaved off the
            # perceived "addin loads late after model" latency.
            $mainHwnd = $targetErwin.MainWindowHandle
            if ($mainHwnd -eq [IntPtr]::Zero) {
                Write-Log "MainWindowHandle is zero - giving erwin 1s to settle"
                Start-Sleep -Seconds 1
                $targetErwin.Refresh()
                $mainHwnd = $targetErwin.MainWindowHandle
            }
            if ($mainHwnd -eq [IntPtr]::Zero) {
                Write-Log "Still no MainWindowHandle - manual click needed"
            } else {
                # Snapshot the addin's wmcmd.log size BEFORE the post so
                # we can verify the addin actually loaded by observing a
                # NEW EXECUTE entry written by its Execute().
                $wmcmdLog = Join-Path $env:LOCALAPPDATA 'EliteSoft\ErwinAddIn\wmcmd.log'
                $logSizeBefore = 0
                if (Test-Path $wmcmdLog) { $logSizeBefore = (Get-Item $wmcmdLog).Length }

                # Aggressive but de-duplicated retry. erwin's MFC command
                # map can drop WM_COMMAND when UPDATE_COMMAND_UI marks the
                # entry disabled - happens transiently while a big Mart
                # model parses geometry (UI thread busy >5 s), and
                # persistently while the MDI child is in "restored" (not
                # maximized) state. In both cases the title looks bracketed
                # so Test-ErwinHasModel returns true, but the dispatch
                # silently fails until command state flips to enabled
                # (model parse finishes / user maximizes the MDI child).
                #
                # Strategy: post every 2 s and tight-poll wmcmd.log inside
                # each window. The first post that lands while command is
                # enabled triggers Execute() within ~1.6 s (CoreCLR cold
                # start) and we see growth on the next poll tick. Cap at
                # 30 attempts = 60 s of patience (covers SQL_BUYUKMODEL
                # cold-load + user-maximize delay). Beyond that the late-
                # retry loop below keeps trying with longer cadence.
                #
                # Safe to retry aggressively because the addin's
                # ErwinAddIn.Execute() has both an active-form guard
                # (returns BringToFront for already-open form) and a
                # license-popup latch (only first failed CheckLicense pops
                # a dialog), so worst-case duplicate dispatches degrade to
                # cheap no-ops, not user-visible duplicates.
                # MDI activation pre-step: programatically make sure erwin's
                # MDI child is active+maximized BEFORE the first WM_COMMAND
                # post. Without this, models opened with a restored MDI
                # child stay command-disabled (UPDATE_COMMAND_UI cycle
                # rejects the addin entry), all posts get silently dropped,
                # and the user has to manually maximize before the addin
                # appears. Run once on attempt #1 only - subsequent retries
                # honour any manual restore the user did. Bridge logic
                # mirrored from Services/MartMartAutomation.cs:2421-2467
                # (already proven on erwin r10.10).
                $mdiResult = [WatcherWmPoster]::ActivateAndMaximizeMdiChild($mainHwnd)
                switch ($mdiResult) {
                    0 { Write-Log "MDI pre-step: MDIClient class not found - skipping" }
                    1 { Write-Log "MDI pre-step: MDIClient found but no active child yet" }
                    2 { Write-Log "MDI pre-step: WM_MDIACTIVATE + WM_MDIMAXIMIZE sent to active child" }
                }
                # Brief settle so erwin's UPDATE_COMMAND_UI cycle picks up
                # the new active-child state before our first WM_COMMAND
                # lands - empirically 100 ms is enough on r10.10.
                if ($mdiResult -eq 2) { Start-Sleep -Milliseconds 100 }

                # Force erwin's lazy addin activation. Without this, on
                # certain erwin cold-launch sessions cmd id 1181 is in
                # the menu list but NOT bound to a real handler yet; all
                # subsequent PostMessages get dropped. Empirically the
                # ONLY way to flip erwin out of the lazy state is for
                # something to enumerate Tools > Add-Ins (either via the
                # menu or the manager dialog). PostMessage WM_COMMAND
                # 1179 (Manage Add-Ins dialog) triggers that enumeration;
                # we hide+close the dialog before the user sees it. Cost
                # ~250-800 ms on first launch, near-zero on cached
                # sessions (we still attempt but it's cheap).
                $actResult = [WatcherWmPoster]::TriggerLazyActivation($mainHwnd, [uint32]$targetPid)
                switch ($actResult) {
                    0 { Write-Log "Lazy activation: dialog never appeared (WM_COMMAND 1179 may have been dropped)" }
                    1 { Write-Log "Lazy activation: Add-Ins dialog opened, hidden, closed cleanly - cmd id binding should be live now" }
                    2 { Write-Log "Lazy activation: WARN dialog opened but close attempts exhausted - dialog un-hidden so user can dismiss it manually. Cmd id binding likely OK (activation work happens before close)." }
                }
                Start-Sleep -Milliseconds 100

                $maxAttempts    = 30
                $attemptWaitMs  = 2000
                $pollIntervalMs = 200
                for ($attempt = 1; $attempt -le $maxAttempts; $attempt++) {
                    Write-Log "PostMessage attempt ${attempt}: WM_COMMAND id=$savedCmdId to erwin PID=$targetPid hwnd=0x$('{0:X}' -f $mainHwnd.ToInt64())"
                    $ok = [WatcherWmPoster]::PostMessageW($mainHwnd, 0x0111, [IntPtr]$savedCmdId, [IntPtr]::Zero)
                    if (-not $ok) {
                        $err = [System.Runtime.InteropServices.Marshal]::GetLastWin32Error()
                        Write-Log "  PostMessage Win32 err=$err"
                    }

                    $waited = 0
                    $detected = $false
                    while ($waited -lt $attemptWaitMs) {
                        Start-Sleep -Milliseconds $pollIntervalMs
                        $waited += $pollIntervalMs
                        $logSizeNow = 0
                        if (Test-Path $wmcmdLog) { $logSizeNow = (Get-Item $wmcmdLog).Length }
                        if ($logSizeNow -gt $logSizeBefore) {
                            Write-Log "  wmcmd.log grew $logSizeBefore -> $logSizeNow after ${waited} ms on attempt $attempt - addin loaded"
                            $loaded = $true
                            $detected = $true
                            break
                        }
                    }
                    if ($detected) { break }
                }

                if (-not $loaded) {
                    Write-Log "PostMessage path exhausted $maxAttempts attempts ($($maxAttempts * $attemptWaitMs / 1000) s) - falling through to monitoring + late-retry loop"
                }
            }
        } catch {
            Write-Log "PostMessage path threw: $($_.Exception.Message) - manual click needed"
        }
    } else {
        Write-Log "No saved AddinCmdId yet (HKCU\Software\EliteSoft\ErwinAddIn\Watcher\AddinCmdId missing)"
    }

    if (-not $loaded) {
        if ($savedCmdId -gt 0) {
            # We HAD a cmd id but all PostMessage attempts failed.
            # Usually means erwin's UI thread is still busy loading a
            # large model and dropped the queued messages. The late
            # retry loop in the monitoring phase below will keep trying.
            Write-Log "Addin not yet loaded; falling through to late-retry loop (model likely still mid-load)"
        } else {
            # No cmd id persisted yet -> first-ever install before any
            # manual click. Watcher cannot auto-load until
            # WmCommandLogger has captured + persisted the cmd id on
            # the user's first invocation.
            Write-Log "Addin NOT auto-loaded (no saved cmd id yet). User must click Tools > Add-Ins > Elite Soft Meta Addin manually once; WmCommandLogger persists the cmd id on the 2nd click and every subsequent session auto-loads."
        }
    }

    # --- Wait for THIS specific erwin to close (PID-based) ---
    # OR: if PostMessage path was attempted but addin didn't actually load
    # (e.g. command not enabled because title fired Test-ErwinHasModel too
    # early - false positive on transient Mart-only state), poll periodically
    # for the wmcmd.log to grow as a sign the user activated the addin via
    # menu OR for the title to become a real model state so we can retry
    # PostMessage. Otherwise the watcher just sits in this loop until erwin
    # closes, which is bad UX when the user opens a model 30 s later than
    # expected.
    Write-Log "Monitoring erwin PID=$targetPid..."
    $shouldRetryPostMessage = ($savedCmdId -gt 0 -and -not $loaded)
    $wmcmdLogPath = Join-Path $env:LOCALAPPDATA 'EliteSoft\ErwinAddIn\wmcmd.log'
    $logSizeAtMonitorStart = 0
    if (Test-Path $wmcmdLogPath) { $logSizeAtMonitorStart = (Get-Item $wmcmdLogPath).Length }

    while ($true) {
        Start-Sleep -Seconds 3
        $stillRunning = Get-Process -Id $targetPid -ErrorAction SilentlyContinue
        if (-not $stillRunning) {
            Write-Log "erwin PID=$targetPid closed - resuming watch"
            break
        }

        if (-not $shouldRetryPostMessage) { continue }

        # If addin loaded itself (user clicked manually, or post message
        # fired late from earlier attempt), wmcmd.log grew. Stop retrying.
        $logSizeNow = 0
        if (Test-Path $wmcmdLogPath) { $logSizeNow = (Get-Item $wmcmdLogPath).Length }
        if ($logSizeNow -gt $logSizeAtMonitorStart) {
            Write-Log "Addin loaded externally (wmcmd.log grew $logSizeAtMonitorStart -> $logSizeNow) - no more retries"
            $shouldRetryPostMessage = $false
            continue
        }

        # Re-check via the same combined detector Wait-ForModel uses
        # (title fast path + MDI hierarchy fallback) so the restored-MDI-
        # child case doesn't fall through to "still no model".
        $erwinNow = Get-Process -Id $targetPid -ErrorAction SilentlyContinue
        if (-not $erwinNow) { continue }
        if (-not (Test-ErwinHasModelComplete $erwinNow)) { continue }
        $newTitle = $erwinNow.MainWindowTitle

        Write-Log "Title now '$newTitle' looks real - retrying PostMessage WM_COMMAND id=$savedCmdId"
        $mainHwnd = $erwinNow.MainWindowHandle
        if ($mainHwnd -eq [IntPtr]::Zero) { continue }
        try {
            [void][WatcherWmPoster]::PostMessageW($mainHwnd, 0x0111, [IntPtr]$savedCmdId, [IntPtr]::Zero)
        } catch {
            Write-Log "Late retry PostMessage threw: $($_.Exception.Message)"
            continue
        }
        Start-Sleep -Seconds 2
        $logSizeNow = (Get-Item $wmcmdLogPath -ErrorAction SilentlyContinue).Length
        if ($logSizeNow -gt $logSizeAtMonitorStart) {
            Write-Log "  Late retry succeeded (wmcmd.log $logSizeAtMonitorStart -> $logSizeNow)"
            $shouldRetryPostMessage = $false
        } else {
            Write-Log "  Late retry still no growth; will poll again in 3 s"
        }
    }

    Start-Sleep -Seconds 2
  }
  catch {
    # Outer guard - swallowing here is intentional: alternative is silent script
    # death (which is exactly the bug we're fixing). Type+message logged so we
    # can still diagnose recurring failures.
    $errType = $_.Exception.GetType().Name
    $errMsg  = $_.Exception.Message
    Write-Log "Outer loop error: ${errType}: ${errMsg}"
    Start-Sleep -Seconds 2
  }
}
