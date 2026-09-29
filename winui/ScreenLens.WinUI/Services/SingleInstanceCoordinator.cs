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
            var mutex = new Mutex(initiallyOwned: true, mutexName,
                out var createdNew);
            if (!createdNew)
            {
                mutex.Dispose();
                coordinator = null;
                return false;
            }

            coordinator = new SingleInstanceCoordinator(mutex, pipeName,
                dispatcher, activate);
            return true;
        }

        public static async Task<bool> ForwardToPrimaryAsync(string[] args,
            CancellationToken cancellationToken = default)
        {
            var (_, pipeName) = GetIdentity();
            var payload = JsonSerializer.Serialize(args);
            var deadline = DateTime.UtcNow.AddSeconds(4);
            while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    using var client = new NamedPipeClientStream(".", pipeName,
                        PipeDirection.InOut, PipeOptions.Asynchronous);
                    using var connectTimeout = CancellationTokenSource
                        .CreateLinkedTokenSource(cancellationToken);
                    connectTimeout.CancelAfter(250);
                    await client.ConnectAsync(connectTimeout.Token);
                    using var writer = new StreamWriter(client, new UTF8Encoding(false),
                        1024, leaveOpen: true) { AutoFlush = true };
                    using var reader = new StreamReader(client, Encoding.UTF8,
                        detectEncodingFromByteOrderMarks: false, 1024,
                        leaveOpen: true);
                    await writer.WriteLineAsync(payload);
                    var ack = await reader.ReadLineAsync(cancellationToken);
                    return ack == "ok";
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(100, cancellationToken);
                }
                catch (IOException)
                {
                    await Task.Delay(100, cancellationToken);
                }
                catch (TimeoutException)
                {
                    await Task.Delay(100, cancellationToken);
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
                    _dispatcher.TryEnqueue(() => _activate(args));
                    await writer.WriteLineAsync("ok");
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
