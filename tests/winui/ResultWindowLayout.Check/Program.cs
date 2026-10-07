using System;
using ScreenLens.WinUI.Views.Result;

var shortText = ResultWindowLayout.InitialSize("Young分享·3小时前更新", 15, 1280, 720);
var longText = ResultWindowLayout.InitialSize(new string('字', 3000), 15, 1280, 720);
Require(longText.Width > shortText.Width && longText.Height > shortText.Height,
    "long OCR text has more reading space than a short line");
Require(shortText.Width < ResultWindowLayout.ComparisonWidth,
    "compact results do not start in split view");

foreach (var text in new[] { "", "A", new string('A', 50000), new string('字', 50000),
    string.Join('\n', new string[5000]), "🙂日本語 e\u0301\t文字\r\n第二行" })
{
    foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
    {
        var size = ResultWindowLayout.InitialSize(text, 17, 1280 / scale, 720 / scale);
        Require(size.Width > 0 && size.Width * scale <= 1280,
            "initial width fits the monitor at every DPI");
        Require(size.Height > 0 && size.Height * scale <= 720,
            "initial height fits the monitor at every DPI");
    }
}
var narrowWork = ResultWindowLayout.InitialSize("文字", 15, 320, 240);
Require(narrowWork.Width <= 320 && narrowWork.Height <= 240,
    "available work area takes precedence over the normal minimum");
var multiline = ResultWindowLayout.InitialSize(string.Join('\n', new string[30]), 15, 1280, 1080);
Require(multiline.Height > shortText.Height, "explicit OCR line breaks grow the window");

foreach (var work in new[] { new ResultBounds(-1920, -180, 1920, 1040),
    new ResultBounds(1920, 80, 1280, 944), new ResultBounds(0, -1080, 1920, 1040) })
{
    foreach (var desired in new[] { new ResultBounds(0, 5000, 3000, 2000),
        new ResultBounds(-5000, -5000, 630, 495), new ResultBounds(work.X + 20, work.Y + 20, 420, 330) })
    {
        var fitted = ResultWindowLayout.FitToWorkArea(desired, work);
        Require(fitted.X >= work.X && fitted.Y >= work.Y
            && fitted.X + fitted.Width <= work.X + work.Width
            && fitted.Y + fitted.Height <= work.Y + work.Height,
            "result stays on the selected monitor, including negative origins and taskbar offsets");
    }
}
Require(!ResultWindowLayout.CanCompare(shortText.Width - 32)
    && ResultWindowLayout.CanCompare(1200), "comparison is available only with enough reading width");
Console.WriteLine("Result window layout checks passed (short/long text, multilingual, DPI, monitor bounds, comparison).");

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
