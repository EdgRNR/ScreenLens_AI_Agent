using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ScreenLens.WinUI.Services
{
    /// <summary>
    /// 先连接现有 Agent。只有发布目录包含同目录 Agent exe，或用户显式配置
    /// SCREENLENS_AGENT_EXE / SCREENLENS_AUTOSTART_AGENT=1 时才启动后台。
    /// Visual Studio 调试默认不擅自启动 Python 进程。
    /// </summary>
    internal static class BackendBootstrapper
    {
        public static async Task<string?> EnsureRunningAsync(
            CancellationToken cancellationToken = default)
        {
            if (await BackendClient.Instance.IsAvailableAsync(cancellationToken))
                return null;

            var startInfo = CreateStartInfo();
            if (startInfo is null)
                return "未连接后台代理。开发调试时请先单独运行 .venv\\Scripts\\python.exe run_agent.py；VS 不会自动启动后台。";

            Process? process = null;
            try
            {
                process = Process.Start(startInfo);
                if (process is null)
                    return "无法启动 ScreenLens 后台代理。";

                // 后台初始化 OCR 以外的托盘/热键/管道；给命名管道数秒启动时间。
                for (var i = 0; i < 40; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (process.HasExited)
                        return $"后台代理启动后立即退出（代码 {process.ExitCode}）。查看 %LOCALAPPDATA%\\ScreenLens\\logs\\agent.log。";
                    if (await BackendClient.Instance.IsAvailableAsync(cancellationToken))
                        return null;
                    await Task.Delay(250, cancellationToken);
                }
                return "后台代理已启动，但 IPC 在 10 秒内未就绪。查看 %LOCALAPPDATA%\\ScreenLens\\logs\\agent.log。";
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                return $"启动后台代理失败：{e.Message}";
            }
            finally
            {
                process?.Dispose();
            }
        }

        private static ProcessStartInfo? CreateStartInfo()
        {
            var explicitExe = Environment.GetEnvironmentVariable(
                "SCREENLENS_AGENT_EXE");
            if (!string.IsNullOrWhiteSpace(explicitExe)
                && File.Exists(explicitExe))
            {
                return HiddenStart(explicitExe);
            }

            var appDir = AppContext.BaseDirectory;
            var packagedAgent = Path.Combine(appDir, "ScreenLensAgent.exe");
            if (File.Exists(packagedAgent))
                return HiddenStart(packagedAgent);

            // 开发态默认由开发者独立启动 Agent；仅显式开启时才自动拉起仓库 Python。
            if (!string.Equals(Environment.GetEnvironmentVariable(
                    "SCREENLENS_AUTOSTART_AGENT"), "1",
                    StringComparison.OrdinalIgnoreCase))
                return null;

            var repo = FindRepositoryRoot(appDir);
            if (repo is null) return null;
            var entry = Path.Combine(repo, "run_agent.py");
            var venv = Path.Combine(repo, ".venv", "Scripts");
            var python = new[]
            {
                Path.Combine(venv, "python.exe"),
                Path.Combine(venv, "pythonw.exe"),
            };
            foreach (var interpreter in python)
            {
                if (!File.Exists(interpreter)) continue;
                var info = HiddenStart(interpreter);
                info.WorkingDirectory = repo;
                info.ArgumentList.Add(entry);
                return info;
            }

            // 支持已自行配置 Python 的开发机；正式发布应随包放置 Agent exe。
            var pathPython = Environment.GetEnvironmentVariable(
                "SCREENLENS_PYTHON_EXE");
            if (!string.IsNullOrWhiteSpace(pathPython) && File.Exists(pathPython))
            {
                var info = HiddenStart(pathPython);
                info.WorkingDirectory = repo;
                info.ArgumentList.Add(entry);
                return info;
            }
            return null;
        }

        private static ProcessStartInfo HiddenStart(string executable)
            => new(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };

        private static string? FindRepositoryRoot(string start)
        {
            var current = new DirectoryInfo(start);
            while (current is not null)
            {
                if (File.Exists(Path.Combine(current.FullName, "run_agent.py")))
                    return current.FullName;
                current = current.Parent;
            }
            return null;
        }
    }
}
