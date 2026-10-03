using Microsoft.UI.Dispatching;
using System;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ScreenLens.WinUI.Services
{
    /// <summary>
    /// WinUI 单实例激活通道。重复启动仅转发命令给现有 UI，不创建第二个窗口进程。
    /// </summary>
    internal sealed class SingleInstanceCoordinator : IDisposable
    {
        private readonly Mutex _mutex;
        private readonly CancellationTokenSource _stop = new();
        private readonly DispatcherQueue _dispatcher;
        private readonly Action<string[]> _activate;
        private readonly string _pipeName;
        private bool _disposed;

        private SingleInstanceCoordinator(Mutex mutex, string pipeName,
            DispatcherQueue dispatcher, Action<string[]> activate)
        {
            _mutex = mutex;
            _pipeName = pipeName;
            _dispatcher = dispatcher;
            _activate = activate;
            _ = Task.Run(ListenAsync);
        }

        public static (string MutexName, string PipeName) GetIdentity()
        {
            var sid = WindowsIdentity.GetCurrent().User?.Value
                ?? Environment.UserName;
            var hash = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(sid)));
            var suffix = hash[..24];
            return ($"Local\\ScreenLens.WinUI.{suffix}",
                $"ScreenLens.WinUI.{suffix}");
        }

        public static bool TryBecomePrimary(DispatcherQueue dispatcher,
            Action<string[]> activate, out SingleInstanceCoordinator? coordinator)
        {
            var (mutexName, pipeName) = GetIdentity();
            var mutex = new Mutex(initiallyOwned: false, mutexName);
            var acquired = false;
            var recoveredAbandonedMutex = false;
            try
            {
                acquired = mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                // 主实例异常退出后，接管已被放弃的互斥体，允许正常恢复。
                acquired = true;
                recoveredAbandonedMutex = true;
            }

            if (!acquired)
            {
                mutex.Dispose();
                coordinator = null;
                return false;
            }

            coordinator = new SingleInstanceCoordinator(mutex, pipeName,
                dispatcher, activate);
            if (recoveredAbandonedMutex)
                App.WriteLifecycleLog("检测到上一个 WinUI 实例异常退出，已接管单实例锁");
            App.WriteLifecycleLog("成为 WinUI 主实例");
            return true;
        }

        public static async Task<bool> ForwardToPrimaryAsync(string[] args,
            CancellationToken cancellationToken = default)
        {
            var (_, pipeName) = GetIdentity();
            var payload = JsonSerializer.Serialize(args);
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    using var client = new NamedPipeClientStream(".", pipeName,
                        PipeDirection.InOut, PipeOptions.Asynchronous);
                    // 同一个超时同时约束连接、写入后的 ACK 等待；旧实现只限制
                    // ConnectAsync，ReadLineAsync 可能永久挂住热键启动的子进程。
                    using var attemptTimeout = CancellationTokenSource
                        .CreateLinkedTokenSource(cancellationToken);
                    attemptTimeout.CancelAfter(600);
                    await client.ConnectAsync(attemptTimeout.Token)
                        .ConfigureAwait(false);
                    using var writer = new StreamWriter(client, new UTF8Encoding(false),
                        1024, leaveOpen: true) { AutoFlush = true };
                    using var reader = new StreamReader(client, Encoding.UTF8,
                        detectEncodingFromByteOrderMarks: false, 1024,
                        leaveOpen: true);
                    await writer.WriteLineAsync(payload)
                        .ConfigureAwait(false);
                    var ack = await reader.ReadLineAsync(attemptTimeout.Token)
                        .ConfigureAwait(false);
                    return ack == "ok";
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                }
            }
            return false;
        }

        private async Task ListenAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(_pipeName,
                        PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(_stop.Token);
                    using var reader = new StreamReader(server, Encoding.UTF8,
                        detectEncodingFromByteOrderMarks: false, 1024,
                        leaveOpen: true);
                    using var writer = new StreamWriter(server, new UTF8Encoding(false),
                        1024, leaveOpen: true) { AutoFlush = true };
                    var line = await reader.ReadLineAsync(_stop.Token);
                    if (line is null) continue;
                    var args = JsonSerializer.Deserialize<string[]>(line) ?? [];
                    var queued = _dispatcher.TryEnqueue(() =>
                    {
                        App.WriteLifecycleLog("命名管道激活请求已进入 UI 队列");
                        _activate(args);
                    });
                    await writer.WriteLineAsync(queued ? "ok" : "error");
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception)
                {
                    if (!_stop.IsCancellationRequested)
                        await Task.Delay(100, _stop.Token);
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _stop.Cancel();
            try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
            _mutex.Dispose();
            _stop.Dispose();
        }
    }
}
