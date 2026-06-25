#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

using EliteSoft.Erwin.AddIn.Services;

namespace EliteSoft.Erwin.AddIn.Forms
{
    /// <summary>
    /// Read-only diagram of a config's deployment-environment topology for the
    /// add-in's Integrate tab. It mirrors the visual language of the admin
    /// app's Integrate screen (rounded environment nodes, directed Bezier
    /// arrows with forward arcs above / backward arcs below, orange arrows plus
    /// a badge for approval-gated transitions) but is an independent
    /// reimplementation in pure System.Drawing - the admin renderer is a
    /// private, app-coupled control and is not referenceable from here.
    ///
    /// On top of the admin visual the add-in adds two model-centric things: the
    /// environment the open model currently sits in is highlighted, and each
    /// ALLOWED transition out of that environment (a non-approval relation whose
    /// source is the current environment) carries a small promote icon button
    /// on its arrow. Clicking it raises <see cref="IntegrateRequested"/>.
    /// Approval-gated transitions show the badge and never a button.
    /// </summary>
    internal sealed class EnvironmentPipelineDiagram : Panel
    {
        // Palette mirrored from the admin diagram + the add-in accent.
        private static readonly Color NodeFill = Color.White;
        private static readonly Color NodeFillCurrent = Color.FromArgb(232, 242, 254);
        private static readonly Color BorderDefault = Color.FromArgb(148, 163, 184);
        private static readonly Color Accent = Color.FromArgb(0, 102, 204);
        private static readonly Color ArrowNormal = Color.FromArgb(100, 116, 139);
        private static readonly Color ArrowApproval = Color.FromArgb(234, 88, 12);
        private static readonly Color TextPrimary = Color.FromArgb(26, 26, 26);

        private const int NodeW = 116;
        private const int NodeH = 48;
        private const int HGap = 96;
        private const int LeftMargin = 16;
        private const int TopPad = 16;
        private const int BottomPad = 18;
        private const int CaptionH = 18;

        // Shared so a model switch that rebuilds the buttons does not allocate a
        // fresh Font handle per button each time.
        private static readonly Font ActionGlyphFont = new Font("Segoe UI", 9f, FontStyle.Bold);

        private readonly ToolTip _tip = new ToolTip();
        private readonly List<Button> _actionButtons = new List<Button>();
        private readonly Dictionary<int, Rectangle> _rects = new Dictionary<int, Rectangle>();
        private readonly Dictionary<int, int> _index = new Dictionary<int, int>();

        private IReadOnlyList<IntegrationEnvironment> _envs = Array.Empty<IntegrationEnvironment>();
        private IReadOnlyList<IntegrationRelation> _relations = Array.Empty<IntegrationRelation>();
        private int _currentId = -1;

        /// <summary>Raised when the user clicks the promote button on an allowed transition. Argument is the target environment.</summary>
        public event Action<IntegrationEnvironment>? IntegrateRequested;

        public EnvironmentPipelineDiagram()
        {
            DoubleBuffered = true;
            BackColor = Color.White;
            SetStyle(ControlStyles.ResizeRedraw, true);
        }

        /// <summary>
        /// Feeds the diagram the full topology and the environment the open model
        /// currently sits in, then lays out nodes/arrows and (re)creates the
        /// promote buttons. Safe to call repeatedly (model switch / reconnect).
        /// </summary>
        public void SetData(
            IReadOnlyList<IntegrationEnvironment> environments,
            IReadOnlyList<IntegrationRelation> relations,
            int currentEnvironmentId)
        {
            _envs = (environments ?? new List<IntegrationEnvironment>())
                .OrderBy(e => e.SortOrder).ThenBy(e => e.Id).ToList();
            _relations = relations ?? new List<IntegrationRelation>();
            _currentId = currentEnvironmentId;
            RebuildLayout();
            Invalidate();
        }

        private void RebuildLayout()
        {
            foreach (var b in _actionButtons) { Controls.Remove(b); b.Region?.Dispose(); b.Dispose(); }
            _actionButtons.Clear();
            _rects.Clear();
            _index.Clear();

            int n = _envs.Count;
            if (n == 0)
            {
                Size = new Size(LeftMargin * 2 + NodeW, TopPad + NodeH + BottomPad);
                return;
            }

            for (int i = 0; i < n; i++) _index[_envs[i].Id] = i;

            // Largest forward/backward arc so the panel is sized to never clip.
            int maxFwd = 0, maxBwd = 0;
            foreach (var r in _relations)
            {
                if (!_index.TryGetValue(r.FromEnvironmentId, out int fi) ||
                    !_index.TryGetValue(r.ToEnvironmentId, out int ti)) continue;
                int lift = LiftFor(Math.Abs(ti - fi));
                if (ti >= fi) maxFwd = Math.Max(maxFwd, lift); else maxBwd = Math.Max(maxBwd, lift);
            }

            int laneTop = TopPad + maxFwd;
            int width = LeftMargin * 2 + n * NodeW + (n - 1) * HGap;
            int height = laneTop + NodeH + Math.Max(maxBwd, CaptionH) + BottomPad;
            Size = new Size(width, height);

            for (int i = 0; i < n; i++)
                _rects[_envs[i].Id] = new Rectangle(LeftMargin + i * (NodeW + HGap), laneTop, NodeW, NodeH);

            // A promote button per allowed (non-approval) transition out of current.
            string currentName = _envs.FirstOrDefault(e => e.Id == _currentId)?.Name ?? "current";
            foreach (var r in _relations)
            {
                if (r.FromEnvironmentId != _currentId || r.RequiresApproval) continue;
                if (!_rects.TryGetValue(r.FromEnvironmentId, out var fr) ||
                    !_rects.TryGetValue(r.ToEnvironmentId, out var tr)) continue;

                var target = _envs.FirstOrDefault(e => e.Id == r.ToEnvironmentId);
                if (target == null) continue;

                bool forward = _index[r.ToEnvironmentId] >= _index[r.FromEnvironmentId];
                int lift = LiftFor(Math.Abs(_index[r.ToEnvironmentId] - _index[r.FromEnvironmentId]));
                Point apex = ApexOf(fr, tr, forward, lift);

                var btn = MakeActionButton();
                btn.Location = new Point(apex.X - btn.Width / 2, apex.Y - btn.Height / 2);
                var captured = target;
                btn.Click += (s, e) => IntegrateRequested?.Invoke(captured);
                _tip.SetToolTip(btn, $"Integrate: {currentName} → {target.Name}");
                Controls.Add(btn);
                btn.BringToFront();
                _actionButtons.Add(btn);
            }
        }

        // Arc height grows with how many environments a transition skips, so a
        // Dev -> Prod jump clears the Test node it passes over. Capped so deep
        // pipelines stay bounded.
        private static int LiftFor(int distance) => Math.Min(80, 22 + (Math.Max(1, distance) - 1) * 20);

        // Midpoint of the cubic Bezier (t = 0.5) where both controls are lifted
        // by `lift`: x is the chord midpoint, y is base +/- 0.75*lift.
        private static Point ApexOf(Rectangle fr, Rectangle tr, bool forward, int lift)
        {
            int p0x = fr.Left + fr.Width / 2;
            int p3x = tr.Left + tr.Width / 2;
            int baseY = forward ? fr.Top : fr.Bottom;
            int dir = forward ? -1 : 1;
            return new Point((p0x + p3x) / 2, (int)Math.Round(baseY + dir * 0.75 * lift));
        }

        private Button MakeActionButton()
        {
            var btn = new Button
            {
                Size = new Size(26, 26),
                FlatStyle = FlatStyle.Flat,
                BackColor = Accent,
                ForeColor = Color.White,
                Font = ActionGlyphFont,
                Text = "▶", // play glyph = promote
                Cursor = Cursors.Hand,
                TabStop = false
            };
            btn.FlatAppearance.BorderSize = 0;
            using (var path = CirclePath(new Rectangle(0, 0, btn.Width, btn.Height)))
                btn.Region = new Region(path);
            return btn;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (_envs.Count == 0) return;

            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            // Arrows first so the nodes paint on top of the chord endpoints.
            foreach (var r in _relations)
            {
                if (!_rects.TryGetValue(r.FromEnvironmentId, out var fr) ||
                    !_rects.TryGetValue(r.ToEnvironmentId, out var tr)) continue;

                bool forward = _index[r.ToEnvironmentId] >= _index[r.FromEnvironmentId];
                int lift = LiftFor(Math.Abs(_index[r.ToEnvironmentId] - _index[r.FromEnvironmentId]));
                DrawArrow(g, fr, tr, forward, lift, r.RequiresApproval ? ArrowApproval : ArrowNormal);
                if (r.RequiresApproval)
                    DrawApprovalBadge(g, ApexOf(fr, tr, forward, lift));
            }

            using var fontName = new Font("Segoe UI", 9.5f, FontStyle.Bold);
            using var fontCap = new Font("Segoe UI", 8f);
            foreach (var env in _envs)
            {
                var rect = _rects[env.Id];
                bool isCurrent = env.Id == _currentId;
                Color border = isCurrent ? Accent : (TryParseHex(env.ColorHex) ?? BorderDefault);
                int borderW = isCurrent ? 3 : 2;

                using (var path = RoundedPath(rect, 8))
                {
                    using (var fill = new SolidBrush(isCurrent ? NodeFillCurrent : NodeFill))
                        g.FillPath(fill, path);
                    using (var pen = new Pen(border, borderW))
                        g.DrawPath(pen, path);
                }

                TextRenderer.DrawText(g, env.Name, fontName, rect, TextPrimary,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

                if (isCurrent)
                {
                    var capRect = new Rectangle(rect.Left, rect.Bottom + 2, rect.Width, CaptionH);
                    TextRenderer.DrawText(g, "current", fontCap, capRect, Accent,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.Top);
                }
            }
        }

        private static void DrawArrow(Graphics g, Rectangle fr, Rectangle tr, bool forward, int lift, Color color)
        {
            var p0 = new Point(fr.Left + fr.Width / 2, forward ? fr.Top : fr.Bottom);
            var p3 = new Point(tr.Left + tr.Width / 2, forward ? tr.Top : tr.Bottom);
            int dir = forward ? -1 : 1;
            var c1 = new Point(p0.X, p0.Y + dir * lift);
            var c2 = new Point(p3.X, p3.Y + dir * lift);
            using var pen = new Pen(color, 2f) { CustomEndCap = new AdjustableArrowCap(5, 5) };
            g.DrawBezier(pen, p0, c1, c2, p3);
        }

        private static void DrawApprovalBadge(Graphics g, Point center)
        {
            const int d = 18;
            int r = d / 2;
            var rect = new Rectangle(center.X - r, center.Y - r, d, d);
            using var brush = new SolidBrush(ArrowApproval);
            using var pen = new Pen(Color.White, 1.5f);
            using var font = new Font("Segoe UI", 9f, FontStyle.Bold);
            g.FillEllipse(brush, rect);
            g.DrawEllipse(pen, rect);
            TextRenderer.DrawText(g, "⚠", font, rect, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        private static GraphicsPath RoundedPath(Rectangle r, int radius)
        {
            int d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private static GraphicsPath CirclePath(Rectangle r)
        {
            var path = new GraphicsPath();
            path.AddEllipse(r);
            return path;
        }

        private static Color? TryParseHex(string? hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return null;
            string s = hex.Trim().TrimStart('#');
            if (s.Length != 6) return null;
            return int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb)
                ? Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF)
                : (Color?)null;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _tip.Dispose();
                foreach (var b in _actionButtons) { b.Region?.Dispose(); b.Dispose(); }
                _actionButtons.Clear();
            }
            base.Dispose(disposing);
        }
    }
}
