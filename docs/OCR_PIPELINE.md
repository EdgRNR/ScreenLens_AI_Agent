# OCR 模型与性能

当前使用 RapidOCR 3.9.2，检测和识别为 PP-OCRv6 small，方向分类为
`ch_ppocr_mobile_v2.0_cls_mobile`，通过 ONNX Runtime 在 CPU 上运行。
版本与模型配置均显式固定；不依赖未来安装版本的默认模型变化。
模型已下载后，识别完全在本地执行，不上传截图。

同一 worker 复用一个懒加载引擎。首次请求需要启动进程和加载模型，后续请求
复用推理会话。并发推理串行化，避免 RapidOCR 内部可变状态相互覆盖。
空闲 30 秒后仍退出整个 worker；Esc 取消仍终止 worker，下一次请求重新启动。
已退出或取消的 worker 会显式关闭管道，避免句柄遗留及缓冲区销毁异常。

## 参数调整

- 检测改用 `Det.limit_type=max`。RapidOCR 3.9.2 根据输入长边选择
  960/1500/2000 像素上限，不再将小选区的短边放大到 736 像素。
  识别阶段仍从原始图像获取文字行，返回框坐标仍对应原始选区。
- 180 度方向纠正的置信度门槛从 0.9 调整到 0.99。测试中的一条正向数字行
  被分类器以约 0.9004 的分数错误旋转，导致乱码；提高门槛避免这类边缘判断。
  低于新门槛的倒置文字也可能不被纠正，因此保留中/日文倒置样本回归测试。
- CPU 推理最多使用 4 个 intra-op 线程，inter-op 为 1，CPU memory arena
  保持关闭。在测试机器上，8 线程未明显改善这组短截图的中位耗时。
- 初始化失败允许下次重试；损坏图像不会清除已成功加载的引擎。

## 本机比较

环境为 24 个逻辑 CPU、RapidOCR 3.9.2、ONNX Runtime 1.30.0。
31 张由 Windows 字体生成的合成图涵盖 10/12/16/24 像素文字、中/英/日、
混排、明暗背景、低对比、倒置、倾斜、多行大选区和空白图。
每个方案使用独立新进程；性能比较不包含模型下载。

| 方案 | 推理中位耗时 | 字符编辑错误 / 1638 字符 | 采样 Private Bytes 峰值 |
|---|---:|---:|---:|
| 修改前默认 small | 2054 ms | 67 | 1851 MiB |
| 当前 small + 截图参数优化 | 135 ms | 14 | 1057 MiB |
| medium 检测/识别 + 长边限制，4 线程 | 2538 ms | 6 | 1364 MiB |

字符错误指标忽略空白，不能体现英文空格是否正确；JSON 同时保留原始结果和
包含空白的编辑错误。主要残余差异包括 `I/l` 易混字符和 `￥/¥`。
Private Bytes 是进程私有提交内存，由 psutil 在 Windows 上以 20 ms 间隔采样，
不是驻留物理内存，也不是整个 ScreenLens 的内存。合成结果不代表真实截图的
总体准确率；极小字号、密集大图及特殊字体仍需实际截图验收。

当前保留 small。medium 在样本中减少了一些字符错误，但推理耗时显著增加，
不作为默认模型。只替换识别模型的 medium 组合也未取得足够收益。

真实 IPC worker 检查覆盖连续 101 次识别仅初始化一次、同进程复用、重复任务
内存稳定、实际空闲 30 秒退出、取消后进程退出以及再次识别成功。

## 复测

在安装好项目依赖和开发测试依赖 `pytest`、`psutil` 的环境中执行：

```powershell
.venv\Scripts\python.exe -X utf8 scripts/benchmark_ocr.py
.venv\Scripts\python.exe -X utf8 -m pytest tests/test_ocr.py tests/test_ocr_engine.py tests/test_ocr_worker.py tests/test_agent_cancellation.py -q -p no:cacheprovider
```

基准默认比较 `baseline` 和实际生产参数 `production`，报告及合成图片写入
已忽略的 `.local/capture-diagnostics/ocr-benchmark/`。medium 比较需先下载
对应官方模型并校验 SHA256，用 `--model-dir` 指定同时包含 small、medium
及方向模型的目录。脚本缺模型时明确报错，不在性能测试过程中下载。
本次试验的 medium 模型留在该报告目录下的 `models/`，不加入运行依赖或
生产打包目录。可复测：

```powershell
.venv\Scripts\python.exe -X utf8 scripts/benchmark_ocr.py --profiles baseline production screen-medium-4 --model-dir .local/capture-diagnostics/ocr-benchmark/models
```
