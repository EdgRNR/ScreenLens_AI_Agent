using System;

namespace ScreenLens.WinUI.Views.Capture;

[Flags]
internal enum SelectionEdit
{
    None = 0,
    Left = 1,
    Right = 2,
    Top = 4,
    Bottom = 8,
    Move = 16,
    TopLeft = Left | Top,
    TopRight = Right | Top,
    BottomLeft = Left | Bottom,
    BottomRight = Right | Bottom,
}

internal readonly record struct SelectionBounds(double X, double Y, double Width, double Height)
{
    internal double Right => X + Width;
    internal double Bottom => Y + Height;
}

/// <summary>Geometry only: hit tests use DIPs; edits use physical screenshot pixels.</summary>
internal static class RectangleSelectionEditor
{
    internal static SelectionEdit HitTest(SelectionBounds rect, double x, double y,
        double tolerance)
    {
        if (rect.Width <= 0 || rect.Height <= 0) return SelectionEdit.None;
        var hit = SelectionEdit.None;
        var closest = double.PositiveInfinity;
        // Corners take priority over edges, including overlapping hit areas on tiny selections.
        for (var i = 0; i < 8; i++)
        {
            var (hx, hy, part) = HandleAt(rect, i);
            var dx = Math.Abs(x - hx);
            var dy = Math.Abs(y - hy);
            if (dx <= tolerance && dy <= tolerance && dx * dx + dy * dy < closest)
            {
                closest = dx * dx + dy * dy;
                hit = part;
            }
        }
        if (hit != SelectionEdit.None) return hit;
        if (y >= rect.Y && y <= rect.Bottom)
        {
            if (Math.Abs(x - rect.X) <= tolerance) return SelectionEdit.Left;
            if (Math.Abs(x - rect.Right) <= tolerance) return SelectionEdit.Right;
        }
        if (x >= rect.X && x <= rect.Right)
        {
            if (Math.Abs(y - rect.Y) <= tolerance) return SelectionEdit.Top;
            if (Math.Abs(y - rect.Bottom) <= tolerance) return SelectionEdit.Bottom;
        }
        return x >= rect.X && x <= rect.Right && y >= rect.Y && y <= rect.Bottom
            ? SelectionEdit.Move : SelectionEdit.None;
    }

    internal static (double X, double Y, SelectionEdit Part) HandleAt(SelectionBounds r, int index)
        => index switch
        {
            0 => (r.X, r.Y, SelectionEdit.TopLeft),
            1 => (r.Right, r.Y, SelectionEdit.TopRight),
            2 => (r.X, r.Bottom, SelectionEdit.BottomLeft),
            3 => (r.Right, r.Bottom, SelectionEdit.BottomRight),
            4 => (r.X + r.Width / 2, r.Y, SelectionEdit.Top),
            5 => (r.Right, r.Y + r.Height / 2, SelectionEdit.Right),
            6 => (r.X + r.Width / 2, r.Bottom, SelectionEdit.Bottom),
            7 => (r.X, r.Y + r.Height / 2, SelectionEdit.Left),
            _ => throw new ArgumentOutOfRangeException(nameof(index)),
        };

    internal static SelectionBounds ToPixels(SelectionBounds r, double scaleX, double scaleY,
        double width, double height)
    {
        var left = Math.Clamp(Math.Round(r.X * scaleX), 0, width - 1);
        var top = Math.Clamp(Math.Round(r.Y * scaleY), 0, height - 1);
        // Match the displayed width/height, rather than rounding two endpoints separately.
        var right = Math.Clamp(left + Math.Round(r.Width * scaleX), left + 1, width);
        var bottom = Math.Clamp(top + Math.Round(r.Height * scaleY), top + 1, height);
        return new(left, top, right - left, bottom - top);
    }

    internal static SelectionBounds Drag(SelectionBounds start, SelectionEdit edit,
        double dx, double dy, double width, double height, double minWidth, double minHeight)
    {
        dx = Math.Round(dx);
        dy = Math.Round(dy);
        if (edit == SelectionEdit.Move)
            return start with
            {
                X = Math.Clamp(start.X + dx, 0, width - start.Width),
                Y = Math.Clamp(start.Y + dy, 0, height - start.Height),
            };

        var left = start.X;
        var top = start.Y;
        var right = start.Right;
        var bottom = start.Bottom;
        // Clamp at the opposite edge rather than flipping handles mid-drag.
        if ((edit & SelectionEdit.Left) != 0)
            left = Math.Clamp(left + dx, 0, right - Math.Min(minWidth, start.Width));
        if ((edit & SelectionEdit.Right) != 0)
            right = Math.Clamp(right + dx, left + Math.Min(minWidth, start.Width), width);
        if ((edit & SelectionEdit.Top) != 0)
            top = Math.Clamp(top + dy, 0, bottom - Math.Min(minHeight, start.Height));
        if ((edit & SelectionEdit.Bottom) != 0)
            bottom = Math.Clamp(bottom + dy, top + Math.Min(minHeight, start.Height), height);
        return new(left, top, right - left, bottom - top);
    }
}
