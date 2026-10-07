using System.Buffers.Binary;
using System.Text.Json;

namespace ScreenLens.Ocr.Worker;

internal static class Program
{
    private const int MaxFrameBytes = 32 * 1024 * 1024;

    public static int Main(string[] args)
    {
        // The Agent owns idle timeout, serialization, watchdog and cancellation.
        // No window, startup preheating, or diagnostics may write to stdout.
        using var input = Console.OpenStandardInput();
        using var output = Console.OpenStandardOutput();
        Console.SetOut(TextWriter.Null);
        using var engine = new OcrEngine(args.Length == 1 ? args[0] :
            Path.Combine(AppContext.BaseDirectory, "models"));
        try
        {
            while (ReadFrame(input) is { } frame)
            {
                using var request = JsonDocument.Parse(frame);
                var root = request.RootElement;
                var id = root.TryGetProperty("id", out var value) ? value.GetInt32() : 0;
                var op = root.GetProperty("op").GetString();
                if (op == "exit") return 0;
                if (op != "ocr")
                {
                    Respond(output, new { id, ok = false,
                        error = new { code = "bad_request", message = "不支持的 OCR 请求" } });
                    continue;
                }
                var png = ReadFrame(input);
                if (png is null)
                {
                    Respond(output, new { id, ok = false,
                        error = new { code = "bad_request", message = "缺少图像帧" } });
                    return 0;
                }
                try
                {
                    Respond(output, new { id, ok = true, data = engine.Recognize(png) });
                }
                catch (Exception error)
                {
                    Console.Error.WriteLine(error);
                    Respond(output, new { id, ok = false,
                        error = new { code = "ocr_failed", message = $"OCR 识别失败：{error.Message}" } });
                }
            }
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1; // Broken/oversized protocol frames must not be reused.
        }
    }

    private static byte[] ReadFrame(Stream input)
    {
        Span<byte> header = stackalloc byte[4];
        int read = input.Read(header);
        if (read == 0) return null;
        input.ReadExactly(header[read..]);
        uint size = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (size == 0 || size > MaxFrameBytes)
            throw new InvalidDataException("图像帧长度不合法");
        var buffer = new byte[(int)size];
        input.ReadExactly(buffer);
        return buffer;
    }

    private static void Respond(Stream output, object response)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(response);
        Span<byte> header = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)bytes.Length);
        output.Write(header);
        output.Write(bytes);
        output.Flush();
    }
}
