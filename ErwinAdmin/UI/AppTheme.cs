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
        public static readonly Color FormBackground = SystemColors.Control;
        public static readonly Color PanelBackground = Color.White;
        public static readonly Color GroupBoxBackground = Color.White;

        // Status Colors
        public static readonly Color Success = Color.DarkGreen;
        public static readonly Color Error = Color.Red;
        public static readonly Color Warning = Color.FromArgb(200, 140, 0);
        public static readonly Color Info = Color.DarkBlue;

        // Text Colors - Dark text on light background
        public static readonly Color TextPrimary = Color.Black;
        public static readonly Color TextSecondary = Color.FromArgb(80, 80, 80);
        public static readonly Color TextDisabled = Color.Gray;

        // Input Colors - Standard Windows controls
        public static readonly Color InputBackground = Color.White;
        public static readonly Color InputBorder = Color.FromArgb(180, 180, 180);
        public static readonly Color InputFocus = Color.FromArgb(0, 122, 204);

        // Catalog Entry Colors - for TreeView on light background
        public static readonly Color LibraryColor = Color.FromArgb(0, 100, 180);      // Dark Blue
        public static readonly Color CategoryColor = Color.FromArgb(160, 120, 0);     // Dark Gold
        public static readonly Color ModelGroupColor = Color.FromArgb(120, 80, 160);  // Purple - model container
        public static readonly Color ModelColor = Color.DarkGreen;                     // Green - loadable model

        // Fonts - standard sizes
        public static readonly Font DefaultFont = new("Segoe UI", 9F);
        public static readonly Font TitleFont = new("Segoe UI Semibold", 12F);
        public static readonly Font SubtitleFont = new("Segoe UI Semibold", 10F);
        public static readonly Font ButtonFont = new("Segoe UI", 9F);
        public static readonly Font SmallFont = new("Segoe UI", 8F);
        public static readonly Font MonoFont = new("Consolas", 9F);
        public static readonly Font LogFont = new("Consolas", 9F);
        public static readonly Font TreeFont = new("Segoe UI", 9F);

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
    }
}
