// Adapted from RapidAI/RapidOcrDotNet (Apache-2.0). See NOTICE.md.
using Emgu.CV;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using RapidOCRLib.Models;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RapidOCRLib
{
    class CrnnNet : IDisposable
    {

        private InferenceSession crnnNet;
        private List<string> keys;
        private List<string> inputNames;

        public CrnnNet() { }

        public void Dispose()
        {
            crnnNet?.Dispose();
        }

        public async Task InitModel(string path, string keysPath, int numThread)
        {
            try
            {
                using SessionOptions op = new SessionOptions();
                op.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
                op.InterOpNumThreads = 1;
                op.EnableCpuMemArena = false;
                op.IntraOpNumThreads = numThread;
                crnnNet = new InferenceSession(path, op);
                inputNames = crnnNet.InputMetadata.Keys.ToList();
                keys = new List<string>{"#"};
                keys.AddRange(crnnNet.ModelMetadata.CustomMetadataMap["character"].Replace("\r\n","\n").TrimEnd('\n').Split('\n'));
                keys.Add(" ");
                await Task.CompletedTask;
            }
            catch (Exception)
            {

                throw;
            }
        }

        public List<TextLine> GetTextLines(List<Mat> partImgs)
        {
            var lines=new TextLine[partImgs.Count];
            var indices=Enumerable.Range(0,partImgs.Count).OrderBy(i=>partImgs[i].Cols/(double)partImgs[i].Rows).ToArray();
            for(int start=0;start<indices.Length;start+=6)
            {
                var ids=indices.Skip(start).Take(6).ToArray();
                var imgs=ids.Select(i=>partImgs[i]).ToList();
                double ratio=Math.Max(320.0/48,imgs.Max(m=>m.Cols/(double)m.Rows));
                var input=OcrUtils.MakeBatch(imgs,48,(int)(48*ratio));
                var inputs=new[]{NamedOnnxValue.CreateFromTensor(inputNames[0],input)};
                using var results=crnnNet.Run(inputs);
                var tensor=results.First().AsTensor<float>();
                ReadOnlySpan<float> output=tensor is DenseTensor<float> dense ? dense.Buffer.Span : tensor.ToArray();
                int t=tensor.Dimensions[1], vocab=tensor.Dimensions[2], stride=t*vocab;
                for(int n=0;n<ids.Length;++n) lines[ids[n]]=ScoreToTextLine(output.Slice(n*stride,stride),t,vocab);
            }
            return lines.ToList();
        }

        private TextLine ScoreToTextLine(ReadOnlySpan<float> srcData, int h, int w)
        {
            StringBuilder sb = new StringBuilder();
            TextLine textLine = new TextLine();

            int lastIndex = 0;
            List<float> scores = new List<float>();

            for (int i = 0; i < h; i++)
            {
                int maxIndex = 0;
                float maxValue = -1000F;
                for (int j = 0; j < w; j++)
                {
                    int idx = i * w + j;
                    if (srcData[idx] > maxValue)
                    {
                        maxIndex = j;
                        maxValue = srcData[idx];
                    }
                }

                if (maxIndex > 0 && maxIndex < keys.Count && (!(i > 0 && maxIndex == lastIndex)))
                {
                    scores.Add(maxValue);
                    sb.Append(keys[maxIndex]);
                }
                lastIndex = maxIndex;
            }
            textLine.Text = sb.ToString();
            textLine.CharScores = scores;
            return textLine;
        }

    }
}