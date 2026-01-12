using System.Drawing;

namespace EliteSoft.Erwin.Admin.UI
{
    /// <summary>
    /// Application theme colors and styles - Light theme matching erwin Add-In
    /// </summary>
    public static class AppTheme
    {
        // Primary Colors - Light theme
        public static readonly Color Primary = Color.White;
        public static readonly Color Secondary = Color.FromArgb(245, 245, 245);
        public static readonly Color Accent = Color.FromArgb(0, 122, 204);

        // Background Colors
        public static readonly Color FormBackground = Color.FromArgb(243, 243, 243);
        public static readonly Color PanelBackground = Color.White;
        public static readonly Color GroupBoxBackground = Color.White;
        public static readonly Color SidebarBackground = Color.FromArgb(248, 249, 250);
        public static readonly Color CardBackground = Color.White;

        // Interactive State Colors
        public static readonly Color Hover = Color.FromArgb(235, 235, 235);
        public static readonly Color FocusBorder = Color.FromArgb(0, 122, 204);
        public static readonly Color ActiveIndicator = Color.FromArgb(0, 122, 204);

        // Disabled State Colors
        public static readonly Color DisabledBackground = Color.FromArgb(245, 245, 245);
        public static readonly Color DisabledForeground = Color.FromArgb(160, 160, 160);
        public static readonly Color DisabledBorder = Color.FromArgb(200, 200, 200);

        // Status Colors
        public static readonly Color Success = Color.FromArgb(40, 167, 69);
        public static readonly Color Error = Color.FromArgb(220, 53, 69);
        public static readonly Color Warning = Color.FromArgb(255, 193, 7);
        public static readonly Color Info = Color.FromArgb(23, 162, 184);

        // Status Background Colors (for status messages)
        public static readonly Color SuccessBackground = Color.FromArgb(232, 245, 233);
        public static readonly Color ErrorBackground = Color.FromArgb(255, 235, 238);
        public static readonly Color WarningBackground = Color.FromArgb(255, 248, 225);
        public static readonly Color InfoBackground = Color.FromArgb(227, 242, 253);

        // Text Colors - Dark text on light background
        public static readonly Color TextPrimary = Color.FromArgb(33, 37, 41);
        public static readonly Color TextSecondary = Color.FromArgb(108, 117, 125);
        public static readonly Color TextDisabled = Color.FromArgb(160, 160, 160);
        public static readonly Color TextMuted = Color.FromArgb(134, 142, 150);

        // Border Colors
        public static readonly Color Border = Color.FromArgb(222, 226, 230);
        public static readonly Color BorderLight = Color.FromArgb(233, 236, 239);
        public static readonly Color Divider = Color.FromArgb(222, 226, 230);

        // Input Colors - Standard Windows controls
        public static readonly Color InputBackground = Color.White;
        public static readonly Color InputBorder = Color.FromArgb(206, 212, 218);
        public static readonly Color InputFocus = Color.FromArgb(0, 122, 204);

        // Catalog Entry Colors - for TreeView on light background
        public static readonly Color LibraryColor = Color.FromArgb(0, 123, 255);      // Blue
        public static readonly Color CategoryColor = Color.FromArgb(253, 126, 20);    // Orange
        public static readonly Color ModelGroupColor = Color.FromArgb(111, 66, 193);  // Purple
        public static readonly Color ModelColor = Color.FromArgb(40, 167, 69);        // Green

        // Fonts - standard sizes
        public static readonly Font DefaultFont = new("Segoe UI", 9F);
        public static readonly Font TitleFont = new("Segoe UI Semibold", 14F);
        public static readonly Font SubtitleFont = new("Segoe UI Semibold", 11F);
        public static readonly Font SectionFont = new("Segoe UI Semibold", 10F);
        public static readonly Font ButtonFont = new("Segoe UI", 9F);
        public static readonly Font SmallFont = new("Segoe UI", 8F);
        public static readonly Font MonoFont = new("Consolas", 9F);
        public static readonly Font LogFont = new("Consolas", 8.5F);
        public static readonly Font TreeFont = new("Segoe UI", 9F);

        // Spacing constants (in pixels)
        public static class Spacing
        {
            public const int XS = 4;
            public const int SM = 8;
            public const int MD = 12;
            public const int LG = 16;
            public const int XL = 24;
            public const int XXL = 32;
        }

        // Status Icons (Unicode)
        public static class Icons
        {
            public const string Success = "\u2713";  // ✓
            public const string Error = "\u2717";    // ✗
            public const string Warning = "\u26A0";  // ⚠
            public const string Info = "\u2139";     // ℹ
            public const string Loading = "\u23F3"; // ⏳
            public const string Folder = "\uD83D\uDCC1";  // 📁
            public const string FolderOpen = "\uD83D\uDCC2"; // 📂
            public const string File = "\uD83D\uDCC4";    // 📄
            public const string Database = "\uD83D\uDDC4"; // 🗄
            public const string Connect = "\uD83D\uDD17"; // 🔗
            public const string Save = "\uD83D\uDCBE";    // 💾
            public const string Refresh = "\uD83D\uDD04"; // 🔄
            public const string Search = "\uD83D\uDD0D";  // 🔍
        }

        /// <summary>
        /// Gets the color for a catalog entry type
        /// </summary>
        public static Color GetCatalogEntryColor(string entryType) => entryType switch
        {
            "L" or "Library" => LibraryColor,
            "C" or "Category" => CategoryColor,
            "D" or "ModelGroup" => ModelGroupColor,
            "V" or "O" or "MODEL" or "Model" => ModelColor,
            _ => TextPrimary
        };

        /// <summary>
        /// Gets status message with icon prefix
        /// </summary>
        public static string FormatStatusMessage(string message, StatusType type) => type switch
        {
            StatusType.Success => $"{Icons.Success} {message}",
            StatusType.Error => $"{Icons.Error} {message}",
            StatusType.Warning => $"{Icons.Warning} {message}",
            StatusType.Info => $"{Icons.Info} {message}",
            _ => message
        };

        /// <summary>
        /// Gets the appropriate colors for a status type
        /// </summary>
        public static (Color foreground, Color background) GetStatusColors(StatusType type) => type switch
        {
            StatusType.Success => (Success, SuccessBackground),
            StatusType.Error => (Error, ErrorBackground),
            StatusType.Warning => (Warning, WarningBackground),
            StatusType.Info => (Info, InfoBackground),
            _ => (TextPrimary, PanelBackground)
        };
    }

    public enum StatusType
    {
        None,
        Success,
        Error,
        Warning,
        Info
    }
}
