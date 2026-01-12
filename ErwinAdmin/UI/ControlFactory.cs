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
        /// Creates a title label (larger, bolder)
        /// </summary>
        public static Label CreateTitle(string text, int x, int y)
        {
            return CreateLabel(text, x, y, AppTheme.TitleFont, AppTheme.TextPrimary);
        }

        /// <summary>
        /// Creates a section title label
        /// </summary>
        public static Label CreateSectionTitle(string text, int x, int y)
        {
            return CreateLabel(text, x, y, AppTheme.SectionFont, AppTheme.TextPrimary);
        }

        /// <summary>
        /// Creates a description/help text label
        /// </summary>
        public static Label CreateDescription(string text, int x, int y, int maxWidth = 0)
        {
            var lbl = CreateLabel(text, x, y, AppTheme.SmallFont, AppTheme.TextMuted);
            if (maxWidth > 0)
            {
                lbl.AutoSize = false;
                lbl.Size = new Size(maxWidth, 40);
            }
            return lbl;
        }

        /// <summary>
        /// Creates a styled textbox
        /// </summary>
        public static TextBox CreateTextBox(int x, int y, int width, string defaultValue = "", bool isPassword = false)
        {
            var txt = new TextBox
            {
                Location = new Point(x, y),
                Size = new Size(width, 28),
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
        /// Creates a styled button with icon support
        /// </summary>
        public static Button CreateButton(
            string text,
            int x,
            int y,
            int width,
            int height,
            ButtonStyle style = ButtonStyle.Primary,
            EventHandler onClick = null,
            string icon = null)
        {
            var displayText = string.IsNullOrEmpty(icon) ? text : $"{icon} {text}";

            var btn = new Button
            {
                Text = displayText,
                Location = new Point(x, y),
                Size = new Size(width, height),
                Font = AppTheme.ButtonFont,
                Cursor = Cursors.Hand,
                FlatStyle = FlatStyle.Flat
            };

            ApplyButtonStyle(btn, style);

            if (onClick != null)
                btn.Click += onClick;

            return btn;
        }

        /// <summary>
        /// Applies visual style to a button based on ButtonStyle
        /// </summary>
        public static void ApplyButtonStyle(Button btn, ButtonStyle style)
        {
            btn.FlatStyle = FlatStyle.Flat;
            btn.FlatAppearance.BorderSize = 1;

            switch (style)
            {
                case ButtonStyle.Primary:
                    btn.BackColor = AppTheme.Accent;
                    btn.ForeColor = Color.White;
                    btn.FlatAppearance.BorderColor = AppTheme.Accent;
                    break;
                case ButtonStyle.Secondary:
                    btn.BackColor = AppTheme.PanelBackground;
                    btn.ForeColor = AppTheme.TextPrimary;
                    btn.FlatAppearance.BorderColor = AppTheme.Border;
                    break;
                case ButtonStyle.Success:
                    btn.BackColor = AppTheme.Success;
                    btn.ForeColor = Color.White;
                    btn.FlatAppearance.BorderColor = AppTheme.Success;
                    break;
                case ButtonStyle.Danger:
                    btn.BackColor = AppTheme.Error;
                    btn.ForeColor = Color.White;
                    btn.FlatAppearance.BorderColor = AppTheme.Error;
                    break;
            }
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
                Font = AppTheme.TreeFont,
                ItemHeight = 24,
                ShowLines = true,
                ShowPlusMinus = true,
                ShowRootLines = true,
                HotTracking = true,
                FullRowSelect = true
            };
        }

        /// <summary>
        /// Creates a styled log TextBox (collapsible style)
        /// </summary>
        public static TextBox CreateLogTextBox(int x, int y, int width, int height)
        {
            return new TextBox
            {
                Location = new Point(x, y),
                Size = new Size(width, height),
                BackColor = Color.FromArgb(30, 30, 30),
                ForeColor = Color.FromArgb(200, 200, 200),
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
                BackColor = backColor ?? AppTheme.PanelBackground
            };
        }

        /// <summary>
        /// Creates a card panel with border and shadow effect
        /// </summary>
        public static Panel CreateCard(int x, int y, int width, int height, string title = null)
        {
            var card = new Panel
            {
                Location = new Point(x, y),
                Size = new Size(width, height),
                BackColor = AppTheme.CardBackground,
                BorderStyle = BorderStyle.FixedSingle
            };

            if (!string.IsNullOrEmpty(title))
            {
                var titleLabel = CreateSectionTitle(title, AppTheme.Spacing.LG, AppTheme.Spacing.MD);
                card.Controls.Add(titleLabel);

                var separator = CreateSeparator(0, 40, width);
                card.Controls.Add(separator);
            }

            return card;
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
                BackColor = AppTheme.Divider
            };
        }

        /// <summary>
        /// Creates a styled LinkLabel
        /// </summary>
        public static LinkLabel CreateLink(string text, int x, int y, EventHandler onClick = null, string icon = null)
        {
            var displayText = string.IsNullOrEmpty(icon) ? text : $"{icon} {text}";

            var link = new LinkLabel
            {
                Text = displayText,
                AutoSize = true,
                Location = new Point(x, y),
                LinkColor = AppTheme.Accent,
                ActiveLinkColor = AppTheme.FocusBorder,
                VisitedLinkColor = AppTheme.Accent,
                Font = AppTheme.DefaultFont,
                Cursor = Cursors.Hand
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
                MarqueeAnimationSpeed = 30,
                Visible = false
            };
        }

        /// <summary>
        /// Creates a status label with icon and colored background
        /// </summary>
        public static Label CreateStatusLabel(int x, int y, int width, int height = 32)
        {
            return new Label
            {
                Location = new Point(x, y),
                Size = new Size(width, height),
                Font = AppTheme.DefaultFont,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(AppTheme.Spacing.SM, 0, AppTheme.Spacing.SM, 0),
                AutoSize = false
            };
        }

        /// <summary>
        /// Updates a status label with formatted message and appropriate styling
        /// </summary>
        public static void SetStatus(Label label, string message, StatusType type)
        {
            var (foreground, _) = AppTheme.GetStatusColors(type);
            // Minimal status: icon + text, no background
            string icon = type switch
            {
                StatusType.Success => "✓",
                StatusType.Error => "✗",
                StatusType.Warning => "⚠",
                StatusType.Info => "ℹ",
                _ => ""
            };
            label.Text = string.IsNullOrEmpty(icon) ? message : $"{icon} {message}";
            label.ForeColor = foreground;
            label.BackColor = Color.Transparent;
        }

        /// <summary>
        /// Creates a search TextBox with placeholder
        /// </summary>
        public static TextBox CreateSearchBox(int x, int y, int width, string placeholder = "Search...")
        {
            var txt = new TextBox
            {
                Location = new Point(x, y),
                Size = new Size(width, 28),
                BackColor = AppTheme.InputBackground,
                ForeColor = AppTheme.TextMuted,
                BorderStyle = BorderStyle.FixedSingle,
                Font = AppTheme.DefaultFont,
                Text = placeholder
            };

            txt.GotFocus += (s, e) =>
            {
                if (txt.Text == placeholder)
                {
                    txt.Text = "";
                    txt.ForeColor = AppTheme.TextPrimary;
                }
            };

            txt.LostFocus += (s, e) =>
            {
                if (string.IsNullOrWhiteSpace(txt.Text))
                {
                    txt.Text = placeholder;
                    txt.ForeColor = AppTheme.TextMuted;
                }
            };

            return txt;
        }

        /// <summary>
        /// Creates a ComboBox with consistent styling
        /// </summary>
        public static ComboBox CreateComboBox(int x, int y, int width)
        {
            return new ComboBox
            {
                Location = new Point(x, y),
                Size = new Size(width, 28),
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = AppTheme.DefaultFont,
                BackColor = AppTheme.InputBackground,
                ForeColor = AppTheme.TextPrimary
            };
        }

        /// <summary>
        /// Creates a styled CheckBox
        /// </summary>
        public static CheckBox CreateCheckBox(string text, int x, int y, bool isChecked = false)
        {
            return new CheckBox
            {
                Text = text,
                Location = new Point(x, y),
                AutoSize = true,
                Font = AppTheme.DefaultFont,
                ForeColor = AppTheme.TextPrimary,
                Checked = isChecked,
                Cursor = Cursors.Hand
            };
        }

        /// <summary>
        /// Creates a styled DataGridView
        /// </summary>
        public static DataGridView CreateDataGrid(int x, int y, int width, int height)
        {
            var grid = new DataGridView
            {
                Location = new Point(x, y),
                Size = new Size(width, height),
                BackgroundColor = AppTheme.InputBackground,
                BorderStyle = BorderStyle.FixedSingle,
                Font = AppTheme.DefaultFont,
                RowHeadersVisible = false,
                AllowUserToResizeRows = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2,
                GridColor = AppTheme.Border,
                CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
                ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single,
                EnableHeadersVisualStyles = false
            };

            // Header style
            grid.ColumnHeadersDefaultCellStyle.BackColor = AppTheme.Secondary;
            grid.ColumnHeadersDefaultCellStyle.ForeColor = AppTheme.TextPrimary;
            grid.ColumnHeadersDefaultCellStyle.Font = AppTheme.SectionFont;
            grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(AppTheme.Spacing.SM);
            grid.ColumnHeadersHeight = 36;

            // Row style
            grid.DefaultCellStyle.BackColor = AppTheme.InputBackground;
            grid.DefaultCellStyle.ForeColor = AppTheme.TextPrimary;
            grid.DefaultCellStyle.SelectionBackColor = AppTheme.Accent;
            grid.DefaultCellStyle.SelectionForeColor = Color.White;
            grid.DefaultCellStyle.Padding = new Padding(AppTheme.Spacing.SM);
            grid.RowTemplate.Height = 32;

            // Alternating rows
            grid.AlternatingRowsDefaultCellStyle.BackColor = AppTheme.Secondary;

            return grid;
        }

        /// <summary>
        /// Creates a styled ListView
        /// </summary>
        public static ListView CreateListView(int x, int y, int width, int height)
        {
            var list = new ListView
            {
                Location = new Point(x, y),
                Size = new Size(width, height),
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                BackColor = AppTheme.InputBackground,
                ForeColor = AppTheme.TextPrimary,
                Font = AppTheme.DefaultFont,
                BorderStyle = BorderStyle.FixedSingle
            };

            return list;
        }

        /// <summary>
        /// Applies disabled styling to a control
        /// </summary>
        public static void SetDisabledStyle(Control control, bool isDisabled)
        {
            control.Enabled = !isDisabled;

            if (control is Button btn)
            {
                if (isDisabled)
                {
                    btn.BackColor = AppTheme.DisabledBackground;
                    btn.ForeColor = AppTheme.DisabledForeground;
                    btn.FlatAppearance.BorderColor = AppTheme.DisabledBorder;
                    btn.Cursor = Cursors.Default;
                }
            }
            else if (control is TextBox txt)
            {
                txt.BackColor = isDisabled ? AppTheme.DisabledBackground : AppTheme.InputBackground;
                txt.ForeColor = isDisabled ? AppTheme.DisabledForeground : AppTheme.TextPrimary;
            }
        }

        /// <summary>
        /// Creates a GroupBox with styled header
        /// </summary>
        public static GroupBox CreateGroupBox(string text, int x, int y, int width, int height)
        {
            return new GroupBox
            {
                Text = text,
                Location = new Point(x, y),
                Size = new Size(width, height),
                Font = AppTheme.SectionFont,
                ForeColor = AppTheme.TextPrimary,
                BackColor = AppTheme.PanelBackground
            };
        }

        /// <summary>
        /// Creates an accordion/collapsible panel with header and optional checkbox
        /// </summary>
        public static (Panel container, Panel header, Panel content, CheckBox headerCheckbox, Label toggleIcon) CreateAccordion(
            string title, int x, int y, int width, int collapsedHeight, int expandedHeight,
            bool startExpanded = true, bool hasCheckbox = false, bool checkboxChecked = true)
        {
            var container = new Panel
            {
                Location = new Point(x, y),
                Size = new Size(width, startExpanded ? expandedHeight : collapsedHeight),
                BackColor = AppTheme.CardBackground,
                BorderStyle = BorderStyle.FixedSingle,
                Tag = new AccordionState { CollapsedHeight = collapsedHeight, ExpandedHeight = expandedHeight, IsExpanded = startExpanded }
            };

            // Header panel (clickable)
            var header = new Panel
            {
                Location = new Point(0, 0),
                Size = new Size(width - 2, collapsedHeight - 2),
                BackColor = AppTheme.SidebarBackground,
                Cursor = Cursors.Hand
            };

            // Toggle icon (▼ or ▶)
            var toggleIcon = new Label
            {
                Text = startExpanded ? "▼" : "▶",
                Location = new Point(AppTheme.Spacing.SM, (collapsedHeight - 20) / 2),
                AutoSize = true,
                Font = new Font(AppTheme.DefaultFont.FontFamily, 8f),
                ForeColor = AppTheme.TextMuted,
                Cursor = Cursors.Hand
            };
            header.Controls.Add(toggleIcon);

            // Header checkbox (includes title)
            var headerCheckbox = new CheckBox
            {
                Text = title,
                Location = new Point(AppTheme.Spacing.SM + 20, (collapsedHeight - 22) / 2),
                AutoSize = true,
                Font = AppTheme.SectionFont,
                ForeColor = AppTheme.TextPrimary,
                Checked = hasCheckbox ? checkboxChecked : true,
                Cursor = Cursors.Hand,
                // If no checkbox functionality needed, make it look like a label
                Appearance = hasCheckbox ? Appearance.Normal : Appearance.Normal
            };

            if (!hasCheckbox)
            {
                // Hide checkbox visual, just show text
                headerCheckbox.AutoCheck = false;
                headerCheckbox.FlatStyle = FlatStyle.Flat;
            }

            header.Controls.Add(headerCheckbox);
            container.Controls.Add(header);

            // Content panel
            var content = new Panel
            {
                Location = new Point(0, collapsedHeight),
                Size = new Size(width - 2, expandedHeight - collapsedHeight),
                BackColor = AppTheme.CardBackground,
                Visible = startExpanded,
                Padding = new Padding(AppTheme.Spacing.SM)
            };
            container.Controls.Add(content);

            // Helper to expand/collapse
            Action<bool> setExpanded = (expand) =>
            {
                var state = (AccordionState)container.Tag;
                state.IsExpanded = expand;
                content.Visible = expand;
                container.Height = expand ? state.ExpandedHeight : state.CollapsedHeight;
                toggleIcon.Text = expand ? "▼" : "▶";
            };

            // Click handlers for toggle (on icon and header background only)
            EventHandler toggleHandler = (s, e) =>
            {
                var state = (AccordionState)container.Tag;
                setExpanded(!state.IsExpanded);
            };

            header.Click += toggleHandler;
            toggleIcon.Click += toggleHandler;

            // Checkbox state does NOT affect accordion expand/collapse
            // User can toggle accordion independently by clicking header or arrow
            if (!hasCheckbox)
            {
                // If no checkbox, clicking title also toggles
                headerCheckbox.Click += toggleHandler;
            }

            return (container, header, content, headerCheckbox, toggleIcon);
        }

        /// <summary>
        /// Updates accordion expanded height dynamically
        /// </summary>
        public static void SetAccordionExpandedHeight(Panel container, int newExpandedHeight)
        {
            if (container.Tag is AccordionState state)
            {
                state.ExpandedHeight = newExpandedHeight;
                if (state.IsExpanded)
                {
                    container.Height = newExpandedHeight;
                    var content = container.Controls[1] as Panel;
                    if (content != null)
                    {
                        content.Height = newExpandedHeight - state.CollapsedHeight;
                    }
                }
            }
        }
    }

    /// <summary>
    /// State holder for accordion panels
    /// </summary>
    public class AccordionState
    {
        public int CollapsedHeight { get; set; }
        public int ExpandedHeight { get; set; }
        public bool IsExpanded { get; set; }
    }

    public enum ButtonStyle
    {
        Primary,
        Secondary,
        Success,
        Danger
    }
}
