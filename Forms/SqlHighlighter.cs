using System.Drawing;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace EliteSoft.Erwin.AddIn.Forms
{
    /// <summary>
    /// VS-Code-flavoured SQL syntax highlighting for a <see cref="RichTextBox"/>.
    /// Extracted from <c>ModelConfigForm.ApplySqlHighlighting</c> so the
    /// DDL approval popup and any future SQL viewer can share the same colour
    /// palette without copy-paste drift. The DDL Generation tab, Alter Compare
    /// tab, and DdlApprovalDialog all call into this single implementation.
    /// </summary>
    internal static class SqlHighlighter
    {
        public static void Apply(RichTextBox rtb, string sql)
        {
            if (rtb == null) return;
            rtb.SuspendLayout();
            rtb.Clear();
            // Pad with trailing blank lines so the last real line isn't clipped
            // at the bottom of the RichTextBox viewport when the user scrolls
            // all the way down (common RTB rendering issue).
            rtb.Text = (sql ?? string.Empty) + "\n\n\n";

            rtb.SelectAll();
            rtb.SelectionColor = Color.FromArgb(220, 220, 220);

            // RichTextBox converts \r\n to \n internally; use its own text
            // for regex offsets so Select(index,length) lands correctly.
            string rtbText = rtb.Text;

            var clrKeyword    = Color.FromArgb( 86, 156, 214);   // VS Code blue
            var clrType       = Color.FromArgb( 78, 201, 176);   // VS Code teal
            var clrComment    = Color.FromArgb(106, 153,  85);   // VS Code green
            var clrString     = Color.FromArgb(206, 145, 120);   // VS Code orange
            var clrNumber     = Color.FromArgb(181, 206, 168);   // VS Code light green
            var clrGo         = Color.FromArgb(197, 134, 192);   // VS Code purple
            var clrDiffNew    = Color.FromArgb( 80, 220,  80);
            var clrDiffDrop   = Color.FromArgb(240,  80,  80);
            var clrDiffChange = Color.FromArgb(255, 180,  50);
            var clrSection    = Color.FromArgb(220, 220, 100);

            HighlightRegex(rtb, rtbText, @"\b(CREATE|ALTER|DROP|TABLE|ADD|COLUMN|CONSTRAINT|PRIMARY|KEY|FOREIGN|REFERENCES|NOT|NULL|DEFAULT|IDENTITY|CLUSTERED|NONCLUSTERED|INDEX|UNIQUE|ON|DELETE|UPDATE|CASCADE|SET|CHECK|WITH|ASC|DESC|BEGIN|END|DECLARE|IF|EXISTS|SELECT|FROM|WHERE|AND|OR|RETURN|GOTO|TRIGGER|FOR|INSERT|AS|RAISERROR|ROLLBACK|TRANSACTION|INTO|ACTION)\b", clrKeyword);
            HighlightRegex(rtb, rtbText, @"\b(int|bigint|smallint|tinyint|bit|varchar|nvarchar|char|nchar|text|ntext|datetime|smalldatetime|date|time|timestamp|decimal|numeric|float|real|money|smallmoney|varbinary|binary|image|uniqueidentifier|VARCHAR2|NUMBER|CLOB|BLOB|COLLATE)\b", clrType);
            HighlightRegex(rtb, rtbText, @"(?<![a-zA-Z_])\d+(?![a-zA-Z_])", clrNumber);
            HighlightRegex(rtb, rtbText, @"(?m)^go$", clrGo);
            HighlightRegex(rtb, rtbText, @"'[^']*'", clrString);
            HighlightRegex(rtb, rtbText, @"--[^\n]*", clrComment);
            HighlightRegex(rtb, rtbText, @"-- NEW:.*", clrDiffNew);
            HighlightRegex(rtb, rtbText, @"-- DROPPED:.*", clrDiffDrop);
            HighlightRegex(rtb, rtbText, @"-- CHANGED:.*", clrDiffChange);
            HighlightRegex(rtb, rtbText, @"-- =+.*=+", clrSection);
            HighlightRegex(rtb, rtbText, @"-- Summary:.*", clrSection);
            HighlightRegex(rtb, rtbText, @"-- WARNING:.*", clrDiffDrop);

            rtb.SelectionStart = 0;
            rtb.SelectionLength = 0;
            rtb.ResumeLayout();
        }

        private static void HighlightRegex(RichTextBox rtb, string rtbText, string pattern, Color color)
        {
            try
            {
                foreach (Match m in Regex.Matches(rtbText, pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline))
                {
                    rtb.Select(m.Index, m.Length);
                    rtb.SelectionColor = color;
                }
            }
            catch { /* highlighting is best-effort; never break the viewer */ }
        }
    }
}
