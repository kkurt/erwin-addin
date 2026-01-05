using System;
using System.Drawing;
using System.Windows.Forms;

namespace EliteSoft.Erwin.Admin.UI
{
    /// <summary>
    /// Factory for creating styled UI controls
    /// </summary>
    public static class ControlFactory
    {
        /// <summary>
        /// Creates a styled label
        /// </summary>
        public static Label CreateLabel(string text, int x, int y, Font font = null, Color? foreColor = null)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                Location = new Point(x, y),
                ForeColor = foreColor ?? AppTheme.TextPrimary,
                Font = font ?? AppTheme.DefaultFont
            };
        }

        /// <summary>
        /// Creates a title label
        /// </summary>
        public static Label CreateTitle(string text, int x, int y)
        {
            return CreateLabel(text, x, y, AppTheme.TitleFont, AppTheme.TextPrimary);
        }

        /// <summary>
        /// Creates a styled textbox
        /// </summary>
        public static TextBox CreateTextBox(int x, int y, int width, string defaultValue = "", bool isPassword = false)
        {
            var txt = new TextBox
            {
                Location = new Point(x, y),
                Size = new Size(width, 26),
                BackColor = AppTheme.InputBackground,
                ForeColor = AppTheme.TextPrimary,
                BorderStyle = BorderStyle.FixedSingle,
                Font = AppTheme.DefaultFont,
                Text = defaultValue,
                UseSystemPasswordChar = isPassword
            };
            return txt;
        }

        /// <summary>
        /// Creates a styled button
        /// </summary>
        public static Button CreateButton(
            string text,
            int x,
            int y,
            int width,
            int height,
            ButtonStyle style = ButtonStyle.Primary,
            EventHandler onClick = null)
        {
            Color backColor;
            Color foreColor;

            switch (style)
            {
                case ButtonStyle.Primary:
                    backColor = AppTheme.Accent;
                    foreColor = Color.White;
                    break;
                case ButtonStyle.Success:
                    backColor = AppTheme.Success;
                    foreColor = Color.White;
                    break;
                case ButtonStyle.Danger:
                    backColor = AppTheme.Error;
                    foreColor = Color.White;
                    break;
                case ButtonStyle.Secondary:
                    backColor = AppTheme.Secondary;
                    foreColor = AppTheme.TextPrimary;
                    break;
                default:
                    backColor = AppTheme.Accent;
                    foreColor = Color.White;
                    break;
            }

            var btn = new Button
            {
                Text = text,
                Location = new Point(x, y),
                Size = new Size(width, height),
                FlatStyle = FlatStyle.Flat,
                BackColor = backColor,
                ForeColor = foreColor,
                Font = AppTheme.ButtonFont,
                Cursor = Cursors.Hand
            };

            btn.FlatAppearance.BorderSize = style == ButtonStyle.Secondary ? 1 : 0;
            btn.FlatAppearance.BorderColor = AppTheme.InputBorder;
            btn.FlatAppearance.MouseOverBackColor = style == ButtonStyle.Secondary
                ? AppTheme.Accent
                : ControlPaint.Light(backColor, 0.2f);

            if (onClick != null)
                btn.Click += onClick;

            return btn;
        }

        /// <summary>
        /// Creates a navigation button
        /// </summary>
        public static Button CreateNavButton(string text, int panelIndex, bool isActive, Action<int> onClick)
        {
            var btn = new Button
            {
                Text = text,
                Size = new Size(160, 35),
                FlatStyle = FlatStyle.Flat,
                ForeColor = AppTheme.TextPrimary,
                BackColor = isActive ? AppTheme.Accent : AppTheme.Secondary,
                Font = AppTheme.DefaultFont,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(8, 0, 0, 0),
                Cursor = Cursors.Hand,
                Tag = panelIndex
            };
            btn.FlatAppearance.BorderSize = 0;
            btn.FlatAppearance.MouseOverBackColor = AppTheme.Accent;
            btn.Click += (s, e) => onClick?.Invoke(panelIndex);
            return btn;
        }

        /// <summary>
        /// Creates a styled TreeView
        /// </summary>
        public static TreeView CreateTreeView(int x, int y, int width, int height)
        {
            return new TreeView
            {
                Location = new Point(x, y),
                Size = new Size(width, height),
                BackColor = AppTheme.InputBackground,
                ForeColor = AppTheme.TextPrimary,
                BorderStyle = BorderStyle.FixedSingle,
                Font = AppTheme.MonoFont
            };
        }

        /// <summary>
        /// Creates a styled log TextBox
        /// </summary>
        public static TextBox CreateLogTextBox(int x, int y, int width, int height)
        {
            return new TextBox
            {
                Location = new Point(x, y),
                Size = new Size(width, height),
                BackColor = AppTheme.InputBackground,
                ForeColor = AppTheme.TextSecondary,
                BorderStyle = BorderStyle.FixedSingle,
                Font = AppTheme.LogFont,
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                ReadOnly = true
            };
        }

        /// <summary>
        /// Creates a styled panel
        /// </summary>
        public static Panel CreatePanel(int x, int y, int width, int height, Color? backColor = null)
        {
            return new Panel
            {
                Location = new Point(x, y),
                Size = new Size(width, height),
                BackColor = backColor ?? AppTheme.Primary
            };
        }

        /// <summary>
        /// Creates a horizontal separator line
        /// </summary>
        public static Panel CreateSeparator(int x, int y, int width)
        {
            return new Panel
            {
                Location = new Point(x, y),
                Size = new Size(width, 1),
                BackColor = AppTheme.InputBorder
            };
        }

        /// <summary>
        /// Creates a styled LinkLabel
        /// </summary>
        public static LinkLabel CreateLink(string text, int x, int y, EventHandler onClick = null)
        {
            var link = new LinkLabel
            {
                Text = text,
                AutoSize = true,
                Location = new Point(x, y),
                LinkColor = AppTheme.Accent,
                ActiveLinkColor = Color.White,
                Font = AppTheme.DefaultFont
            };

            if (onClick != null)
                link.LinkClicked += (s, e) => onClick(s, e);

            return link;
        }

        /// <summary>
        /// Creates a styled ProgressBar
        /// </summary>
        public static ProgressBar CreateProgressBar(int x, int y, int width, int height)
        {
            return new ProgressBar
            {
                Location = new Point(x, y),
                Size = new Size(width, height),
                Style = ProgressBarStyle.Marquee,
                Visible = false
            };
        }
    }

    public enum ButtonStyle
    {
        Primary,
        Secondary,
        Success,
        Danger
    }
}
