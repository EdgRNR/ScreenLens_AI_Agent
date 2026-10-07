// Adapted from RapidAI/RapidOcrDotNet (Apache-2.0). See NOTICE.md.
using Clipper2Lib;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Emgu.CV.Util;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using RapidOCRLib.Models;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;

namespace RapidOCRLib
{
    class DbNet : IDisposable
    {
        private readonly float[] MeanValues = { 127.5F, 127.5F, 127.5F };
        private readonly float[] NormValues = { 1.0F / 127.5F, 1.0F / 127.5F, 1.0F / 127.5F };

        private InferenceSession dbNet;

        private List<string> inputNames;

        public DbNet() { }

        public void Dispose()
        {
            dbNet?.Dispose();
        }

        public async Task InitModel(string path, int numThread)
        {
            try
            {
                using SessionOptions op = new SessionOptions();
                op.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
                op.InterOpNumThreads = 1;
                op.EnableCpuMemArena = false;
                op.IntraOpNumThreads = numThread;
                dbNet = new InferenceSession(path, op);
                inputNames = dbNet.InputMetadata.Keys.ToList();
                await Task.CompletedTask;
            }
            catch (Exception)
            {

                throw;
            }
        }

        public List<TextBox> GetTextBoxes(Mat src, ScaleParam scale, float boxScoreThresh, float boxThresh, float unClipRatio)
        {
            using Mat srcResize = new Mat();
            CvInvoke.Resize(src, srcResize, new Size(scale.DstWidth, scale.DstHeight));
            Tensor<float> inputTensors = OcrUtils.SubstractMeanNormalize(srcResize, MeanValues, NormValues);
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor(inputNames[0], inputTensors)
            };
            try
            {
                using (IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = dbNet.Run(inputs))
                {
                    var resultsArray = results.ToArray();

                    var textBoxes = GetTextBoxes(resultsArray, srcResize.Rows, srcResize.Cols, scale, boxScoreThresh, boxThresh, unClipRatio);
                    return textBoxes;
                }
            }
            catch (Exception)
            {
                throw;
            }
        }

        private static List<TextBox> GetTextBoxes(DisposableNamedOnnxValue[] outputTensor, int rows, int cols, ScaleParam s, float boxScoreThresh, float boxThresh, float unClipRatio)
        {
            float maxSideThresh = 3.0f;//长边门限
            List<TextBox> rsBoxes = new List<TextBox>();
            //-----Data preparation-----
            float[] predData = outputTensor[0].AsEnumerable<float>().ToArray();
            using Mat predMat = new Mat(rows, cols, DepthType.Cv32F, 1);
            predMat.SetTo(predData);
            using Mat thresholdFloat = new Mat();
            using Mat thresholdMat = new Mat();
            CvInvoke.Threshold(predMat, thresholdFloat, boxThresh, 255, ThresholdType.Binary);
            thresholdFloat.ConvertTo(thresholdMat, DepthType.Cv8U);

            //-----dilate-----
            using Mat dilateMat = new Mat();
            //using Mat dilateElement = CvInvoke.GetStructuringElement(ElementShape.Rectangle, new Size(2, 2), new Point(-1, -1));
            using Mat dilateElement = CvInvoke.GetStructuringElement(MorphShapes.Rectangle, new Size(2, 2), new Point(-1, -1));
            CvInvoke.Dilate(thresholdMat, dilateMat, dilateElement, new Point(-1, -1), 1, BorderType.Default, new MCvScalar(128, 128, 128));

            using VectorOfVectorOfPoint contours = new VectorOfVectorOfPoint();

            CvInvoke.FindContours(dilateMat, contours, null, RetrType.List, ChainApproxMethod.ChainApproxSimple);

            for (int i = 0; i < Math.Min(contours.Size, 1000); i++)
            {
                using var contour = contours[i];
                if (contour.Size <= 2)
                {
                    continue;
                }
                float maxSide = 0;
                List<PointF> minBox = GetMiniBox(contour, out maxSide);
                if (maxSide < maxSideThresh)
                {
                    continue;
                }
                double score = GetScore(minBox, predMat);
                if (score < boxScoreThresh)
                {
                    continue;
                }
                List<Point> clipBox = Unclip(minBox, unClipRatio);
                if (clipBox == null)
                {
                    continue;
                }

                List<PointF> clipMinBox = GetMiniBox(clipBox, out maxSide);
                if (maxSide < maxSideThresh + 2)
                {
                    continue;
                }
                List<Point> finalPoints = new List<Point>();
                foreach (var item in clipMinBox)
                {
                    int x = (int)Math.Round(item.X / (double)s.ScaleWidth);
                    int ptx = Math.Min(Math.Max(x, 0), s.SrcWidth - 1);

                    int y = (int)Math.Round(item.Y / (double)s.ScaleHeight);
                    int pty = Math.Min(Math.Max(y, 0), s.SrcHeight - 1);
                    Point dstPt = new Point(ptx, pty);
                    finalPoints.Add(dstPt);
                }

                TextBox textBox = new TextBox();
                textBox.Score = (float)score;
                textBox.Points = finalPoints;
                rsBoxes.Add(textBox);
            }
            rsBoxes.Reverse();
            return rsBoxes;
        }

        private static List<PointF> GetMiniBox(List<Point> contours, out float minEdgeSize)
        {
            using VectorOfPoint vop = new VectorOfPoint();
            vop.Push(contours.ToArray<Point>());
            return GetMiniBox(vop, out minEdgeSize);
        }

        private static List<PointF> GetMiniBox(VectorOfPoint contours, out float minEdgeSize)
        {
            List<PointF> box = new List<PointF>();
            RotatedRect rrect = CvInvoke.MinAreaRect(contours);
            PointF[] points = CvInvoke.BoxPoints(rrect);
            minEdgeSize = Math.Min(rrect.Size.Width, rrect.Size.Height);

            List<PointF> thePoints = new List<PointF>(points);
            thePoints.Sort(CompareByX);

            int index_1 = 0, index_2 = 1, index_3 = 2, index_4 = 3;
            if (thePoints[1].Y > thePoints[0].Y)
            {
                index_1 = 0;
                index_4 = 1;
            }
            else
            {
                index_1 = 1;
                index_4 = 0;
            }

            if (thePoints[3].Y > thePoints[2].Y)
            {
                index_2 = 2;
                index_3 = 3;
            }
            else
            {
                index_2 = 3;
                index_3 = 2;
            }

            box.Add(thePoints[index_1]);
            box.Add(thePoints[index_2]);
            box.Add(thePoints[index_3]);
            box.Add(thePoints[index_4]);

            return box;
        }

        public static int CompareByX(PointF left, PointF right) { return left.X.CompareTo(right.X); }

        private static double GetScore(List<PointF> box, Mat fMapMat)
        {
            int x0 = Math.Clamp((int)Math.Floor(box.Min(p => p.X)), 0, fMapMat.Cols - 1);
            int y0 = Math.Clamp((int)Math.Floor(box.Min(p => p.Y)), 0, fMapMat.Rows - 1);
            int x1 = Math.Clamp((int)Math.Ceiling(box.Max(p => p.X)), 0, fMapMat.Cols - 1);
            int y1 = Math.Clamp((int)Math.Ceiling(box.Max(p => p.Y)), 0, fMapMat.Rows - 1);
            var rect = new Rectangle(x0, y0, x1 - x0 + 1, y1 - y0 + 1);
            if(rect.Width<=0||rect.Height<=0) return 0;
            var pts=box.Select(p=>new Point((int)(p.X-rect.X),(int)(p.Y-rect.Y))).ToArray();
            using var polygon=new VectorOfPoint(pts);
            using var polygons=new VectorOfVectorOfPoint();
            polygons.Push(new[]{polygon});
            using var mask=new Mat(rect.Height,rect.Width,DepthType.Cv8U,1);
            mask.SetTo(new MCvScalar(0));
            CvInvoke.FillPoly(mask,polygons,new MCvScalar(1));
            using var roi=new Mat(fMapMat,rect);
            return CvInvoke.Mean(roi,mask).V0;
        }

        private static List<Point> Unclip(List<PointF> box, float unclip_ratio)
        {
            RotatedRect clipRect = CvInvoke.MinAreaRect(box.ToArray());
            if (clipRect.Size.Height < 1.001 && clipRect.Size.Width < 1.001)
            {
                return null;
            }

            Path64 theCliperPts = new Path64();
            foreach (PointF pt in box)
            {
                theCliperPts.Add(new Point64((int)pt.X, (int)pt.Y));
            }

            float area = Math.Abs(SignedPolygonArea(box.ToArray<PointF>()));
            double length = LengthOfPoints(box);
            double distance = area * unclip_ratio / length;

            ClipperOffset co = new ClipperOffset();
            co.AddPath(theCliperPts, JoinType.Round, EndType.Polygon);

            Paths64 solution = new Paths64();

            co.Execute(distance, solution);
            if (solution.Count == 0)
            {
                return null;
            }

            List<Point> retPts = new List<Point>();
            foreach (Point64 ip in solution[0])
            {
                retPts.Add(new Point((int)ip.X, (int)ip.Y));
            }

            return retPts;
        }

        private static float SignedPolygonArea(PointF[] Points)
        {
            // Add the first point to the end.
            int num_points = Points.Length;
            PointF[] pts = new PointF[num_points + 1];
            Points.CopyTo(pts, 0);
            pts[num_points] = Points[0];

            // Get the areas.
            float area = 0;
            for (int i = 0; i < num_points; i++)
            {
                area +=
                    (pts[i + 1].X - pts[i].X) *
                    (pts[i + 1].Y + pts[i].Y) / 2;
            }

            return area;
        }

        private static double LengthOfPoints(List<PointF> box)
        {
            double length = 0;

            PointF pt = box[0];
            double x0 = pt.X;
            double y0 = pt.Y;
            double x1 = 0, y1 = 0, dx = 0, dy = 0;
            box.Add(pt);

            int count = box.Count;
            for (int idx = 1; idx < count; idx++)
            {
                PointF pts = box[idx];
                x1 = pts.X;
                y1 = pts.Y;
                dx = x1 - x0;
                dy = y1 - y0;

                length += Math.Sqrt(dx * dx + dy * dy);

                x0 = x1;
                y0 = y1;
            }

            box.RemoveAt(count - 1);
            return length;
        }

    }
}
