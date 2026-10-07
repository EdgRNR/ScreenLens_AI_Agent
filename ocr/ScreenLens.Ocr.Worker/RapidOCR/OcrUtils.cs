// Adapted from RapidAI/RapidOcrDotNet (Apache-2.0). See NOTICE.md.
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Microsoft.ML.OnnxRuntime.Tensors;
using RapidOCRLib.Models;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace RapidOCRLib
{
    class OcrUtils
    {
        public static Tensor<float> SubstractMeanNormalize(Mat src, float[] meanVals, float[] normVals)
        {
            var tensor = new DenseTensor<float>(new[] {1, 3, src.Rows, src.Cols});
            WriteNormalized(src, tensor.Buffer.Span, src.Cols, meanVals, normVals);
            return tensor;
        }

        public static unsafe void WriteNormalized(Mat src, Span<float> target, int paddedWidth, float[] mean, float[] norm)
        {
            byte* ptr = (byte*)src.DataPointer;
            int plane = src.Rows * paddedWidth;
            for (int ch = 0; ch < 3; ++ch)
                for (int y = 0; y < src.Rows; ++y)
                    for (int x = 0; x < src.Cols; ++x)
                        target[ch*plane + y*paddedWidth+x] = (ptr[y*src.Step + x*3 + (2-ch)] - mean[ch]) * norm[ch];
        }

        public static DenseTensor<float> MakeBatch(List<Mat> imgs, int height, int width)
        {
            var tensor = new DenseTensor<float>(new[]{imgs.Count, 3, height, width});
            float[] mean = {127.5f,127.5f,127.5f}, norm = {1/127.5f,1/127.5f,1/127.5f};
            int stride = 3*height*width;
            for (int i=0; i<imgs.Count; ++i)
            {
                int resizedWidth = Math.Min(width, (int)Math.Ceiling(height*imgs[i].Cols/(double)imgs[i].Rows));
                using var resized = new Mat();
                CvInvoke.Resize(imgs[i],resized,new Size(resizedWidth,height),0,0,Inter.Linear);
                WriteNormalized(resized,tensor.Buffer.Span.Slice(i*stride,stride),width,mean,norm);
            }
            return tensor;
        }

        public static Mat GetRotateCropImage(Mat src, List<Point> box)
        {
            double Dist(Point a,Point b) => Math.Sqrt(Math.Pow(a.X-b.X,2)+Math.Pow(a.Y-b.Y,2));
            int w=Math.Max(1,(int)Math.Max(Dist(box[0],box[1]),Dist(box[2],box[3])));
            int h=Math.Max(1,(int)Math.Max(Dist(box[0],box[3]),Dist(box[1],box[2])));
            var from=box.Select(p=>new PointF(p.X,p.Y)).ToArray();
            PointF[] to={new(0,0),new(w,0),new(w,h),new(0,h)};
            using var matrix=CvInvoke.GetPerspectiveTransform(from,to);
            var result=new Mat();
            try { CvInvoke.WarpPerspective(src,result,matrix,new Size(w,h),Inter.Cubic,Warp.Default,BorderType.Replicate); }
            catch { result.Dispose(); throw; }
            if(h/(double)w>=1.5)
            {
                var rotated=new Mat();
                try { CvInvoke.Rotate(result,rotated,RotateFlags.Rotate90CounterClockwise); }
                catch { rotated.Dispose(); result.Dispose(); throw; }
                result.Dispose();
                return rotated;
            }
            return result;
        }

    }
}
