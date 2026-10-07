// Adapted from RapidAI/RapidOcrDotNet (Apache-2.0). See NOTICE.md.
using System.Drawing;

namespace RapidOCRLib.Models;

internal sealed class TextBox
{
    public List<Point> Points { get; set; }
    public float Score { get; set; }
}

internal sealed class Angle
{
    public int Index { get; set; }
    public float Score { get; set; }
}

internal sealed class TextLine
{
    public string Text { get; set; }
    public List<float> CharScores { get; set; }
}

internal sealed record ScaleParam(int SrcWidth, int SrcHeight, int DstWidth, int DstHeight,
    float ScaleWidth, float ScaleHeight);
