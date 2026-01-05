using System.Drawing;

namespace EliteSoft.Erwin.Admin.UI
{
    /// <summary>
    /// Application theme colors and styles
    /// </summary>
    public static class AppTheme
    {
        // Primary Colors
        public static readonly Color Primary = Color.FromArgb(45, 45, 48);
        public static readonly Color Secondary = Color.FromArgb(60, 60, 65);
        public static readonly Color Accent = Color.FromArgb(0, 122, 204);

        // Status Colors
        public static readonly Color Success = Color.FromArgb(46, 160, 67);
        public static readonly Color Error = Color.FromArgb(215, 58, 73);
        public static readonly Color Warning = Color.FromArgb(227, 179, 65);
        public static readonly Color Info = Color.FromArgb(86, 156, 214);

        // Text Colors
        public static readonly Color TextPrimary = Color.FromArgb(241, 241, 241);
        public static readonly Color TextSecondary = Color.FromArgb(150, 150, 150);
        public static readonly Color TextDisabled = Color.FromArgb(100, 100, 100);

        // Input Colors
        public static readonly Color InputBackground = Color.FromArgb(37, 37, 38);
        public static readonly Color InputBorder = Color.FromArgb(70, 70, 75);
        public static readonly Color InputFocus = Color.FromArgb(0, 122, 204);

        // Catalog Entry Colors
        public static readonly Color LibraryColor = Color.FromArgb(86, 156, 214);    // Blue
        public static readonly Color CategoryColor = Color.FromArgb(220, 180, 100);  // Yellow/Gold
        public static readonly Color ModelGroupColor = Color.FromArgb(180, 140, 200); // Purple - model container
        public static readonly Color ModelColor = Color.FromArgb(46, 160, 67);       // Green - loadable model

        // Fonts - increased sizes for better readability
        public static readonly Font DefaultFont = new("Segoe UI", 10F);
        public static readonly Font TitleFont = new("Segoe UI Semibold", 16F);
        public static readonly Font SubtitleFont = new("Segoe UI Semibold", 12F);
        public static readonly Font ButtonFont = new("Segoe UI Semibold", 10F);
        public static readonly Font SmallFont = new("Segoe UI", 9F);
        public static readonly Font MonoFont = new("Consolas", 10F);
        public static readonly Font LogFont = new("Consolas", 9F);
        public static readonly Font TreeFont = new("Segoe UI", 11F);

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
