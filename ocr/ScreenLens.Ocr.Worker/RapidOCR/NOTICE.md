# Third-party source

`DbNet.cs`, `AngleNet.cs`, `CrnnNet.cs`, `OcrUtils.cs` and `Models.cs` are
adapted from [RapidAI/RapidOcrDotNet](https://github.com/RapidAI/RapidOcrDotNet),
commit `c1148c5ff8b3ed864d7660b87bd7d53208b595bc`, under Apache-2.0.
The upstream license is retained in `LICENSE`.

ScreenLens adaptations: PP-OCRv6 metadata dictionary, CPU session settings,
RGB tensors, batch recognition/classification, bounded detection, RapidOCR
box scoring/rounding, removal of UI helpers and deterministic native disposal.
Models remain the same offline RapidOCR model files used by the Python engine.
