#nullable enable
using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace EliteSoft.Erwin.AddIn.Forms
{
    /// <summary>
    /// Drop-in replacement for <see cref="MessageBox"/> calls inside the
    /// add-in. Modeled on the Windows TaskDialog layout (icon glyph on the
    /// left, message text on the right, button row at the bottom) but with
    /// borderless chrome so the title appears exactly once - in our own
    /// prominent header row, not duplicated in the OS title bar.
    ///
    /// Why it exists (2026-05-15): the system <c>MessageBox</c> looks like
    /// "another Windows dialog", which inside erwin visually disappears
    /// among the host's own warnings and errors. Users sometimes could not
    /// tell at a glance which popup came from the add-in. The custom dialog
    /// keeps the add-in's accent colour and Segoe UI palette so its origin
    /// is obvious.
    ///
    /// Key UX decisions (after first-pass review, 2026-05-15):
    /// 1. <c>FormBorderStyle.None</c> - one title, drawn by us, never greyed
    ///    by Win11's "inactive window" style.
    /// 2. <c>TopMost = true</c> kept ON for the dialog's full lifetime so
    ///    the popup can never be hidden behind the addin form (releasing on
    ///    Shown - a previous design - allowed the addin form to push it
    ///    back, dead-locking the user).
    /// 3. Multi-monitor: position is computed against <see cref="ErwinAddIn.ActiveForm"/>'s
    ///    screen first, foreground-window screen second, primary screen
    ///    last. This guarantees the popup lands on the same display as the
    ///    add-in regardless of whether the caller supplied an owner.
    /// 4. Drag-by-header so the user can still move it out of the way even
    ///    without a system title bar.
    ///
    /// Lesson 2026-05-07: ShowDialog pumps the message loop while modal,
    /// so timer-driven code that relies on its own reentrancy guards
    /// (ValidationCoordinatorService._scopedCheckInProgress etc.) keeps
    /// working unchanged - same behaviour as MessageBox.
    /// </summary>
    public sealed class AddinMessageDialog : Form
    {
        // Design tokens (shared with LockedUdpDialog / RequiredUdpForm).
        private static readonly Color ClrInfo = Color.FromArgb(0, 102, 204);     // primary blue
        private static readonly Color ClrWarning = Color.FromArgb(204, 102, 0);  // amber
        private static readonly Color ClrError = Color.FromArgb(192, 57, 43);    // red
        private static readonly Color ClrQuestion = Color.FromArgb(0, 102, 204); // primary blue (matches Info)
        private static readonly Color ClrTextPrimary = Color.FromArgb(26, 26, 26);
        private static readonly Color ClrTextSecondary = Color.FromArgb(102, 102, 102);
        private static readonly Color ClrBorder = Color.FromArgb(208, 208, 208);
        private static readonly Color ClrSurface = Color.FromArgb(245, 247, 250);
        private static readonly Color ClrCloseHover = Color.FromArgb(232, 17, 35); // Windows-style red-on-hover

        private const int DialogWidth = 460;
        private const int MinDialogHeight = 180;
        private const int IconSlot = 32;
        private const int IconTextGap = 14;
        private const int BodyHorizontalPadding = 20;
        private const int BodyVerticalPadding = 18;
        private const int FooterHeight = 50;
        private const int AccentStripHeight = 4;
        private const int HeaderHeight = 46;
        private const int CloseButtonSize = 32;

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private const int WM_NCLBUTTONDOWN = 0xA1;
        private const int HTCAPTION = 0x2;

        private AddinMessageDialog(
            string text,
            string title,
            MessageBoxButtons buttons,
            MessageBoxIcon icon)
        {
            Color accent = AccentFromIcon(icon);
            string effectiveTitle = string.IsNullOrEmpty(title) ? IconToFallbackTitle(icon) : title;

            // Form.Text still set for accessibility / taskbar, but
            // FormBorderStyle.None hides it from the screen so there is no
            // duplicate-title artifact.
            Text = effectiveTitle;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = Color.White;
            Font = new Font("Segoe UI", 9.5F);
            // 1px outer border so the borderless dialog has visual separation
            // from whatever sits behind it (especially on light backgrounds).
            Padding = new Padding(1);
            // Use a paint hook on the form to draw the outer border line.
            Paint += (_, e) =>
            {
                using var pen = new Pen(ClrBorder, 1);
                e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            };

            // TopMost for the whole lifetime (verified necessary 2026-05-15:
            // releasing on Shown caused the addin form to push the popup
            // behind it during fast focus transitions).
            TopMost = true;

            // Accent strip
            var accentStrip = new Panel
            {
                Dock = DockStyle.Top,
                Height = AccentStripHeight,
                BackColor = accent
            };

            // Header row: title + drag handle + close X
            var header = new Panel
            {
                Dock = DockStyle.Top,
                Height = HeaderHeight,
                BackColor = Color.White,
                Cursor = Cursors.SizeAll
            };

            var lblHeader = new Label
            {
                Text = effectiveTitle,
                Font = new Font("Segoe UI", 13F, FontStyle.Bold),
                ForeColor = ClrTextPrimary,
                AutoSize = false,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(BodyHorizontalPadding, 0, CloseButtonSize + 8, 0),
                UseMnemonic = false,
                Cursor = Cursors.SizeAll
            };

            // Custom close X (right-aligned in header). Draws "X" glyph, turns
            // Windows-red on hover. Mirrors Windows' own title-bar close
            // affordance so users recognise it instantly.
            var btnClose = new Label
            {
                Text = "✕", // U+2715 MULTIPLICATION X (no Wingdings needed)
                Font = new Font("Segoe UI", 10F, FontStyle.Regular),
                ForeColor = ClrTextSecondary,
                BackColor = Color.White,
                AutoSize = false,
                Size = new Size(CloseButtonSize, CloseButtonSize),
                TextAlign = ContentAlignment.MiddleCenter,
                Cursor = Cursors.Hand,
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Location = new Point(DialogWidth - CloseButtonSize - 2, (HeaderHeight - CloseButtonSize) / 2),
                Tag = "close"
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
            btnClose.Click += (_, _) =>
            {
                DialogResult = DialogResult.Cancel;
                Close();
            };

            // Drag-by-header: clicking anywhere on the header (except the close
            // button) triggers the OS window-drag protocol via WM_NCLBUTTONDOWN
            // HTCAPTION. This is the same trick LockedUdpDialog uses; for
            // FormBorderStyle.None it is the only way to let the user move the
            // dialog out of the way.
            void StartDrag(object? s, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left) return;
                ReleaseCapture();
                SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
            }
            header.MouseDown += StartDrag;
            lblHeader.MouseDown += StartDrag;

            header.Controls.Add(btnClose);
            header.Controls.Add(lblHeader);

            var headerSep = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = ClrBorder };

            // Body: icon + message
            var body = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.White,
                Padding = new Padding(
                    BodyHorizontalPadding, BodyVerticalPadding,
                    BodyHorizontalPadding, BodyVerticalPadding)
            };

            var iconBox = new PictureBox
            {
                Size = new Size(IconSlot, IconSlot),
                Location = new Point(BodyHorizontalPadding, BodyVerticalPadding),
                SizeMode = PictureBoxSizeMode.CenterImage,
                BackColor = Color.Transparent,
                TabStop = false
            };
            var glyph = GlyphBitmapFromIcon(icon);
            if (glyph != null) iconBox.Image = glyph;

            int textWidth = DialogWidth
                            - BodyHorizontalPadding * 2
                            - IconSlot
                            - IconTextGap
                            - 18;
            var lblBody = new Label
            {
                Text = text ?? string.Empty,
                Font = new Font("Segoe UI", 9.75F),
                ForeColor = ClrTextPrimary,
                AutoSize = false,
                Location = new Point(
                    BodyHorizontalPadding + IconSlot + IconTextGap,
                    BodyVerticalPadding),
                Size = new Size(textWidth, 0),
                TextAlign = ContentAlignment.TopLeft,
                UseMnemonic = false
            };

            body.Controls.Add(iconBox);
            body.Controls.Add(lblBody);

            var footerSep = new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = ClrBorder };

            var footer = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = FooterHeight,
                BackColor = ClrSurface,
                Padding = new Padding(16, 8, 16, 8)
            };
            BuildFooterButtons(footer, buttons, accent, out Button? acceptBtn, out Button? cancelBtn);

            Controls.Add(body);
            Controls.Add(footerSep);
            Controls.Add(footer);
            Controls.Add(headerSep);
            Controls.Add(header);
            Controls.Add(accentStrip);

            if (acceptBtn != null) AcceptButton = acceptBtn;
            if (cancelBtn != null) CancelButton = cancelBtn;
            if (acceptBtn != null) ActiveControl = acceptBtn;

            // Belt-and-suspenders: TopMost keeps z-order, SetForegroundWindow
            // ensures keyboard focus when erwin's main thread is busy.
            Shown += (_, _) =>
            {
                if (IsDisposed) return;
                try { SetForegroundWindow(Handle); } catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"AddinMessageDialog SetForegroundWindow failed: {ex.Message}");
                }
            };

            ResizeToFitBody(lblBody);
        }

        /// <summary>
        /// Place the dialog at the centre of the screen the user is currently
        /// working on. Strategy in priority order:
        ///   1. <see cref="ErwinAddIn.ActiveForm"/> screen - the addin window
        ///      the user is interacting with (most reliable).
        ///   2. Caller-supplied owner's screen.
        ///   3. Foreground window's screen.
        ///   4. Primary screen.
        /// Manual positioning is necessary because <see cref="FormStartPosition.CenterParent"/>
        /// / <see cref="FormStartPosition.CenterScreen"/> both fail silently
        /// when the owner sits on a non-primary monitor with negative-origin
        /// virtual coordinates (verified 2026-05-15).
        /// </summary>
        private void PositionOnActiveScreen(IWin32Window? owner)
        {
            Screen target;
            string source;

            // 1. Addin's active form - this is the user's anchor point.
            var addinForm = EliteSoft.Erwin.AddIn.ErwinAddIn.ActiveForm;
            if (addinForm != null && !addinForm.IsDisposed && addinForm.IsHandleCreated)
            {
                target = Screen.FromControl(addinForm);
                source = "addinForm";
            }
            // 2. Explicit owner from the caller.
            else if (owner is Control { IsDisposed: false } ownerCtrl && ownerCtrl.IsHandleCreated)
            {
                target = Screen.FromControl(ownerCtrl);
                source = "owner";
            }
            else
            {
                // 3 / 4. Foreground window or primary fallback.
                IntPtr fg = IntPtr.Zero;
                try { fg = GetForegroundWindow(); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"AddinMessageDialog GetForegroundWindow failed: {ex.Message}"); }
                if (fg != IntPtr.Zero)
                {
                    target = Screen.FromHandle(fg);
                    source = "foreground";
                }
                else
                {
                    target = Screen.PrimaryScreen ?? Screen.AllScreens[0];
                    source = "primary";
                }
            }

            var area = target.WorkingArea;
            int x = area.Left + Math.Max(0, (area.Width - Width) / 2);
            int y = area.Top + Math.Max(0, (area.Height - Height) / 2);
            Location = new Point(x, y);
            System.Diagnostics.Debug.WriteLine(
                $"AddinMessageDialog positioned via {source} on screen '{target.DeviceName}' ({area}) -> ({x},{y}) size {Width}x{Height}");
        }

        #region Footer button row

        private void BuildFooterButtons(
            Panel footer,
            MessageBoxButtons buttons,
            Color accent,
            out Button? acceptBtn,
            out Button? cancelBtn)
        {
            acceptBtn = null;
            cancelBtn = null;

            switch (buttons)
            {
                case MessageBoxButtons.OK:
                {
                    var ok = MakePrimaryButton("OK", DialogResult.OK, accent);
                    AddRightAligned(footer, ok, 0);
                    acceptBtn = ok;
                    cancelBtn = ok;
                    return;
                }
                case MessageBoxButtons.OKCancel:
                {
                    var ok = MakePrimaryButton("OK", DialogResult.OK, accent);
                    var cancel = MakeSecondaryButton("Cancel", DialogResult.Cancel);
                    AddRightAligned(footer, ok, 0);
                    AddRightAligned(footer, cancel, 1);
                    acceptBtn = ok;
                    cancelBtn = cancel;
                    return;
                }
                case MessageBoxButtons.YesNo:
                {
                    var yes = MakePrimaryButton("Yes", DialogResult.Yes, accent);
                    var no = MakeSecondaryButton("No", DialogResult.No);
                    AddRightAligned(footer, yes, 0);
                    AddRightAligned(footer, no, 1);
                    acceptBtn = yes;
                    cancelBtn = no;
                    return;
                }
                case MessageBoxButtons.YesNoCancel:
                {
                    var yes = MakePrimaryButton("Yes", DialogResult.Yes, accent);
                    var no = MakeSecondaryButton("No", DialogResult.No);
                    var cancel = MakeSecondaryButton("Cancel", DialogResult.Cancel);
                    AddRightAligned(footer, yes, 0);
                    AddRightAligned(footer, no, 1);
                    AddRightAligned(footer, cancel, 2);
                    acceptBtn = yes;
                    cancelBtn = cancel;
                    return;
                }
                default:
                {
                    var ok = MakePrimaryButton("OK", DialogResult.OK, accent);
                    AddRightAligned(footer, ok, 0);
                    acceptBtn = ok;
                    cancelBtn = ok;
                    return;
                }
            }
        }

        private static Button MakePrimaryButton(string text, DialogResult result, Color accent)
        {
            var b = new Button
            {
                Text = text,
                Size = new Size(96, 32),
                FlatStyle = FlatStyle.Flat,
                BackColor = accent,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
                Cursor = Cursors.Hand,
                Anchor = AnchorStyles.Right | AnchorStyles.Bottom,
                DialogResult = result,
                UseMnemonic = false,
                TabStop = true
            };
            b.FlatAppearance.BorderSize = 0;
            return b;
        }

        private static Button MakeSecondaryButton(string text, DialogResult result)
        {
            var b = new Button
            {
                Text = text,
                Size = new Size(96, 32),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.White,
                ForeColor = ClrTextPrimary,
                Font = new Font("Segoe UI", 9.5F),
                Cursor = Cursors.Hand,
                Anchor = AnchorStyles.Right | AnchorStyles.Bottom,
                DialogResult = result,
                UseMnemonic = false,
                TabStop = true
            };
            b.FlatAppearance.BorderColor = ClrBorder;
            return b;
        }

        private static void AddRightAligned(Panel footer, Button btn, int slotFromRight)
        {
            const int btnGap = 8;
            const int rightPadding = 16;
            void Place()
            {
                int x = footer.ClientSize.Width
                        - rightPadding
                        - (slotFromRight + 1) * btn.Width
                        - slotFromRight * btnGap;
                btn.Location = new Point(x, 9);
            }
            Place();
            footer.Resize += (_, _) => Place();
            footer.Controls.Add(btn);
        }

        #endregion

        #region Sizing + icon glyph

        private void ResizeToFitBody(Label lblBody)
        {
            Size measured;
            using (var g = CreateGraphics())
            {
                // TextFormatFlags.NoPadding strips the descender slack
                // Windows normally reserves below the baseline (used by g, y,
                // p, j). Without it a single line like "Owner girilmelidir!"
                // gets its 'g' tail clipped at the bottom of the label.
                // Use the default (no NoPadding) and add a couple of extra
                // pixels as a safety margin against per-font metrics that
                // still draw outside MeasureText's reported height on some
                // DPI settings.
                measured = TextRenderer.MeasureText(
                    g,
                    lblBody.Text,
                    lblBody.Font,
                    new Size(lblBody.Width, int.MaxValue),
                    TextFormatFlags.WordBreak);
            }

            const int DescenderSafety = 4;
            int bodyContentHeight = Math.Max(IconSlot, measured.Height + DescenderSafety);
            int bodyPanelHeight = bodyContentHeight + BodyVerticalPadding * 2;

            int chromeHeight = AccentStripHeight + HeaderHeight + 1 + 1 + FooterHeight;
            int totalHeight = Math.Max(MinDialogHeight, chromeHeight + bodyPanelHeight);

            int maxScreen = (int)((Screen.PrimaryScreen?.WorkingArea.Height ?? 800) * 0.75);
            if (totalHeight > maxScreen) totalHeight = maxScreen;

            lblBody.Height = bodyContentHeight;
            ClientSize = new Size(DialogWidth, totalHeight);
        }

        private static Bitmap? GlyphBitmapFromIcon(MessageBoxIcon icon)
        {
            try
            {
                Icon? src = icon switch
                {
                    MessageBoxIcon.Warning => SystemIcons.Warning,
                    MessageBoxIcon.Error => SystemIcons.Error,
                    MessageBoxIcon.Question => SystemIcons.Question,
                    MessageBoxIcon.Information => SystemIcons.Information,
                    _ => null
                };
                if (src == null) return null;
                return new Icon(src, IconSlot, IconSlot).ToBitmap();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"AddinMessageDialog: glyph load failed: {ex.Message}");
                return null;
            }
        }

        private static Color AccentFromIcon(MessageBoxIcon icon) => icon switch
        {
            MessageBoxIcon.Warning => ClrWarning,
            MessageBoxIcon.Error => ClrError,
            MessageBoxIcon.Question => ClrQuestion,
            MessageBoxIcon.Information => ClrInfo,
            _ => ClrInfo
        };

        private static string IconToFallbackTitle(MessageBoxIcon icon) => icon switch
        {
            MessageBoxIcon.Warning => "Warning",
            MessageBoxIcon.Error => "Error",
            MessageBoxIcon.Question => "Confirm",
            MessageBoxIcon.Information => "Information",
            _ => "Notice"
        };

        #endregion

        #region Static API (mirrors MessageBox.Show)

        public static DialogResult Show(string text)
            => Show(null, text, "", MessageBoxButtons.OK, MessageBoxIcon.None);

        public static DialogResult Show(string text, string title)
            => Show(null, text, title, MessageBoxButtons.OK, MessageBoxIcon.None);

        public static DialogResult Show(string text, string title, MessageBoxButtons buttons)
            => Show(null, text, title, buttons, MessageBoxIcon.None);

        public static DialogResult Show(string text, string title, MessageBoxButtons buttons, MessageBoxIcon icon)
            => Show(null, text, title, buttons, icon);

        public static DialogResult Show(IWin32Window? owner, string text)
            => Show(owner, text, "", MessageBoxButtons.OK, MessageBoxIcon.None);

        public static DialogResult Show(IWin32Window? owner, string text, string title)
            => Show(owner, text, title, MessageBoxButtons.OK, MessageBoxIcon.None);

        public static DialogResult Show(IWin32Window? owner, string text, string title, MessageBoxButtons buttons)
            => Show(owner, text, title, buttons, MessageBoxIcon.None);

        public static DialogResult Show(
            IWin32Window? owner,
            string text,
            string title,
            MessageBoxButtons buttons,
            MessageBoxIcon icon)
        {
            try
            {
                using var dlg = new AddinMessageDialog(text, title, buttons, icon);
                dlg.PositionOnActiveScreen(owner);

                // Owner resolution. A caller-supplied owner always wins. With no
                // explicit owner we must NOT blindly attach to the addin config
                // form: that made background notifications (e.g. "Naming standard
                // applied", raised while the user is working inside erwin) drag the
                // config window to the front - a reported UX bug. See
                // ResolveImplicitOwner for the foreground-aware policy.
                IWin32Window? effectiveOwner = owner ?? ResolveImplicitOwner();

                if (effectiveOwner != null)
                    return dlg.ShowDialog(effectiveOwner);
                return dlg.ShowDialog();
            }
            catch (Exception ex)
            {
                // Last-resort fallback - mirrors LockedUdpDialog pattern.
                System.Diagnostics.Debug.WriteLine($"AddinMessageDialog.Show fallback: {ex.Message}");
                return MessageBox.Show(text, title, buttons, icon);
            }
        }

        /// <summary>
        /// Picks the implicit ShowDialog owner when the caller passes none.
        /// Policy (2026-06-09): attach the popup to whatever window the user is
        /// actually looking at, so a background notification never drags the
        /// addin's config window to the front:
        ///   - config form IS the foreground window -> own it (correct modality
        ///     for popups the config form itself raised from a button click);
        ///   - otherwise (the user is inside erwin)  -> own erwin's main frame so
        ///     the popup floats over erwin and the config form stays where it is;
        ///   - erwin frame not found                 -> null (ownerless, still
        ///     TopMost) which likewise never surfaces the config form.
        /// The dialog keeps TopMost=true + SetForegroundWindow regardless, so it
        /// is always visible whichever owner we pick here.
        /// </summary>
        private static IWin32Window? ResolveImplicitOwner()
        {
            try
            {
                var addinForm = EliteSoft.Erwin.AddIn.ErwinAddIn.ActiveForm;
                if (addinForm != null && !addinForm.IsDisposed && addinForm.IsHandleCreated)
                {
                    // Only own the config form when it is genuinely the foreground
                    // window - then the popup was raised from its own UI and should
                    // be modal to it. GetForegroundWindow returns the top-level
                    // window, which equals the form handle when it (or a child) has
                    // focus.
                    if (GetForegroundWindow() == addinForm.Handle)
                        return addinForm;
                }

                IntPtr erwinMain = EliteSoft.Erwin.AddIn.Services.Win32Helper.GetErwinMainWindow();
                if (erwinMain != IntPtr.Zero)
                    return new HwndOwner(erwinMain);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"AddinMessageDialog.ResolveImplicitOwner failed: {ex.Message}");
            }

            return null; // ownerless TopMost - does not surface the config form
        }

        /// <summary>Minimal <see cref="IWin32Window"/> over a raw HWND (erwin's main frame).</summary>
        private sealed class HwndOwner : IWin32Window
        {
            public HwndOwner(IntPtr handle) => Handle = handle;
            public IntPtr Handle { get; }
        }

        #endregion
    }
}
