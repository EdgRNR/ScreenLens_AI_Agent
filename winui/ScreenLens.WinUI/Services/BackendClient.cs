using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ScreenLens.WinUI.Services
{
    /// <summary>与 Python 后台代理通信失败（含协议错误码）。</summary>
    public sealed class BackendException : Exception
    {
        public string Code { get; }

        public BackendException(string code, string message) : base(message)
            => Code = code;
    }

    /// <summary>
    /// 每个 IPC 请求使用独立管道连接。这样耗时 OCR 不会阻塞另一连接上的
    /// CancelRequest；服务端本身支持并发连接，任务执行仍由 WorkerManager 串行化。
    /// 已发送的请求不自动重试，避免断线后重复执行非幂等操作。
    /// </summary>
    public sealed class BackendClient
    {
        public const string PipeName = "ScreenLensAgent";
        public const int ProtocolVersion = 1;

        private const int ConnectTimeoutMs = 1500;
        private const int DefaultTimeoutMs = 10_000;
        private const int MaxFrame = 32 * 1024 * 1024;

        public static BackendClient Instance { get; } = new();
        private int _nextId;

        private BackendClient() { }

        public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
        {
            try
            {
                await CallAsync("Ping", null, timeoutMs: 1000, ct: ct);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>发起一次请求并返回响应 data；业务失败抛 BackendException。</summary>
        public async Task<JsonObject> CallAsync(string op, JsonObject? data,
            byte[]? image = null, int timeoutMs = DefaultTimeoutMs,
            CancellationToken ct = default)
        {
            using var requestTimeout = CancellationTokenSource
                .CreateLinkedTokenSource(ct);
            requestTimeout.CancelAfter(timeoutMs);
            var token = requestTimeout.Token;
            var reqId = Interlocked.Increment(ref _nextId);
            using var pipe = new NamedPipeClientStream(".", PipeName,
                PipeDirection.InOut, PipeOptions.Asynchronous);

            try
            {
                using (var connectTimeout = CancellationTokenSource
                    .CreateLinkedTokenSource(token))
                {
                    connectTimeout.CancelAfter(ConnectTimeoutMs);
                    await pipe.ConnectAsync(connectTimeout.Token);
                }

                var request = new JsonObject
                {
                    ["v"] = ProtocolVersion,
                    ["id"] = reqId,
                    ["op"] = op,
                    ["data"] = data?.DeepClone() ?? new JsonObject(),
                };
                await WriteFrameAsync(pipe, EncodeJson(request), token);
                if (image is { Length: > 0 })
                {
                    if (image.Length > MaxFrame)
                        throw new BackendException("image_too_large", "图像超过 IPC 大小限制");
                    await WriteFrameAsync(pipe, image, token);
                }

                var payload = await ReadFrameAsync(pipe, token);
                var response = JsonNode.Parse(Encoding.UTF8.GetString(payload))
                    as JsonObject
                    ?? throw new BackendException("internal", "响应不是 JSON 对象");
                if (response["v"]?.GetValue<int>() != ProtocolVersion
                    || response["id"]?.GetValue<int>() != reqId)
                    throw new BackendException("protocol_mismatch", "后台响应版本或请求编号不匹配");

                if (response["ok"]?.GetValue<bool>() == true)
                    return response["data"]?.AsObject() ?? new JsonObject();

                var err = response["error"]?.AsObject();
                throw new BackendException(
                    err?["code"]?.GetValue<string>() ?? "internal",
                    err?["message"]?.GetValue<string>() ?? "后台请求失败");
            }
            catch (BackendException)
            {
                throw;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw new BackendException("timeout", $"后台请求超时（{op}）");
            }
            catch (Exception e) when (e is IOException
                or TimeoutException or UnauthorizedAccessException)
            {
                throw new BackendException("agent_unavailable",
                    $"后台代理未运行或 IPC 连接失败：{e.Message}");
            }
        }

        private static byte[] EncodeJson(JsonNode node)
            => Encoding.UTF8.GetBytes(node.ToJsonString());

        private static async Task WriteFrameAsync(Stream stream, byte[] payload,
            CancellationToken ct)
        {
            if (payload.Length == 0 || payload.Length > MaxFrame)
                throw new BackendException("bad_request", "帧大小超出限制");
            var head = new byte[4]
            {
                (byte)(payload.Length >>> 24),
                (byte)(payload.Length >>> 16),
                (byte)(payload.Length >>> 8),
                (byte)payload.Length,
            };
            await stream.WriteAsync(head, ct);
            await stream.WriteAsync(payload, ct);
            await stream.FlushAsync(ct);
        }

        private static async Task<byte[]> ReadFrameAsync(Stream stream,
            CancellationToken ct)
        {
            var head = await ReadExactAsync(stream, 4, ct);
            var len = ((long)head[0] << 24) | ((long)head[1] << 16)
                | ((long)head[2] << 8) | head[3];
            if (len == 0 || len > MaxFrame)
                throw new BackendException("protocol_error", "响应帧大小非法");
            return await ReadExactAsync(stream, (int)len, ct);
        }

        private static async Task<byte[]> ReadExactAsync(Stream stream, int length,
            CancellationToken ct)
        {
            var buffer = new byte[length];
            var offset = 0;
            while (offset < length)
            {
                var read = await stream.ReadAsync(
                    buffer.AsMemory(offset, length - offset), ct);
                if (read == 0) throw new IOException("命名管道提前关闭");
                offset += read;
            }
            return buffer;
        }
    }
}
