#nullable enable
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace EliteSoft.Erwin.AddIn.Forms
{
    /// <summary>
    /// Lightweight transient notification used for "we just did something
    /// silently" feedback - currently AutoApply prefix/suffix fixes (a
    /// naming rule sneakily appended `_DATE` to a column name and the user
    /// deserves to see what happened without an interruptive popup).
    /// <para>
    /// Visual: small borderless white panel with a primary-blue accent
    /// strip on the left, anchored to the bottom-right corner of the
    /// add-in's active screen. Stacks vertically when multiple toasts
    /// are alive. Auto-dismisses after <see cref="DefaultLifetimeMs"/>;
    /// the user can also click X to close.
    /// </para>
    /// <para>
    /// Why a custom toast instead of <see cref="NotifyIcon"/>: the
    /// system tray notification is too disconnected from the add-in UI
    /// and ignores the "addin's own screen" multi-monitor rule we
    /// already apply to every other dialog.
    /// </para>
    /// </summary>
    public sealed class ToastNotification : Form
    {
        private const int DefaultLifetimeMs = 5000;
        private const int ToastWidth = 360;
        private const int ToastHeight = 56;
        private const int ScreenEdgeMargin = 16;
        private const int AccentStripWidth = 4;
        private const int CloseButtonSize = 24;

        private static readonly Color ClrPrimary = Color.FromArgb(0, 102, 204);
        private static readonly Color ClrTextPrimary = Color.FromArgb(26, 26, 26);
        private static readonly Color ClrTextSecondary = Color.FromArgb(102, 102, 102);
        private static readonly Color ClrBorder = Color.FromArgb(208, 208, 208);
        private static readonly Color ClrCloseHover = Color.FromArgb(232, 17, 35);

        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

        // Live-toast registry (per-process). New toasts are stacked above
        // existing ones at the bottom-right of the addin's screen, so the
        // most recent fix sits closest to the user's eye and older ones
        // age out upward.
        private static readonly List<ToastNotification> _liveToasts = new();
        private static readonly object _liveLock = new();

        private readonly Timer _lifetimeTimer;

        private ToastNotification(string title, string body)
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            BackColor = Color.White;
            Font = new Font("Segoe UI", 9F);
            TopMost = true;
            Size = new Size(ToastWidth, ToastHeight);

            Paint += (_, e) =>
            {
                using var pen = new Pen(ClrBorder, 1);
                e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            };

            var accent = new Panel
            {
                Dock = DockStyle.Left,
                Width = AccentStripWidth,
                BackColor = ClrPrimary,
            };

            var lblTitle = new Label
            {
                Text = title ?? "",
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                ForeColor = ClrTextPrimary,
                AutoSize = false,
                Location = new Point(AccentStripWidth + 10, 8),
                Size = new Size(ToastWidth - AccentStripWidth - CloseButtonSize - 20, 18),
                TextAlign = ContentAlignment.MiddleLeft,
                UseMnemonic = false,
                BackColor = Color.White,
            };

            var lblBody = new Label
            {
                Text = body ?? "",
                Font = new Font("Segoe UI", 8.75F),
                ForeColor = ClrTextSecondary,
                AutoSize = false,
                Location = new Point(AccentStripWidth + 10, 26),
                Size = new Size(ToastWidth - AccentStripWidth - CloseButtonSize - 20, 24),
                TextAlign = ContentAlignment.TopLeft,
                UseMnemonic = false,
                AutoEllipsis = true,
                BackColor = Color.White,
            };

            var btnClose = new Label
            {
                Text = "✕",
                Font = new Font("Segoe UI", 9F),
                ForeColor = ClrTextSecondary,
                BackColor = Color.White,
                AutoSize = false,
                Size = new Size(CloseButtonSize, CloseButtonSize),
                Location = new Point(ToastWidth - CloseButtonSize - 4, 4),
                TextAlign = ContentAlignment.MiddleCenter,
                Cursor = Cursors.Hand,
            };
            btnClose.MouseEnter += (_, _) =>
            {
                btnClose.BackColor = ClrCloseHover;
                btnClose.ForeColor = Color.White;
            };
            btnClose.MouseLeave += (_, _) =>
            {
                btnClose.BackColor = Color.White;
                btnClose.ForeColor = ClrTextSecondary;
            };
            btnClose.Click += (_, _) => DismissNow();

            Controls.Add(lblBody);
            Controls.Add(lblTitle);
            Controls.Add(btnClose);
            Controls.Add(accent);

            _lifetimeTimer = new Timer { Interval = DefaultLifetimeMs };
            _lifetimeTimer.Tick += (_, _) => DismissNow();
            _lifetimeTimer.Start();

            FormClosed += (_, _) =>
            {
                _lifetimeTimer.Stop();
                _lifetimeTimer.Dispose();
                lock (_liveLock)
                {
                    _liveToasts.Remove(this);
                    RestackLocked();
                }
            };
        }

        private void DismissNow()
        {
            if (IsDisposed) return;
            try { Close(); } catch { /* best effort */ }
        }

        // CreateParams override: WS_EX_NOACTIVATE so the toast does not
        // steal focus from the diagram / editor when it pops up - the
        // user's pointer stays where it was.
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation => true;

        /// <summary>
        /// Display a transient notification in the bottom-right corner
        /// of the addin's active screen. Safe to call from any thread:
        /// marshals back to the UI thread of <see cref="ErwinAddIn.ActiveForm"/>
        /// when needed. No-ops when there is no addin form available.
        /// </summary>
        public static void Show(string title, string body)
        {
            try
            {
                var addinForm = EliteSoft.Erwin.AddIn.ErwinAddIn.ActiveForm;
                if (addinForm == null || addinForm.IsDisposed) return;

                if (addinForm.InvokeRequired)
                {
                    addinForm.BeginInvoke(new Action(() => ShowOnUiThread(title, body)));
                }
                else
                {
                    ShowOnUiThread(title, body);
                }
            }
            catch (Exception ex)
            {
                // Toasts are advisory - swallow rather than crash the host
                // path (e.g. a transactional naming auto-apply) on UI
                // hiccups like a disposed addin form mid-tick.
                System.Diagnostics.Debug.WriteLine($"ToastNotification.Show failed: {ex.Message}");
            }
        }

        private static void ShowOnUiThread(string title, string body)
        {
            var toast = new ToastNotification(title, body);
            lock (_liveLock)
            {
                _liveToasts.Add(toast);
                RestackLocked();
            }
            toast.Show();
        }

        // Compute stacked positions for every live toast. Newest sits at
        // the bottom; older ones float upward. Called under _liveLock.
        private static void RestackLocked()
        {
            Screen target = PickScreen();
            var area = target.WorkingArea;

            int y = area.Bottom - ScreenEdgeMargin - ToastHeight;
            // _liveToasts ordered chronologically (oldest first); we walk
            // from newest backwards so the newest is closest to the bottom.
            for (int i = _liveToasts.Count - 1; i >= 0; i--)
            {
                var t = _liveToasts[i];
                if (t.IsDisposed) continue;
                int x = area.Right - ScreenEdgeMargin - ToastWidth;
                try
                {
                    t.Location = new Point(x, y);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"ToastNotification.RestackLocked move failed: {ex.Message}");
                }
                y -= ToastHeight + 8;
                if (y < area.Top) break; // off-screen - drop further toasts; lifetime timer will clean
            }
        }

        private static Screen PickScreen()
        {
            var addinForm = EliteSoft.Erwin.AddIn.ErwinAddIn.ActiveForm;
            if (addinForm != null && !addinForm.IsDisposed && addinForm.IsHandleCreated)
                return Screen.FromControl(addinForm);
            IntPtr fg = IntPtr.Zero;
            try { fg = GetForegroundWindow(); } catch { /* fall through */ }
            return fg != IntPtr.Zero ? Screen.FromHandle(fg) : (Screen.PrimaryScreen ?? Screen.AllScreens[0]);
        }
    }
}
