using System.Diagnostics;
using System.Drawing;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using RapidOCRLib;
using RapidOCRLib.Models;

namespace ScreenLens.Ocr.Worker;

internal sealed class OcrEngine(string modelRoot) : IDisposable
{
    private DbNet detector;
    private AngleNet classifier;
    private CrnnNet recognizer;

    public object Recognize(byte[] png)
    {
        var watch = Stopwatch.StartNew();
        using var image = new Mat();
        CvInvoke.Imdecode(png, ImreadModes.ColorBgr, image);
        if (image.IsEmpty) throw new InvalidDataException("无法读取图像");
        EnsureModels();
        using var resized = ResizeWithinBounds(image);
        double ratioX = image.Cols / (double)resized.Cols;
        double ratioY = image.Rows / (double)resized.Rows;
        int padding = resized.Rows <= 30 || resized.Cols / (double)resized.Rows > 8
            ? Math.Abs(Math.Max(resized.Cols / 8, 30) * 2 - resized.Rows) / 2 : 0;
        using var padded = new Mat();
        // Match RapidOCR's vertical letterbox (black); only affects detection.
        CvInvoke.CopyMakeBorder(resized, padded, padding, padding, 0, 0,
            BorderType.Constant, new MCvScalar(0, 0, 0));
        int longest = Math.Max(padded.Rows, padded.Cols);
        int limit = longest < 960 ? 960 : longest < 1500 ? 1500 : 2000;
        double ratio = Math.Min(1, limit / (double)longest);
        int width = Round32((int)(padded.Cols * ratio));
        int height = Round32((int)(padded.Rows * ratio));
        var scale = new ScaleParam(padded.Cols, padded.Rows, width, height,
            width / (float)padded.Cols, height / (float)padded.Rows);
        var boxes = detector.GetTextBoxes(padded, scale, .5f, .3f, 1.6f)
            .Where(b => Distance(b.Points[0], b.Points[1]) > 3 &&
                        Distance(b.Points[0], b.Points[3]) > 3)
            .OrderBy(b => b.Points[0].Y).ToList();
        // RapidOCR 3.9 groups adjacent Y differences, then sorts each row by X.
        var sorted = new List<TextBox>(boxes.Count);
        for (int start = 0; start < boxes.Count;)
        {
            int end = start + 1;
            while (end < boxes.Count && boxes[end].Points[0].Y - boxes[end - 1].Points[0].Y < 10) ++end;
            sorted.AddRange(boxes.GetRange(start, end - start).OrderBy(b => b.Points[0].X));
            start = end;
        }
        boxes = sorted;
        var crops = new List<Mat>();
        try
        {
            foreach (var box in boxes) crops.Add(OcrUtils.GetRotateCropImage(padded, box.Points));
            var angles = crops.Count == 0 ? new List<Angle>() : classifier.GetAngles(crops, true, false);
            for (int i = 0; i < crops.Count; ++i)
                if (angles[i].Index == 1 && angles[i].Score > .99f)
                    CvInvoke.Rotate(crops[i], crops[i], RotateFlags.Rotate180);
            var texts = crops.Count == 0 ? new List<TextLine>() : recognizer.GetTextLines(crops);
            var lines = texts.Select((t, i) => new
            {
                text = t.Text.Trim(),
                score = t.CharScores.Count == 0 ? 0 : (double)t.CharScores.Average(),
                box = boxes[i].Points.Select(p => new[] {
                    Math.Clamp(p.X * ratioX, 0, image.Cols),
                    Math.Clamp((p.Y - padding) * ratioY, 0, image.Rows) }).ToArray()
            }).Where(t => t.text.Length > 0 && t.score >= .5).Select(t => new
            {
                t.text, score = Math.Round(t.score, 4), t.box
            }).ToArray();
            return new { text = string.Join("\n", lines.Select(t => t.text)), lines,
                elapsed_ms = (int)watch.Elapsed.TotalMilliseconds };
        }
        finally
        {
            foreach (var crop in crops) crop.Dispose();
        }
    }

    private static Mat ResizeWithinBounds(Mat image)
    {
        int width = image.Cols, height = image.Rows;
        double ratio = Math.Min(1, 2000.0 / Math.Max(width, height));
        if (ratio == 1 && Math.Min(width, height) < 30)
            ratio = Math.Min(30.0 / Math.Min(width, height), 2000.0 / Math.Max(width, height));
        var result = new Mat();
        try
        {
            if (ratio == 1) image.CopyTo(result);
            else CvInvoke.Resize(image, result,
                new Size(Round32((int)(width * ratio)), Round32((int)(height * ratio))));
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    private static int Round32(int value) => Math.Max(32, (int)Math.Round(value / 32.0) * 32);
    private static double Distance(Point a, Point b) =>
        Math.Sqrt((double)(a.X - b.X) * (a.X - b.X) + (double)(a.Y - b.Y) * (a.Y - b.Y));

    private void EnsureModels()
    {
        if (recognizer is not null) return;
        // Failed initialization must release any sessions already created.
        var det = new DbNet();
        var cls = new AngleNet();
        var rec = new CrnnNet();
        try
        {
            int threads = Math.Min(4, Environment.ProcessorCount);
            det.InitModel(Path.Combine(modelRoot, "PP-OCRv6_det_small.onnx"), threads).GetAwaiter().GetResult();
            cls.InitModel(Path.Combine(modelRoot, "ch_ppocr_mobile_v2.0_cls_mobile.onnx"), threads).GetAwaiter().GetResult();
            rec.InitModel(Path.Combine(modelRoot, "PP-OCRv6_rec_small.onnx"), "", threads).GetAwaiter().GetResult();
            detector = det;
            classifier = cls;
            recognizer = rec;
        }
        catch { det.Dispose(); cls.Dispose(); rec.Dispose(); throw; }
    }

    public void Dispose()
    {
        detector?.Dispose();
        classifier?.Dispose();
        recognizer?.Dispose();
    }
}
