using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Drives erwin's own native Mart Save flow from inside the addin
    /// without showing any UI to the user. Replaces the SCAPI
    /// pu.Save(martUri, "OVM=Yes") path that is rejected in-process by
    /// "Mart user interface is active" (memory
    /// reference_scapi_mart_ui_active_block).
    ///
    /// Sequence (verified 2026-05-31 via Ctrl+Alt+C + Ctrl+Alt+D recon
    /// during a manual Mart Save that successfully advanced v5 -> v6):
    ///
    ///   1. PostMessage WM_COMMAND(1061, 0) to erwin's XTPMainFrame.
    ///      1061 is the ribbon Mart Save command id (captured in
    ///      wmcmd.log across multiple sessions at 21:37, 01:30, 02:48 -
    ///      stable across erwin restarts). Same dispatch path as the
    ///      user's manual click, so the SCAPI "Mart UI active" check
    ///      does not apply.
    ///   2. erwin opens a #32770 dialog titled "Description for '&lt;model&gt;'
    ///      Version &lt;N&gt;". A SetWinEventHook on EVENT_OBJECT_CREATE
    ///      catches it before WM_PAINT and:
    ///        a. ShowWindow(SW_HIDE) - user never sees the dialog.
    ///        b. SetWindowText on the Edit child (control id 1081) =
    ///           the description text from ConfirmSubmitDialog.
    ///        c. PostMessage WM_COMMAND(IDOK = 1, 0) to the dialog -
    ///           IDOK is the Save button (recon dump 2026-05-31 02:48
    ///           confirmed cls='Button' txt='Save' id=1, native dialog
    ///           convention, no custom MFC cmd id needed).
    ///   3. erwin commits the dirty buffer to Mart as a new version
    ///      through MCXModelIncrementalSaveCommand::Write - same code
    ///      path as the manual click. The version description is the
    ///      string we just WM_SETTEXT'd.
    ///   4. Caller re-probes pu.IsDirty - False == commit succeeded.
    ///
    /// Bootstrap-free: cmd id 1061 is hardcoded based on the recon
    /// session. If erwin renumbers it in a future build, fall back to
    /// the existing WmCommandLogger 2-click capture pattern with a new
    /// HKCU value under Software\EliteSoft\ErwinAddIn\Watcher\MartSaveCmdId.
    /// </summary>
    internal static class MartSaveAutomation
    {
        // Discovered 2026-05-31 via Ctrl+Alt+C recon during a manual Mart
        // Save (wmcmd.log line 02:48:08.783). Same id appears in earlier
        // sessions (21:37 / 01:30) so it is stable across restarts.
        private const int RibbonMartSaveCmdId = 1061;

        // From the Ctrl+Alt+D dump of the "Description for ..." dialog at
        // 2026-05-31 02:48:18 (bridge log "[RECON] hwnd=... id=1081 cls='Edit'").
        private const int DescriptionEditCtrlId = 1081;

        // The dialog title prefix (variable suffix is "<model> Version <N>").
        private const string DescriptionDialogTitlePrefix = "Description for ";

        // Standard Win32 dialog class for the description popup. Verified
        // by the recon dump: cls='#32770'. Not erwin-custom.
        private const string DescriptionDialogClass = "#32770";

        // The dialog also has a "Don't show this again" checkbox (id=1743 in
        // the recon dump). User explicit rule 2026-05-31: NEVER touch this
        // checkbox - whatever state the user left it in must be preserved.
        // If the user has it checked, erwin will skip the dialog entirely
        // on subsequent ribbon Save clicks and commit directly with an
        // empty description; in that case our hook will time out (no
        // dialog to catch) and the caller's post-save dirty re-probe is
        // what tells us whether the commit actually happened.

        private const uint WM_COMMAND  = 0x0111;
        private const uint WM_SETTEXT  = 0x000C;
        private const int  SW_HIDE     = 0;
        private const int  IDOK        = 1;

        private const uint EVENT_OBJECT_CREATE     = 0x8000;
        private const uint EVENT_OBJECT_DESTROY    = 0x8001;
        private const uint EVENT_OBJECT_NAMECHANGE = 0x800C;
        // WINEVENT_OUTOFCONTEXT: callback fires on a system worker thread,
        // no DLL inject needed. We deliberately do NOT set
        // WINEVENT_SKIPOWNPROCESS (=0x0002): the addin is loaded INSIDE
        // erwin's process, so erwin's events ARE "own process" events
        // from our point of view. Setting SKIPOWNPROCESS = miss every
        // description dialog (verified 2026-05-31 first test - hook
        // never fired, dialog opened normally but our timeout hit).
        private const uint WINEVENT_OUTOFCONTEXT = 0x0000;

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, string lParam);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string lpszWindow);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern int GetDlgCtrlID(IntPtr hwndCtl);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDlgItem(IntPtr hDlg, int nIDDlgItem);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        private delegate void WinEventDelegate(
            IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
            int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(
            uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
            WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        /// <summary>
        /// Drive erwin's native Mart Save with the given description.
        /// Returns true if the dialog was caught + IDOK'd within the
        /// timeout. The caller verifies the actual commit by re-probing
        /// pu.IsDirty after this returns (not done here so this method
        /// stays pure UI automation).
        /// </summary>
        /// <param name="erwinMainHwnd">erwin's XTPMainFrame HWND from
        /// <c>Win32Helper.GetErwinMainWindow()</c>.</param>
        /// <param name="description">Version description string. Sent
        /// to the dialog's Edit child via SetWindowText. Will be the
        /// version description recorded by Mart commit.</param>
        /// <param name="timeoutMs">How long to wait for the description
        /// dialog to appear after posting the ribbon command. 15s is
        /// the manual flow's worst case (cold Mart connection + lazy
        /// SCAPI init); production normal is &lt;500ms.</param>
        /// <param name="log">Diagnostic logger.</param>
        public static Task<bool> SaveWithDescriptionAsync(
            IntPtr erwinMainHwnd,
            string description,
            int timeoutMs,
            Action<string> log)
        {
            // CANNOT use Task.Run here. WINEVENT_OUTOFCONTEXT callbacks
            // are queued by the OS to the message pump of the thread
            // that called SetWinEventHook. A thread-pool worker (what
            // Task.Run hands you) has NO message pump and is NOT STA,
            // so the callbacks accumulate in the queue and never
            // dispatch - hook fires zero times even though installed
            // (verified 2026-05-31 log: handle non-zero, total=0).
            //
            // Same constraint memory [reference_win32_subclass_from_net]
            // documents for WH_MOUSE_LL: needs STA thread + DoEvents.
            //
            // We spin up our own STA thread, set up the hooks there,
            // pump messages via Application.DoEvents while waiting,
            // and report the result through a TaskCompletionSource so
            // the rest of the addin's async code keeps working.
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var t = new Thread(() =>
            {
                try { tcs.SetResult(SaveWithDescriptionCore(erwinMainHwnd, description, timeoutMs, log)); }
                catch (Exception ex) { tcs.SetException(ex); }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Name = "MartSaveAutomation";
            t.Start();
            return tcs.Task;
        }

        // Pump pending Windows messages on the current STA thread so the
        // queued WINEVENT_OUTOFCONTEXT callbacks can actually run, while
        // still waking up promptly when the inner ManualResetEvent fires.
        // Polls in 50ms slices - the 50ms is small enough to feel
        // instant to the user yet large enough to keep CPU near zero.
        private static bool WaitWithMessagePump(ManualResetEventSlim mre, int timeoutMs)
        {
            int start = Environment.TickCount;
            while (true)
            {
                if (mre.Wait(50)) return true;
                int elapsed = Environment.TickCount - start;
                if (elapsed >= timeoutMs) return false;
                try { System.Windows.Forms.Application.DoEvents(); }
                catch { /* never break the wait loop on a pump exception */ }
            }
        }

        private static bool SaveWithDescriptionCore(
            IntPtr erwinMainHwnd,
            string description,
            int timeoutMs,
            Action<string> log)
        {
            Action<string> safeLog = log ?? (_ => { });

            if (erwinMainHwnd == IntPtr.Zero)
            {
                safeLog("MartSaveAutomation: erwin main HWND is zero - aborting.");
                return false;
            }

            // Snapshot the erwin process id - we use it both for filtering
            // WinEvent callbacks (only events inside erwin's process count)
            // and for the eventual ShowWindow / WM_SETTEXT path.
            uint erwinPid = 0;
            try { GetWindowThreadProcessId(erwinMainHwnd, out erwinPid); }
            catch (Exception ex) { safeLog($"MartSaveAutomation: GetWindowThreadProcessId failed: {ex.Message}"); }

            var dialogReady   = new ManualResetEventSlim(false);
            var dialogClosed  = new ManualResetEventSlim(false);
            IntPtr capturedDialog = IntPtr.Zero;
            int captureRc = 0;       // 1 = handled, -1 = handler exception, -2 = edit / IDOK miss
            string captureNote = "";

            // Diagnostics: WinEvent callback fire counts. Without these we
            // cannot tell "hook never installed" apart from "hook installed
            // but never fired" apart from "hook fired hundreds of times
            // but all filtered out (wrong class/title/process)". Logged
            // once at the end of the wait window.
            int evtTotal       = 0;  // every callback entry
            int evtAfterPid    = 0;  // survived process filter
            int evtAfterClass  = 0;  // survived class match
            int evtAfterTitle  = 0;  // survived title prefix match (= would have been handled if not raced)

            // The WinEvent hook callback can fire on ANY HWND in any
            // thread under EVENT_OBJECT_CREATE / NAMECHANGE. We filter by
            // (a) erwin process, (b) class == "#32770", (c) title prefix
            // "Description for ". Multiple events may fire for the same
            // dialog (CREATE + NAMECHANGE after the title is set) - guard
            // with a once-only Interlocked exchange on capturedDialog.
            int handled = 0;
            WinEventDelegate cb = (hHook, evt, hwnd, idObj, idChild, evtThread, evtTime) =>
            {
                Interlocked.Increment(ref evtTotal);
                try
                {
                    if (hwnd == IntPtr.Zero) return;
                    if (idObj != 0 /* OBJID_WINDOW */) return;
                    if (Interlocked.CompareExchange(ref handled, 0, 0) != 0) return;

                    if (erwinPid != 0)
                    {
                        GetWindowThreadProcessId(hwnd, out uint wndPid);
                        if (wndPid != erwinPid) return;
                    }
                    Interlocked.Increment(ref evtAfterPid);

                    var cls = new StringBuilder(64);
                    GetClassName(hwnd, cls, cls.Capacity);
                    if (cls.ToString() != DescriptionDialogClass) return;
                    Interlocked.Increment(ref evtAfterClass);

                    var title = new StringBuilder(256);
                    GetWindowText(hwnd, title, title.Capacity);
                    string t = title.ToString();
                    if (!t.StartsWith(DescriptionDialogTitlePrefix, StringComparison.Ordinal)) return;
                    Interlocked.Increment(ref evtAfterTitle);

                    // Race guard - first matching event wins, subsequent
                    // NAMECHANGE fires for the same dialog are ignored.
                    if (Interlocked.Exchange(ref handled, 1) != 0) return;

                    Interlocked.Exchange(ref capturedDialog, hwnd);
                    safeLog($"MartSaveAutomation: caught description dialog hwnd=0x{hwnd.ToInt64():X} title='{t}' (evt=0x{evt:X})");

                    // a. Hide before any paint pass. SW_HIDE flips the
                    //    WS_VISIBLE bit before the first WM_PAINT runs;
                    //    the user never sees the dialog flash.
                    ShowWindow(hwnd, SW_HIDE);

                    // b. Find the description Edit child (control id 1081).
                    //    GetDlgItem is the cheapest path - direct lookup
                    //    by control id, no enumeration walk.
                    IntPtr edit = GetDlgItem(hwnd, DescriptionEditCtrlId);
                    if (edit == IntPtr.Zero)
                    {
                        // Fallback: walk children for an Edit class. If
                        // erwin renumbers the control id we still catch
                        // the only Edit child the dialog has.
                        edit = FindWindowEx(hwnd, IntPtr.Zero, "Edit", null);
                    }
                    if (edit == IntPtr.Zero)
                    {
                        captureRc = -2;
                        captureNote = "no Edit child found inside description dialog";
                        safeLog($"MartSaveAutomation: {captureNote} - cannot set description.");
                        // Still post IDOK so the dialog does not hang the
                        // user's session; the commit happens with whatever
                        // default text erwin had in the Edit.
                    }
                    else
                    {
                        SendMessage(edit, WM_SETTEXT, IntPtr.Zero, description ?? string.Empty);
                        safeLog($"MartSaveAutomation: WM_SETTEXT'd Edit hwnd=0x{edit.ToInt64():X} with description ({(description ?? "").Length} chars)");
                    }

                    // c. PostMessage IDOK - Save button. erwin's dialog
                    //    proc runs the OK handler on the next message
                    //    pump cycle, calls MCXModelIncrementalSaveCommand,
                    //    commits to Mart, destroys the dialog.
                    PostMessage(hwnd, WM_COMMAND, (IntPtr)IDOK, IntPtr.Zero);
                    safeLog($"MartSaveAutomation: posted WM_COMMAND IDOK to dialog hwnd=0x{hwnd.ToInt64():X}");

                    if (captureRc == 0) captureRc = 1;
                }
                catch (Exception ex)
                {
                    captureRc = -1;
                    captureNote = $"{ex.GetType().Name}: {ex.Message}";
                    safeLog($"MartSaveAutomation: WinEvent callback threw {captureNote}");
                }
                finally
                {
                    // Signal that the create-side automation is done even
                    // on error - the outer Wait then proceeds and the
                    // dialog-destroy hook signals close.
                    dialogReady.Set();
                }
            };

            // Separate callback for EVENT_OBJECT_DESTROY so we know when
            // the dialog is actually gone (commit complete). Tied to the
            // captured HWND so we only signal on OUR dialog dying, not
            // every random window destruction in erwin's process.
            WinEventDelegate destroyCb = (hHook, evt, hwnd, idObj, idChild, evtThread, evtTime) =>
            {
                if (idObj != 0) return;
                IntPtr target = Interlocked.CompareExchange(ref capturedDialog, IntPtr.Zero, IntPtr.Zero);
                if (target == IntPtr.Zero || hwnd != target) return;
                dialogClosed.Set();
            };

            IntPtr createHook  = IntPtr.Zero;
            IntPtr destroyHook = IntPtr.Zero;
            // Pin the delegates so the GC cannot collect them while the
            // native WinEvent dispatcher still holds the function pointer.
            // A local variable would normally stay alive through the try /
            // finally scope, but OUTOFCONTEXT WinEvent fires on a system
            // worker thread - the JIT may aggressively reuse the closure's
            // stack slot once it has been referenced in SetWinEventHook,
            // and a callback fire mid-collection would jump to freed
            // memory and crash the process. GCHandle.Alloc with no
            // pinning is enough to keep the delegate (and therefore the
            // function pointer) alive until Free.
            GCHandle cbHandle        = GCHandle.Alloc(cb);
            GCHandle destroyCbHandle = GCHandle.Alloc(destroyCb);
            try
            {
                createHook = SetWinEventHook(
                    EVENT_OBJECT_CREATE, EVENT_OBJECT_NAMECHANGE,
                    IntPtr.Zero, cb, erwinPid, 0,
                    WINEVENT_OUTOFCONTEXT);
                destroyHook = SetWinEventHook(
                    EVENT_OBJECT_DESTROY, EVENT_OBJECT_DESTROY,
                    IntPtr.Zero, destroyCb, erwinPid, 0,
                    WINEVENT_OUTOFCONTEXT);

                safeLog($"MartSaveAutomation: SetWinEventHook(create) handle=0x{createHook.ToInt64():X}, SetWinEventHook(destroy) handle=0x{destroyHook.ToInt64():X}, idProcess={erwinPid}");

                if (createHook == IntPtr.Zero)
                {
                    safeLog("MartSaveAutomation: SetWinEventHook(create) returned NULL - aborting.");
                    return false;
                }

                // Post the ribbon Mart Save command to erwin's main window.
                // From here erwin opens the description dialog within ms,
                // our hook fires, hides + fills + IDOKs.
                bool posted = PostMessage(erwinMainHwnd, WM_COMMAND, (IntPtr)RibbonMartSaveCmdId, IntPtr.Zero);
                safeLog($"MartSaveAutomation: PostMessage WM_COMMAND({RibbonMartSaveCmdId}=ribbon Mart Save) to erwinMain=0x{erwinMainHwnd.ToInt64():X} => {posted}");
                if (!posted)
                {
                    safeLog("MartSaveAutomation: PostMessage returned false (queue full / invalid hwnd?) - aborting.");
                    return false;
                }

                // Wait for the dialog to appear + be handled. Use the
                // message-pump loop instead of a plain Wait so the
                // queued OUTOFCONTEXT WinEvent callbacks actually
                // dispatch on this STA thread (see SaveWithDescriptionAsync
                // header comment for the why).
                bool dialogSeen = WaitWithMessagePump(dialogReady, timeoutMs);
                safeLog($"MartSaveAutomation: hook event counters after wait: total={evtTotal}, afterPid={evtAfterPid}, afterClass={evtAfterClass}, afterTitle={evtAfterTitle}");

                if (!dialogSeen)
                {
                    // Two legitimate reasons the dialog does not appear:
                    //   1. User has the "Don't show this again" checkbox
                    //      checked in erwin's HKCU pref. erwin then skips
                    //      the dialog entirely and commits with an empty
                    //      description on its own. Per user rule
                    //      2026-05-31 we do NOT touch that pref, so we
                    //      MUST treat this case as "success path" and let
                    //      the caller's post-save dirty re-probe decide if
                    //      the commit actually happened.
                    //   2. The ribbon command did nothing (cmd id wrong /
                    //      Mart UI in some state where Save is disabled).
                    //      Same observable - no dialog. Same handoff to
                    //      the caller's dirty re-probe.
                    // Either way: return true here, let the caller's
                    // dirty probe be the source of truth. This is NOT a
                    // silent fallback - the dirty probe is a positive
                    // commit-verification signal (dirty -> True after =
                    // commit failed; dirty -> False after = commit OK).
                    safeLog($"MartSaveAutomation: description dialog did not appear within {timeoutMs}ms after ribbon Save - probably 'Don't show this again' is on (erwin commits silently with empty description). Returning true so caller can confirm via post-save dirty re-probe.");
                    return true;
                }

                if (captureRc < 0)
                {
                    safeLog($"MartSaveAutomation: dialog handler reported failure (rc={captureRc}, note='{captureNote}').");
                    return false;
                }

                // Wait for the dialog to actually close (= commit finished
                // and dialog DestroyWindow ran). Cap this separately - if
                // it never closes the commit either silently succeeded or
                // hung. Either way we want a timeout escape rather than a
                // wedged thread. Same message-pump dance as the Ready wait.
                if (!WaitWithMessagePump(dialogClosed, timeoutMs))
                {
                    safeLog($"MartSaveAutomation: dialog did not close within {timeoutMs}ms after IDOK - proceeding anyway, caller will re-probe dirty bit.");
                    // Not a hard failure - the IDOK may have queued and the
                    // commit may still be in progress when the caller probes.
                    return true;
                }

                safeLog("MartSaveAutomation: description dialog closed cleanly after IDOK - commit chain completed.");
                return true;
            }
            catch (Exception ex)
            {
                safeLog($"MartSaveAutomation: top-level threw {ex.GetType().Name}: {ex.Message}");
                return false;
            }
            finally
            {
                if (createHook  != IntPtr.Zero) try { UnhookWinEvent(createHook);  } catch (Exception ex) { safeLog($"MartSaveAutomation: UnhookWinEvent(create) threw: {ex.Message}"); }
                if (destroyHook != IntPtr.Zero) try { UnhookWinEvent(destroyHook); } catch (Exception ex) { safeLog($"MartSaveAutomation: UnhookWinEvent(destroy) threw: {ex.Message}"); }
                if (cbHandle.IsAllocated)        cbHandle.Free();
                if (destroyCbHandle.IsAllocated) destroyCbHandle.Free();

                // Defensive cleanup - if for any reason the dialog is still
                // hidden + alive (we caught it, hid it, but IDOK didn't
                // close it), re-show it so the user can rescue the session
                // instead of being stuck with an invisible modal. Caller
                // sees timeout / false rc and surfaces the message.
                IntPtr d = Interlocked.CompareExchange(ref capturedDialog, IntPtr.Zero, IntPtr.Zero);
                if (d != IntPtr.Zero && IsWindow(d))
                {
                    try
                    {
                        const int SW_SHOW = 5;
                        ShowWindow(d, SW_SHOW);
                        safeLog($"MartSaveAutomation: dialog still alive in finally - re-showed hwnd=0x{d.ToInt64():X} so it's not invisible-modal-stuck.");
                    }
                    catch (Exception ex) { safeLog($"MartSaveAutomation: re-show in finally threw: {ex.Message}"); }
                }
            }
        }
    }
}
