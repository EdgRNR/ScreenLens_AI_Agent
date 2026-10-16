using System;
using ScreenLens.WinUI.Views.Capture;

var rect = new SelectionBounds(100, 80, 300, 200);
foreach (var (x, y, expected) in new[]
{
    (100.0, 80.0, SelectionEdit.TopLeft), (400.0, 80.0, SelectionEdit.TopRight),
    (100.0, 280.0, SelectionEdit.BottomLeft), (400.0, 280.0, SelectionEdit.BottomRight),
    (170.0, 79.0, SelectionEdit.Top), (401.0, 140.0, SelectionEdit.Right),
    (170.0, 281.0, SelectionEdit.Bottom), (99.0, 140.0, SelectionEdit.Left),
    (250.0, 180.0, SelectionEdit.Move), (40.0, 80.0, SelectionEdit.None),
    (250.0, 300.0, SelectionEdit.None),
})
    Require(RectangleSelectionEditor.HitTest(rect, x, y, 8) == expected, "correct hover/edit target");

Require(RectangleSelectionEditor.HitTest(new(100, 80, 8, 8), 108, 88, 8)
    == SelectionEdit.BottomRight, "tiny selections choose the nearest handle");
Require(RectangleSelectionEditor.ToPixels(new(100.4, 80.4, 20.4, 30.4), 1, 1, 1920, 1080)
    == new SelectionBounds(100, 80, 20, 30),
    "fractional pointer positions export the same dimensions as the size inputs");

Require(Edit(rect, SelectionEdit.Left, 30, 0) == new SelectionBounds(130, 80, 270, 200),
    "left edge preserves the opposite edge");
Require(Edit(rect, SelectionEdit.Top, 0, -20) == new SelectionBounds(100, 60, 300, 220),
    "top edge preserves the opposite edge");
Require(Edit(rect, SelectionEdit.Right, 30, 900) == new SelectionBounds(100, 80, 330, 200),
    "right edge ignores vertical displacement");
Require(Edit(rect, SelectionEdit.Bottom, 900, -20) == new SelectionBounds(100, 80, 300, 180),
    "bottom edge ignores horizontal displacement");
Require(Edit(rect, SelectionEdit.TopLeft, 20, 30) == new SelectionBounds(120, 110, 280, 170),
    "corner adjusts both dimensions");
Require(Edit(rect, SelectionEdit.Left, 900, 0) == new SelectionBounds(392, 80, 8, 200),
    "crossing the opposite edge stops at minimum size");
Require(Edit(rect, SelectionEdit.Move, -900, -900) == new SelectionBounds(0, 0, 300, 200),
    "movement clamps at the top/left without changing size");
Require(Edit(rect, SelectionEdit.Move, 9000, 9000) == new SelectionBounds(1620, 880, 300, 200),
    "movement clamps at the bottom/right without changing size");

foreach (var scaleX in new[] { 1.0, 1.25, 1.5, 1.75, 2.0 })
foreach (var scaleY in new[] { 1.0, 1.25, 1.5, 2.0 })
foreach (var origin in new[] { (-2560, -180), (0, 0), (1920, 100) })
{
    // Screen coordinates first become screenshot-local pixels, then XAML DIPs.
    var screenX = origin.Item1 + 123;
    var screenY = origin.Item2 + 87;
    var logical = new SelectionBounds((screenX - origin.Item1) / scaleX,
        (screenY - origin.Item2) / scaleY, 317 / scaleX, 211 / scaleY);
    var pixels = RectangleSelectionEditor.ToPixels(logical, scaleX, scaleY, 4480, 1440);
    Require(pixels == new SelectionBounds(123, 87, 317, 211),
        "mixed DPI and negative monitor origins preserve the physical crop");
    foreach (var step in new[] { 1, 10 })
    {
        var nudged = RectangleSelectionEditor.Drag(pixels, SelectionEdit.Move,
            step, -step, 4480, 1440, 8, 8);
        var roundTrip = RectangleSelectionEditor.ToPixels(new(nudged.X / scaleX,
            nudged.Y / scaleY, nudged.Width / scaleX, nudged.Height / scaleY),
            scaleX, scaleY, 4480, 1440);
        Require(roundTrip == new SelectionBounds(123 + step, 87 - step, 317, 211),
            "keyboard nudges remain exactly 1/10 physical pixels after export conversion");
    }
}

var random = new Random(3721);
foreach (var edit in new[] { SelectionEdit.Left, SelectionEdit.Right, SelectionEdit.Top,
    SelectionEdit.Bottom, SelectionEdit.TopLeft, SelectionEdit.TopRight,
    SelectionEdit.BottomLeft, SelectionEdit.BottomRight, SelectionEdit.Move })
for (var i = 0; i < 1000; i++)
{
    var x = random.Next(0, 1700);
    var y = random.Next(0, 800);
    var start = new SelectionBounds(x, y, random.Next(8, 1921 - x), random.Next(8, 1081 - y));
    var result = Edit(start, edit, random.Next(-3000, 3001), random.Next(-2000, 2001));
    Require(result.X >= 0 && result.Y >= 0 && result.Right <= 1920 && result.Bottom <= 1080,
        "all resize directions stay within the desktop");
    Require(result.Width >= 8 && result.Height >= 8, "all drags keep valid dimensions");
    if (edit == SelectionEdit.Move)
        Require(result.Width == start.Width && result.Height == start.Height,
            "moving never resizes the selection");
}
Console.WriteLine("Rectangle editing checks passed: 8 handles, move/clamp, minimum size, DPI/negative origins, exact pixel nudges, 9000 boundary cases.");

static SelectionBounds Edit(SelectionBounds start, SelectionEdit part, double dx, double dy)
    => RectangleSelectionEditor.Drag(start, part, dx, dy, 1920, 1080, 8, 8);

static void Require(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
}
