using System;
using System.Globalization;
using System.Text;

namespace ScreenLens.WinUI.Views.Result
{
    // All reading measurements are DIPs; placement bounds are physical pixels.
    // Only used at creation: streaming translation never resizes the window.
    internal static class ResultWindowLayout
    {
        internal const double ComparisonWidth = 840;

        internal static (double Width, double Height) InitialSize(
            string text, double fontSize, double workWidth, double workHeight)
        {
            var maxWidth = Math.Max(1, Math.Min(780, workWidth * 0.9));
            var maxHeight = Math.Max(1, workHeight * 0.9);
            var longest = 0.0;
            var lineWidth = 0.0;
            foreach (var rune in text.EnumerateRunes())
            {
                if (rune.Value == '\n')
                {
                    longest = Math.Max(longest, lineWidth);
                    lineWidth = 0;
                }
                else lineWidth += GlyphWidth(rune, fontSize);
            }
            longest = Math.Max(longest, lineWidth);
            var width = Math.Min(maxWidth, Math.Max(420, longest + 64));
            var textWidth = Math.Max(fontSize, width - 64);
            var lines = 1;
            lineWidth = 0;
            foreach (var rune in text.EnumerateRunes())
            {
                if (rune.Value == '\n')
                {
                    lines++;
                    lineWidth = 0;
                }
                else
                {
                    lineWidth += GlyphWidth(rune, fontSize);
                    if (lineWidth > textWidth)
                    {
                        lines++;
                        lineWidth = GlyphWidth(rune, fontSize);
                    }
                }
                // More text will scroll; do not spend time measuring offscreen lines.
                if (lines * fontSize * 1.55 + 230 >= maxHeight) break;
            }
            return (width, Math.Min(maxHeight, Math.Max(330, lines * fontSize * 1.55 + 230)));
        }

        private static double GlyphWidth(Rune rune, double fontSize)
        {
            if (rune.Value == '\r' || Rune.GetUnicodeCategory(rune) is
                UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format)
                return 0;
            if (rune.Value == '\t') return fontSize * 2.4;
            return fontSize * (rune.Value >= 0x2E80 ? 1.0 : 0.6);
        }

        internal static ResultBounds FitToWorkArea(ResultBounds desired, ResultBounds work)
        {
            var width = Math.Clamp(desired.Width, 1, Math.Max(1, work.Width));
            var height = Math.Clamp(desired.Height, 1, Math.Max(1, work.Height));
            return new ResultBounds(
                Math.Clamp(desired.X, work.X, work.X + work.Width - width),
                Math.Clamp(desired.Y, work.Y, work.Y + work.Height - height), width, height);
        }

        internal static bool CanCompare(double width) => width >= ComparisonWidth;
    }

    internal readonly record struct ResultBounds(int X, int Y, int Width, int Height);
}
