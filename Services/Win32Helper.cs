using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Win32 API helper for interacting with erwin DM windows.
    /// Used to scan menu structure and trigger menu commands (e.g. Complete Compare).
    /// </summary>
    public static class Win32Helper
    {
        #region P/Invoke

        [DllImport("user32.dll")]
        private static extern IntPtr GetMenu(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetSubMenu(IntPtr hMenu, int nPos);

        [DllImport("user32.dll")]
        private static extern int GetMenuItemCount(IntPtr hMenu);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetMenuString(IntPtr hMenu, uint uIDItem, StringBuilder lpString, int nMaxCount, uint uFlag);

        [DllImport("user32.dll")]
        private static extern uint GetMenuItemID(IntPtr hMenu, int nPos);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        /// <summary>Public wrapper. Returns true if the window was brought
        /// to the foreground; false if the foreground lock blocked it.</summary>
        public static bool SetForegroundWindowPublic(IntPtr hWnd) => SetForegroundWindow(hWnd);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr hWndParent, EnumChildProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        private delegate bool EnumChildProc(IntPtr hWnd, IntPtr lParam);
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        private const uint MF_BYPOSITION = 0x0400;
        private const uint WM_COMMAND = 0x0111;
        private const uint WM_CLOSE = 0x0010;

        #endregion

        public class MenuItemInfo
        {
            public string Path { get; set; }
            public string Text { get; set; }
            public uint Id { get; set; }
            public int Depth { get; set; }
        }

        public class UIElementInfo
        {
            public string Name { get; set; }
            public string ControlType { get; set; }
            public string AutomationId { get; set; }
            public string Path { get; set; }
            public int Depth { get; set; }
        }

        /// <summary>
        /// Find erwin's REAL main window handle (not our add-in form).
        /// Since our add-in runs inside erwin's process, Process.MainWindowHandle
        /// may return our own form. We need to find the window with "erwin DM" in its title.
        /// </summary>
        public static void GetWindowTextPublic(IntPtr hWnd, System.Text.StringBuilder sb, int maxCount)
        {
            GetWindowText(hWnd, sb, maxCount);
        }

        public static void GetClassNamePublic(IntPtr hWnd, System.Text.StringBuilder sb, int maxCount)
        {
            GetClassName(hWnd, sb, maxCount);
        }

        public static uint GetWindowThreadProcessIdPublic(IntPtr hWnd)
        {
            GetWindowThreadProcessId(hWnd, out uint pid);
            return pid;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        public static IntPtr GetForegroundWindowPublic() => GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr GetFocus();

        [DllImport("user32.dll")]
        private static extern IntPtr GetParent(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern uint GetCurrentThreadId();

        [StructLayout(LayoutKind.Sequential)]
        private struct GUITHREADINFO
        {
            public int cbSize;
            public int flags;
            public IntPtr hwndActive;
            public IntPtr hwndFocus;
            public IntPtr hwndCapture;
            public IntPtr hwndMenuOwner;
            public IntPtr hwndMoveSize;
            public IntPtr hwndCaret;
            public RECT rcCaret;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

        /// <summary>
        /// True when erwin's foreground UI thread has focus on a Win32 "Edit"
        /// class control - the in-place TreeView label editor (Model Explorer
        /// inline rename / new column) and the diagram canvas inline column
        /// editor both create a standard Edit control on top of the parent
        /// window while the user is typing. The class name check is enough to
        /// disambiguate edit-mode from committed state because erwin only spawns
        /// Edit controls for these transient inline-edit moments; Column Editor
        /// dialog hosts its own grid cell editors with different class names
        /// (XTPListBox / Static).
        ///
        /// Why this works (test 2026-05-13): Model Explorer "New Column" pops a
        /// Win32 Edit class window with text "default" selected; the moment the
        /// user hits Enter or Tab the Edit window is destroyed and the
        /// TreeView label flips to the committed display string ("_default_").
        /// We use a foreground-thread-focus probe (GetGUIThreadInfo on erwin's
        /// foreground thread) rather than GetFocus on the calling thread; the
        /// addin's WindowMonitorTimer fires on a different thread that has no
        /// own focused window, so a bare GetFocus would always return 0.
        /// </summary>
        public static bool IsInlineEditActive(IntPtr erwinHwnd)
        {
            if (erwinHwnd == IntPtr.Zero) return false;
            uint erwinThreadId = GetWindowThreadProcessId(erwinHwnd, out _);
            if (erwinThreadId == 0) return false;

            var gti = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
            if (!GetGUIThreadInfo(erwinThreadId, ref gti)) return false;
            if (gti.hwndFocus == IntPtr.Zero) return false;

            var sb = new StringBuilder(64);
            int len = GetClassName(gti.hwndFocus, sb, sb.Capacity);
            if (len <= 0) return false;

            // Standard Win32 TreeView label editor + diagram inline editor are
            // both plain "Edit" class. erwin's own grid cells use XTPGridControl
            // family class names (verified Phase-2D), so a strict "Edit" match
            // does not collide with the Column Editor edit-grid.
            return string.Equals(sb.ToString(), "Edit", StringComparison.Ordinal);
        }

        [DllImport("user32.dll")]
        private static extern bool IsWindowEnabled(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool EnableWindow(IntPtr hWnd, bool bEnable);

        /// <summary>
        /// Re-enables a window that WinForms.Form.ShowDialog disabled when
        /// it opened a modal child. WinForms walks every top-level window
        /// owned by the process and EnableWindow(false) on each so the user
        /// can only interact with the modal. erwin's main frame is one such
        /// top-level (in-proc COM addin shares the process), so a modal
        /// popup that wants the user to drive erwin's own UI (Mart Save
        /// toolbar -> Description dialog) must explicitly re-enable it.
        /// Returns the previous enabled state so the caller can restore.
        /// </summary>
        public static bool EnableWindowPublic(IntPtr hWnd, bool enable)
        {
            if (hWnd == IntPtr.Zero) return false;
            bool wasEnabled = IsWindowEnabled(hWnd);
            EnableWindow(hWnd, enable);
            return wasEnabled;
        }

        /// <summary>
        /// Returns true when erwin's main window is currently disabled by a
        /// modal child dialog (Mart Save, Mart Open, Print, Properties, ...).
        /// Driving COM/SCAPI/NativeBridge actions while a modal is open is a
        /// known crash trigger on r10.10: the addin's keystroke routes
        /// (Ctrl+Alt+T) misroute to the modal dialog, and queued WinForms
        /// accessibility events flush against erwin's broken UIA proxy when
        /// the work returns - 0xC0000005 in coreclr.dll. Verified by the
        /// 17:05:29 crash dump on 2026-05-07. Caller is expected to short
        /// circuit any heavy work and surface a "close the dialog first"
        /// warning instead.
        /// </summary>
        public static bool IsErwinMainWindowBlockedByModal()
        {
            IntPtr main = GetErwinMainWindow();
            if (main == IntPtr.Zero) return false; // Can't determine; let caller proceed.
            return !IsWindowEnabled(main);
        }

        public static IntPtr GetErwinMainWindow()
        {
            IntPtr result = IntPtr.Zero;
            var erwinProcesses = Process.GetProcessesByName("erwin");
            if (erwinProcesses.Length == 0) return IntPtr.Zero;

            var erwinPids = new HashSet<uint>();
            foreach (var p in erwinProcesses)
                erwinPids.Add((uint)p.Id);

            EnumWindows((hWnd, lParam) =>
            {
                if (!IsWindowVisible(hWnd)) return true;

                GetWindowThreadProcessId(hWnd, out uint pid);
                if (!erwinPids.Contains(pid)) return true;

                // Timeout-bounded read: after a version-compare teardown a leftover
                // visible #32770 dialog can be parked on a non-pumping worker thread
                // (stuck on a Mart socket). A plain GetWindowText (synchronous
                // WM_GETTEXT) to it blocks this enum - and thus the UI-thread caller
                // (monitor heartbeat / reconnect modal-check) - forever (hang dump
                // 2026-06-03, frozen on HWND 0xC304DE, a #32770 on a stuck thread).
                // SMTO_ABORTIFHUNG returns "" for such a window so the enum skips it
                // and still reaches erwin's real "erwin DM" main frame.
                string title = GetWindowTextNoHang(hWnd);

                // erwin's main window title starts with "erwin DM"
                if (title.IndexOf("erwin DM", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    result = hWnd;
                    return false; // Stop enumeration
                }

                return true;
            }, IntPtr.Zero);

            return result;
        }

        /// <summary>
        /// Returns the HWND of erwin's ACTIVE MDI child (the model tab the user is
        /// currently looking at), or <see cref="IntPtr.Zero"/> when erwin is not using
        /// a standard MFC/XTP MDI frame or no child is active. The active MDI child is
        /// the ground truth for which model is in front: a modal dialog is not an MDI
        /// child, so (unlike the main-frame title) it cannot mask the real active
        /// model. Timeout-bounded (SMTO_ABORTIFHUNG) so a hung erwin frame returns Zero
        /// instead of blocking the caller, matching <see cref="GetWindowTextNoHang"/>.
        /// </summary>
        public static IntPtr GetActiveMdiChild(IntPtr erwinMain, int timeoutMs = 200)
        {
            if (erwinMain == IntPtr.Zero) return IntPtr.Zero;

            // erwin hosts its model windows in a standard "MDIClient" child of the
            // main frame (same lookup MartMartAutomation uses to drive the MDI).
            IntPtr mdiClient = IntPtr.Zero;
            EnumChildWindows(erwinMain, (h, _) =>
            {
                var cls = new StringBuilder(64);
                GetClassName(h, cls, cls.Capacity);
                if (cls.ToString() == "MDIClient") { mdiClient = h; return false; }
                return true;
            }, IntPtr.Zero);
            if (mdiClient == IntPtr.Zero) return IntPtr.Zero;

            // WM_MDIGETACTIVE returns the active child HWND as the message result.
            const uint WM_MDIGETACTIVE = 0x0229;
            const uint SMTO_ABORTIFHUNG = 0x0002;
            if (SendMessageTimeout(mdiClient, WM_MDIGETACTIVE, IntPtr.Zero, IntPtr.Zero,
                    SMTO_ABORTIFHUNG, (uint)timeoutMs, out IntPtr active) == IntPtr.Zero)
                return IntPtr.Zero; // timed out / hung frame
            return active;
        }

        /// <summary>
        /// Enumerate ALL visible top-level windows belonging to the erwin process.
        /// Useful for debugging which windows exist.
        /// </summary>
        public static List<string> EnumErwinWindows()
        {
            var results = new List<string>();
            var erwinProcesses = Process.GetProcessesByName("erwin");
            if (erwinProcesses.Length == 0) return results;

            var erwinPids = new HashSet<uint>();
            foreach (var p in erwinProcesses)
                erwinPids.Add((uint)p.Id);

            EnumWindows((hWnd, lParam) =>
            {
                if (!IsWindowVisible(hWnd)) return true;

                GetWindowThreadProcessId(hWnd, out uint pid);
                if (!erwinPids.Contains(pid)) return true;

                var titleSb = new StringBuilder(512);
                var classSb = new StringBuilder(256);
                GetWindowText(hWnd, titleSb, titleSb.Capacity);
                GetClassName(hWnd, classSb, classSb.Capacity);

                string title = titleSb.ToString();
                string cls = classSb.ToString();

                IntPtr hMenu = GetMenu(hWnd);
                string menuInfo = hMenu != IntPtr.Zero ? $" [HAS MENU]" : "";

                results.Add($"HWND={hWnd}, Class='{cls}', Title='{title}'{menuInfo}");
                return true;
            }, IntPtr.Zero);

            return results;
        }

        #region Win32 Menu Scan

        /// <summary>
        /// Scan standard Win32 menu structure (HMENU-based).
        /// </summary>
        public static List<MenuItemInfo> ScanMenuStructure(IntPtr hWnd)
        {
            var results = new List<MenuItemInfo>();
            IntPtr hMenu = GetMenu(hWnd);
            if (hMenu == IntPtr.Zero) return results;

            ScanMenuRecursive(hMenu, "", 0, results);
            return results;
        }

        private static void ScanMenuRecursive(IntPtr hMenu, string parentPath, int depth, List<MenuItemInfo> results)
        {
            int count = GetMenuItemCount(hMenu);
            if (count <= 0) return;

            for (int i = 0; i < count; i++)
            {
                var sb = new StringBuilder(256);
                GetMenuString(hMenu, (uint)i, sb, sb.Capacity, MF_BYPOSITION);
                string text = sb.ToString().Replace("&", "");

                if (string.IsNullOrEmpty(text)) continue;

                uint id = GetMenuItemID(hMenu, i);
                string path = string.IsNullOrEmpty(parentPath) ? text : $"{parentPath} > {text}";

                results.Add(new MenuItemInfo
                {
                    Path = path,
                    Text = text,
                    Id = id,
                    Depth = depth
                });

                IntPtr hSubMenu = GetSubMenu(hMenu, i);
                if (hSubMenu != IntPtr.Zero)
                {
                    ScanMenuRecursive(hSubMenu, path, depth + 1, results);
                }
            }
        }

        #endregion

        #region UI Automation Scan

        /// <summary>
        /// Scan UI elements using Windows UI Automation.
        /// Works with any menu type (MFC, ribbon, custom, etc.)
        /// </summary>
        public static List<UIElementInfo> ScanUIAutomation(IntPtr hWnd, Action<string> log = null)
        {
            var results = new List<UIElementInfo>();

            try
            {
                var element = AutomationElement.FromHandle(hWnd);
                if (element == null) return results;

                // First scan: look for menu bars and menu items
                log?.Invoke("[UI AUTO] Scanning for MenuBar and MenuItem elements...");
                ScanForMenuItems(element, "", 0, results, log);

                // If no menu items found, do a broader scan for toolbar buttons
                if (results.Count == 0)
                {
                    log?.Invoke("[UI AUTO] No menu items found, scanning ToolBar elements...");
                    ScanForToolbarItems(element, "", 0, results, log);
                }
            }
            catch (Exception ex)
            {
                log?.Invoke($"[UI AUTO] Error: {ex.Message}");
            }

            return results;
        }

        private static void ScanForMenuItems(AutomationElement parent, string parentPath, int depth, List<UIElementInfo> results, Action<string> log)
        {
            if (depth > 6) return; // Prevent infinite recursion

            try
            {
                // Find MenuBar, Menu, MenuItem, and ToolBar elements
                var condition = new OrCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuBar),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Menu),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem)
                );

                var elements = parent.FindAll(TreeScope.Children, condition);

                foreach (AutomationElement el in elements)
                {
                    try
                    {
                        string name = el.Current.Name ?? "";
                        string controlType = el.Current.ControlType?.ProgrammaticName ?? "";
                        string autoId = el.Current.AutomationId ?? "";
                        string path = string.IsNullOrEmpty(parentPath) ? name : $"{parentPath} > {name}";

                        if (!string.IsNullOrEmpty(name))
                        {
                            results.Add(new UIElementInfo
                            {
                                Name = name,
                                ControlType = controlType,
                                AutomationId = autoId,
                                Path = path,
                                Depth = depth
                            });
                        }

                        // Recurse into children
                        ScanForMenuItems(el, path, depth + 1, results, log);
                    }
                    catch { }
                }
            }
            catch { }
        }

        private static void ScanForToolbarItems(AutomationElement parent, string parentPath, int depth, List<UIElementInfo> results, Action<string> log)
        {
            if (depth > 4) return;

            try
            {
                // Find ToolBar and Button elements
                var condition = new OrCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ToolBar),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.SplitButton),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Tab),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Pane)
                );

                var elements = parent.FindAll(TreeScope.Children, condition);

                foreach (AutomationElement el in elements)
                {
                    try
                    {
                        string name = el.Current.Name ?? "";
                        string controlType = el.Current.ControlType?.ProgrammaticName ?? "";
                        string autoId = el.Current.AutomationId ?? "";
                        string path = string.IsNullOrEmpty(parentPath) ? name : $"{parentPath} > {name}";

                        if (!string.IsNullOrEmpty(name))
                        {
                            results.Add(new UIElementInfo
                            {
                                Name = name,
                                ControlType = controlType,
                                AutomationId = autoId,
                                Path = path,
                                Depth = depth
                            });
                        }

                        ScanForToolbarItems(el, path, depth + 1, results, log);
                    }
                    catch { }
                }
            }
            catch { }
        }

        /// <summary>
        /// Find and invoke a button by name using UI Automation.
        /// For XTP Ribbon: switches ribbon tabs to find hidden buttons.
        /// </summary>
        public static bool InvokeMenuItemByName(IntPtr hWnd, string searchText, Action<string> log = null)
        {
            try
            {
                SetForegroundWindow(hWnd);
                System.Threading.Thread.Sleep(200);

                var root = AutomationElement.FromHandle(hWnd);
                if (root == null) return false;

                // 1. Direct search in current UI state
                log?.Invoke($"[UI AUTO] Searching for '{searchText}' in current state...");
                if (FindAndInvokeButton(root, searchText, log))
                    return true;

                // 2. Not found - try switching ribbon tabs (Tools, Actions, etc.)
                log?.Invoke("[UI AUTO] Not found in current state, switching ribbon tabs...");
                return TrySwitchRibbonTabsAndFind(root, searchText, log);
            }
            catch (Exception ex)
            {
                log?.Invoke($"[UI AUTO] InvokeMenuItemByName error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Invoke a toolbar button by name (e.g. "Review" on the Mart toolbar).
        /// Does NOT switch ribbon tabs - just finds it in current state.
        /// </summary>
        public static bool InvokeToolbarButton(IntPtr hWnd, string buttonName, Action<string> log = null)
        {
            try
            {
                SetForegroundWindow(hWnd);
                System.Threading.Thread.Sleep(200);

                var root = AutomationElement.FromHandle(hWnd);
                if (root == null) return false;

                return FindAndInvokeButton(root, buttonName, log);
            }
            catch (Exception ex)
            {
                log?.Invoke($"[UI AUTO] InvokeToolbarButton error: {ex.Message}");
                return false;
            }
        }

        private static bool FindAndInvokeButton(AutomationElement parent, string searchText, Action<string> log, int depth = 0)
        {
            if (depth > 10) return false;

            try
            {
                var children = parent.FindAll(TreeScope.Children, Condition.TrueCondition);
                foreach (AutomationElement child in children)
                {
                    try
                    {
                        string name = child.Current.Name ?? "";
                        var ctrlType = child.Current.ControlType;

                        if (name.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0
                            && ctrlType == ControlType.Button)
                        {
                            log?.Invoke($"[UI AUTO] Found button: '{name}'");

                            if (child.TryGetCurrentPattern(InvokePattern.Pattern, out object pattern))
                            {
                                ((InvokePattern)pattern).Invoke();
                                log?.Invoke($"[UI AUTO] Invoked '{name}' successfully!");
                                return true;
                            }
                        }

                        if (FindAndInvokeButton(child, searchText, log, depth + 1))
                            return true;
                    }
                    catch { }
                }
            }
            catch { }

            return false;
        }

        private static bool TrySwitchRibbonTabsAndFind(AutomationElement root, string searchText, Action<string> log)
        {
            try
            {
                // Find all TabItem elements (ribbon tabs)
                var tabItems = root.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem));

                // Ribbon tabs to try: Tools, Actions, Home, Model, Mart
                string[] tabPriority = { "Tools", "Actions", "Home", "Model", "Mart", "View", "Diagram" };

                foreach (string tabName in tabPriority)
                {
                    foreach (AutomationElement tab in tabItems)
                    {
                        try
                        {
                            string name = tab.Current.Name ?? "";
                            if (!string.Equals(name, tabName, StringComparison.OrdinalIgnoreCase))
                                continue;

                            log?.Invoke($"[UI AUTO] Switching to ribbon tab '{name}'...");

                            // Select the tab
                            if (tab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object selPattern))
                            {
                                ((SelectionItemPattern)selPattern).Select();
                                System.Threading.Thread.Sleep(500); // Wait for ribbon content to load

                                // Re-scan from root for the button
                                if (FindAndInvokeButton(root, searchText, log))
                                    return true;

                                // Also scan new buttons that appeared
                                log?.Invoke($"[UI AUTO] '{searchText}' not found in '{name}' tab, scanning new buttons...");
                                LogCurrentButtons(root, name, log);
                            }
                            else if (tab.TryGetCurrentPattern(InvokePattern.Pattern, out object invPattern))
                            {
                                ((InvokePattern)invPattern).Invoke();
                                System.Threading.Thread.Sleep(500);

                                if (FindAndInvokeButton(root, searchText, log))
                                    return true;

                                LogCurrentButtons(root, name, log);
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                log?.Invoke($"[UI AUTO] TrySwitchRibbonTabs error: {ex.Message}");
            }

            return false;
        }

        /// <summary>
        /// Log all button names visible in the current ribbon state (for debugging).
        /// </summary>
        private static void LogCurrentButtons(AutomationElement root, string tabName, Action<string> log)
        {
            try
            {
                var buttons = root.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));

                var names = new List<string>();
                foreach (AutomationElement btn in buttons)
                {
                    try
                    {
                        string name = btn.Current.Name ?? "";
                        if (!string.IsNullOrEmpty(name) && name.Length > 1)
                            names.Add(name);
                    }
                    catch { }
                }

                log?.Invoke($"[UI AUTO] Buttons in '{tabName}' tab: {string.Join(", ", names)}");
            }
            catch { }
        }

        #endregion

        #region Child Window Scan

        /// <summary>
        /// Enumerate child windows that have Win32 menus and scan those menus.
        /// This is critical for MFC MDI apps like erwin where the menu lives
        /// on the MDI child frame, not the main frame.
        /// </summary>
        public static List<string> ScanChildWindowMenus(IntPtr hWnd)
        {
            var results = new List<string>();
            var scannedMenus = new HashSet<IntPtr>();

            EnumChildWindows(hWnd, (childHwnd, lParam) =>
            {
                IntPtr childMenu = GetMenu(childHwnd);
                if (childMenu == IntPtr.Zero) return true;

                // Skip tiny values (likely control IDs, not real HMENU handles)
                // Real HMENU handles are typically > 0x10000
                if ((long)childMenu < 0x10000) return true;

                // Skip already scanned menus
                if (scannedMenus.Contains(childMenu)) return true;
                scannedMenus.Add(childMenu);

                var classSb = new StringBuilder(256);
                var textSb = new StringBuilder(256);
                GetClassName(childHwnd, classSb, classSb.Capacity);
                GetWindowText(childHwnd, textSb, textSb.Capacity);

                string cls = classSb.ToString();
                string txt = textSb.ToString();

                // Check if this menu has actual items
                int menuCount = GetMenuItemCount(childMenu);
                if (menuCount <= 0) return true;

                results.Add($"[CHILD MENU] HWND={childHwnd}, Class='{cls}', Text='{txt}', HMENU={childMenu}, Items={menuCount}");

                // Scan this menu recursively
                var menuItems = new List<MenuItemInfo>();
                ScanMenuRecursive(childMenu, "", 0, menuItems);

                foreach (var item in menuItems)
                {
                    string indent = new string(' ', item.Depth * 2);
                    string idStr = item.Id == 0xFFFFFFFF ? "(submenu)" : $"ID={item.Id}";
                    results.Add($"  [MENU] {indent}{item.Text} [{idStr}]");
                }

                // Highlight Compare/Review items
                var compareItems = menuItems.FindAll(i =>
                    (i.Text.IndexOf("Compare", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     i.Text.IndexOf("Review", StringComparison.OrdinalIgnoreCase) >= 0) &&
                    i.Id != 0xFFFFFFFF);

                foreach (var ci in compareItems)
                {
                    results.Add($"  >>> FOUND: {ci.Path} [ID={ci.Id}]");
                }

                return true;
            }, IntPtr.Zero);

            if (results.Count == 0)
                results.Add("[CHILD MENU] No child windows with real menus found.");

            return results;
        }

        /// <summary>
        /// Find a menu command in any child window's menu by text.
        /// Returns the menu item info and the HWND to send WM_COMMAND to.
        /// </summary>
        public static (MenuItemInfo item, IntPtr targetHwnd) FindChildMenuItemByText(IntPtr hWnd, string searchText)
        {
            MenuItemInfo foundItem = null;
            IntPtr foundHwnd = IntPtr.Zero;

            EnumChildWindows(hWnd, (childHwnd, lParam) =>
            {
                IntPtr childMenu = GetMenu(childHwnd);
                if (childMenu == IntPtr.Zero || (long)childMenu < 0x10000) return true;

                var menuItems = new List<MenuItemInfo>();
                ScanMenuRecursive(childMenu, "", 0, menuItems);

                foreach (var item in menuItems)
                {
                    if (item.Text.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0
                        && item.Id != 0xFFFFFFFF)
                    {
                        foundItem = item;
                        foundHwnd = childHwnd;
                        return false; // Stop enumeration
                    }
                }

                return true;
            }, IntPtr.Zero);

            return (foundItem, foundHwnd);
        }

        #endregion

        #region IAccessible

        [DllImport("oleacc.dll")]
        private static extern int AccessibleObjectFromWindow(IntPtr hwnd, uint dwId, ref Guid riid, out dynamic ppvObject);

        private static readonly Guid IID_IAccessible = new Guid("618736E0-3C3D-11CF-810C-00AA00389B71");

        public static dynamic GetAccessibleObject(IntPtr hWnd)
        {
            Guid guid = IID_IAccessible;
            int hr = AccessibleObjectFromWindow(hWnd, 0, ref guid, out dynamic acc);
            return hr == 0 ? acc : null;
        }

        public static void EnumChildWindowsByClass(IntPtr hWndParent, string className, List<IntPtr> results)
        {
            EnumChildWindows(hWndParent, (hWnd, lParam) =>
            {
                var sb = new StringBuilder(256);
                GetClassName(hWnd, sb, sb.Capacity);
                if (sb.ToString().Contains(className))
                    results.Add(hWnd);
                return true;
            }, IntPtr.Zero);
        }

        /// <summary>
        /// Information about a child window.
        /// </summary>
        public class ChildWindowInfo
        {
            public IntPtr Handle { get; set; }
            public string ClassName { get; set; }
            public string Text { get; set; }
        }

        /// <summary>
        /// Enumerates ALL child windows recursively with their class names and text.
        /// </summary>
        public static List<ChildWindowInfo> EnumAllChildWindows(IntPtr hWndParent)
        {
            var results = new List<ChildWindowInfo>();
            EnumChildWindows(hWndParent, (hWnd, lParam) =>
            {
                var sbClass = new StringBuilder(256);
                GetClassName(hWnd, sbClass, sbClass.Capacity);
                results.Add(new ChildWindowInfo
                {
                    Handle = hWnd,
                    ClassName = sbClass.ToString(),
                    // Timeout-bounded per-child read: a single hung child would
                    // otherwise block the whole enumeration - and the UI thread -
                    // on a synchronous WM_GETTEXT (hang class 2026-06-03).
                    Text = GetWindowTextNoHang(hWnd)
                });
                return true;
            }, IntPtr.Zero);
            return results;
        }

        /// <summary>
        /// Finds child windows by class name (exact contains match).
        /// </summary>
        public static List<IntPtr> FindChildWindowsByClass(IntPtr hWndParent, string className)
        {
            var results = new List<IntPtr>();
            EnumChildWindowsByClass(hWndParent, className, results);
            return results;
        }

        /// <summary>
        /// Gets window text safely.
        /// </summary>
        public static string GetWindowTextSafe(IntPtr hWnd)
        {
            var sb = new StringBuilder(512);
            GetWindowText(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }

        // Non-hanging window-title read for UI-thread MONITORING timers.
        // GetWindowText sends WM_GETTEXT synchronously; if hWnd belongs to a hung /
        // non-pumping thread (e.g. a leftover pipeline window) it blocks the calling
        // (UI) thread forever - confirmed via hang dump 2026-06-03 (OnWindowMonitorTick
        // -> IsColumnEditorOpen -> EnumWindows -> GetWindowText -> NtUserMessageCall,
        // whole app frozen). SendMessageTimeout with SMTO_ABORTIFHUNG returns "" instead
        // of hanging. Use this ONLY in the persistent UI-thread monitor scans.
        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, StringBuilder lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

        // IntPtr-lParam overload for fixed-size messages (e.g. SB_GETTEXTLENGTH)
        // that return their result via lpdwResult rather than a text buffer.
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

        public static string GetWindowTextNoHang(IntPtr hWnd, int timeoutMs = 100)
        {
            var sb = new StringBuilder(512);
            // 0x000D = WM_GETTEXT, 0x0002 = SMTO_ABORTIFHUNG.
            IntPtr ok = SendMessageTimeout(hWnd, 0x000D, (IntPtr)512, sb, 0x0002, (uint)timeoutMs, out _);
            return ok == IntPtr.Zero ? "" : sb.ToString();
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, StringBuilder lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        private const uint SB_GETTEXT = 0x040D;
        private const uint SB_GETTEXTLENGTH = 0x040C;
        private const uint SB_GETPARTS = 0x0406;

        /// <summary>
        /// Reads text from a specific part of a Win32 status bar control.
        /// </summary>
        public static string GetStatusBarText(IntPtr hWndStatusBar, int partIndex)
        {
            try
            {
                // Timeout-bounded (SMTO_ABORTIFHUNG): erwin's status bar is owned by
                // the main UI thread; reading it from a hung state would otherwise
                // block on a synchronous SB_GETTEXT (hang class 2026-06-03).
                if (SendMessageTimeout(hWndStatusBar, SB_GETTEXTLENGTH, (IntPtr)partIndex, IntPtr.Zero, 0x0002, 100, out IntPtr lenRes) == IntPtr.Zero)
                    return "";
                int len = (int)lenRes & 0xFFFF;
                if (len == 0) return "";
                var sb = new StringBuilder(len + 1);
                if (SendMessageTimeout(hWndStatusBar, SB_GETTEXT, (IntPtr)partIndex, sb, 0x0002, 100, out _) == IntPtr.Zero)
                    return "";
                return sb.ToString();
            }
            catch { return ""; }
        }

        #endregion

        #region Helpers

        public static MenuItemInfo FindMenuItemByText(IntPtr hWnd, string searchText)
        {
            var items = ScanMenuStructure(hWnd);
            foreach (var item in items)
            {
                if (item.Text.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0 && item.Id != 0xFFFFFFFF)
                    return item;
            }
            return null;
        }

        public static bool TriggerMenuCommand(IntPtr hWnd, uint menuId)
        {
            if (hWnd == IntPtr.Zero || menuId == 0 || menuId == 0xFFFFFFFF)
                return false;

            SetForegroundWindow(hWnd);
            return PostMessage(hWnd, WM_COMMAND, (IntPtr)menuId, IntPtr.Zero);
        }

        /// <summary>
        /// Initiates a graceful shutdown of the erwin host by posting WM_CLOSE
        /// to its main XTPMainFrame window. This drives erwin's NORMAL exit path,
        /// so if any open model has unsaved changes erwin raises its own
        /// "Save changes?" prompt before terminating - no data is lost. We never
        /// Process.Kill erwin: a hard kill would discard the dirty buffer and has
        /// historically corrupted Mart sessions / triggered teardown crashes.
        /// Brought to the foreground first so erwin's save prompt (if any) is
        /// not stranded behind other windows.
        /// </summary>
        /// <returns>
        /// false if erwin's main window could not be resolved (not running);
        /// true once WM_CLOSE has been posted. Note: a true return only means the
        /// message was delivered - the user may still cancel at erwin's save prompt.
        /// </returns>
        public static bool CloseErwinMainWindow()
        {
            IntPtr hWnd = GetErwinMainWindow();
            if (hWnd == IntPtr.Zero)
                return false;

            SetForegroundWindow(hWnd);
            return PostMessage(hWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }

        #endregion

        #region Diagram Selection Detection

        /// <summary>
        /// Reads the currently selected entity name(s) from erwin's Overview pane.
        /// When an entity is selected in the diagram, the Overview pane shows:
        /// "MODELNAME (ENTITY_NAME)" for single selection.
        /// Returns empty list if nothing is selected.
        /// </summary>
        public static List<string> GetDiagramSelectedEntities(IntPtr erwinHwnd, string modelName)
        {
            var result = new List<string>();
            if (erwinHwnd == IntPtr.Zero || string.IsNullOrEmpty(modelName))
                return result;

            // Enumerate all Static child windows looking for the model info display
            // Pattern: "MODELNAME (ENTITY_NAME)" when an entity is selected
            string modelUpper = modelName.ToUpperInvariant();
            var childWindows = EnumAllChildWindows(erwinHwnd);

            foreach (var cw in childWindows)
            {
                if (cw.ClassName != "Static" || string.IsNullOrEmpty(cw.Text))
                    continue;

                string text = cw.Text.Trim();

                // Check if this Static window starts with the model name
                if (!text.StartsWith(modelUpper, StringComparison.OrdinalIgnoreCase))
                    continue;

                // If text has parentheses, extract the entity name
                int parenStart = text.IndexOf('(');
                int parenEnd = text.LastIndexOf(')');
                if (parenStart > 0 && parenEnd > parenStart)
                {
                    string inside = text.Substring(parenStart + 1, parenEnd - parenStart - 1).Trim();
                    if (!string.IsNullOrEmpty(inside))
                    {
                        // Could be comma-separated for multi-select (needs testing)
                        // For now, handle single entity and "N objects" format
                        if (inside.EndsWith("objects", StringComparison.OrdinalIgnoreCase) ||
                            inside.EndsWith("object", StringComparison.OrdinalIgnoreCase))
                        {
                            // Multi-select shows count only - cannot determine individual names
                            // Return empty to indicate "multiple selected but unknown"
                            return result;
                        }

                        // Split by comma in case of multi-select list format
                        var parts = inside.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (var part in parts)
                        {
                            string trimmed = part.Trim();
                            if (!string.IsNullOrEmpty(trimmed))
                                result.Add(trimmed);
                        }
                    }
                }
                break; // Found the model info window
            }

            return result;
        }

        #endregion
    }
}
