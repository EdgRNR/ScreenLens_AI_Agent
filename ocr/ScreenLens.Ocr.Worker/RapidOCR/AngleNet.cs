// Adapted from RapidAI/RapidOcrDotNet (Apache-2.0). See NOTICE.md.
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
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
    class AngleNet : IDisposable
    {

        private int _dstWidth;
        private int _dstHeight;
        private InferenceSession angleNet;
        private List<string> inputNames;

        public AngleNet() { }

        public void Dispose()
        {
            angleNet?.Dispose();
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
                angleNet = new InferenceSession(path, op);
                inputNames = angleNet.InputMetadata.Keys.ToList();

                // 从 ONNX 模型 metadata 读取输入尺寸，兼容 v2 (192x48) 和 v5 (160x80)
                var dims = angleNet.InputMetadata.First().Value.Dimensions;
                _dstHeight = dims[2] > 0 ? dims[2] : 48;
                _dstWidth = dims[3] > 0 ? dims[3] : 192;

                await Task.CompletedTask;
            }
            catch (Exception)
            {

                throw;
            }
        }

        public List<Angle> GetAngles(List<Mat> partImgs, bool doAngle, bool mostAngle)
        {
            if(!doAngle || mostAngle) throw new NotSupportedException("Unsupported direction classification mode");
            var angles=new Angle[partImgs.Count];
            var indices=Enumerable.Range(0,partImgs.Count).OrderBy(i=>partImgs[i].Cols/(double)partImgs[i].Rows).ToArray();
            for(int start=0;start<indices.Length;start+=6)
            {
                var ids=indices.Skip(start).Take(6).ToArray();
                var input=OcrUtils.MakeBatch(ids.Select(i=>partImgs[i]).ToList(),_dstHeight,_dstWidth);
                using var results=angleNet.Run(new[]{NamedOnnxValue.CreateFromTensor(inputNames[0],input)});
                var tensor=results.First().AsTensor<float>();
                for(int n=0;n<ids.Length;++n)
                {
                    int index=tensor[n,1]>tensor[n,0]?1:0;
                    angles[ids[n]]=new Angle{Index=index,Score=tensor[n,index]};
                }
            }
            return angles.ToList();
        }

    }
}
