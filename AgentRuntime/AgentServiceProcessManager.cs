using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Magpie.AgentRuntime
{
    internal static class AgentServiceProcessManager
    {
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);
        private static Process _process;
        private static readonly object OutputLock = new object();
        private static string _lastProcessOutput = "";

        internal static async Task<AgentServiceStartupResult> EnsureRunningAsync(CancellationToken cancellationToken)
        {
            AddGhLog.Info("AgentServiceProcessManager.EnsureRunningAsync begin");
            var health = await MagpieServiceClient.GetHealthAsync().ConfigureAwait(false);
            if (health.Success)
            {
                if (!MagpieSettings.IsDefaultServiceUrl || (_process != null && !_process.HasExited))
                {
                    AddGhLog.Info("AgentServiceProcessManager health already ok");
                    return AgentServiceStartupResult.Available(health);
                }
                AddGhLog.Info("AgentServiceProcessManager health ok on default URL but no tracked process; will start a fresh instance");
            }

            string command = MagpieSettings.ServiceStartCommand;
            if (string.IsNullOrWhiteSpace(command))
            {
                AddGhLog.Warn("AgentServiceProcessManager no start command configured");
                return AgentServiceStartupResult.NotConfigured(health);
            }

            await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                health = await MagpieServiceClient.GetHealthAsync().ConfigureAwait(false);
                if (health.Success)
                {
                    if (!MagpieSettings.IsDefaultServiceUrl || (_process != null && !_process.HasExited))
                    {
                        AddGhLog.Info("AgentServiceProcessManager health ok after gate");
                        return AgentServiceStartupResult.Available(health);
                    }
                    AddGhLog.Info("AgentServiceProcessManager health ok after gate on default URL but no tracked process; will start a fresh instance");
                }

                if (_process == null || _process.HasExited)
                {
                    int port = FindFreeTcpPort();
                    string adjustedCommand = InjectPortIntoCommand(command, port);
                    MagpieSettings.SetAutoServiceBaseUrl($"http://127.0.0.1:{port}");
                    AddGhLog.Info("AgentServiceProcessManager starting process: " + adjustedCommand + " workdir=" + (MagpieSettings.ServiceWorkingDirectory ?? ""));
                    _process = StartServiceProcess(adjustedCommand, MagpieSettings.ServiceWorkingDirectory);
                }
            }
            finally
            {
                Gate.Release();
            }

            DateTime deadline = DateTime.UtcNow + MagpieSettings.ServiceStartupTimeout;
            string lastError = health.Error;
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_process != null && _process.HasExited)
                {
                    string detail = "Process exited with code " + _process.ExitCode + ". " + GetLastProcessOutput();
                    AddGhLog.Warn("AgentServiceProcessManager process exited before health ready: " + detail);
                    return AgentServiceStartupResult.ProcessExited(AgentRuntimeHealth.Failed(detail));
                }
                await Task.Delay(700, cancellationToken).ConfigureAwait(false);
                health = await MagpieServiceClient.GetHealthAsync().ConfigureAwait(false);
                if (health.Success)
                {
                    AddGhLog.Info("AgentServiceProcessManager health ok after process start");
                    return AgentServiceStartupResult.Started(health);
                }
                lastError = health.Error;
            }

            string timeoutDetail = lastError;
            if (_process != null && _process.HasExited)
                timeoutDetail = "Process exited with code " + _process.ExitCode + ". " + GetLastProcessOutput();
            else
                timeoutDetail = (timeoutDetail ?? "Timed out.") + " " + GetLastProcessOutput();

            AddGhLog.Warn("AgentServiceProcessManager startup timed out: " + timeoutDetail);
            return AgentServiceStartupResult.StartTimedOut(AgentRuntimeHealth.Failed(timeoutDetail));
        }

        internal static void RestartForSettingsChange()
        {
            try
            {
                Gate.Wait();
                try
                {
                    if (_process != null)
                    {
                        if (!_process.HasExited)
                        {
                            AddGhLog.Info("AgentServiceProcessManager stopping process to apply updated model settings");
                            try { _process.Kill(); } catch { }
                            try { _process.WaitForExit(3000); } catch { }
                        }

                        try { _process.Dispose(); } catch { }
                        _process = null;
                    }

                    lock (OutputLock)
                    {
                        _lastProcessOutput = "";
                    }
                }
                finally
                {
                    Gate.Release();
                }
            }
            catch (Exception ex)
            {
                AddGhLog.Warn("AgentServiceProcessManager restart failed: " + ex.Message);
            }
        }

        private static int FindFreeTcpPort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            try
            {
                listener.Start();
                return ((IPEndPoint)listener.LocalEndpoint).Port;
            }
            finally
            {
                listener.Stop();
            }
        }

        private static string InjectPortIntoCommand(string command, int port)
        {
            string portFlag = $"--port {port}";
            if (Regex.IsMatch(command, @"--port\s+\d+"))
                return Regex.Replace(command, @"--port\s+\d+", portFlag);
            return command.TrimEnd() + " " + portFlag;
        }

        private static Process StartServiceProcess(string command, string workingDirectory)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c " + command,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            if (!string.IsNullOrWhiteSpace(workingDirectory))
            {
                string full = Path.GetFullPath(workingDirectory);
                if (Directory.Exists(full))
                    startInfo.WorkingDirectory = full;
            }

            foreach (var pair in MagpieSettings.BuildAgentServiceEnvironment())
            {
                startInfo.EnvironmentVariables[pair.Key] = pair.Value ?? "";
            }

            var process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true
            };
            process.OutputDataReceived += (s, e) => CaptureProcessOutput("stdout", e.Data);
            process.ErrorDataReceived += (s, e) => CaptureProcessOutput("stderr", e.Data);
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return process;
        }

        private static void CaptureProcessOutput(string channel, string data)
        {
            if (string.IsNullOrWhiteSpace(data))
                return;

            string line = "[" + channel + "] " + data.Trim();
            AddGhLog.Info("agent_service " + line);
            lock (OutputLock)
            {
                _lastProcessOutput = line.Length > 800 ? line.Substring(0, 800) : line;
            }
        }

        private static string GetLastProcessOutput()
        {
            lock (OutputLock)
            {
                return string.IsNullOrWhiteSpace(_lastProcessOutput)
                    ? "No process output captured."
                    : "Last output: " + _lastProcessOutput;
            }
        }
    }

    internal sealed class AgentServiceStartupResult
    {
        internal AgentRuntimeHealth Health { get; private set; }
        internal bool StartedProcess { get; private set; }
        internal bool StartConfigured { get; private set; }
        internal bool TimedOut { get; private set; }
        internal bool ProcessExitedEarly { get; private set; }

        internal static AgentServiceStartupResult Available(AgentRuntimeHealth health)
        {
            return new AgentServiceStartupResult
            {
                Health = health,
                StartConfigured = true
            };
        }

        internal static AgentServiceStartupResult Started(AgentRuntimeHealth health)
        {
            return new AgentServiceStartupResult
            {
                Health = health,
                StartedProcess = true,
                StartConfigured = true
            };
        }

        internal static AgentServiceStartupResult NotConfigured(AgentRuntimeHealth health)
        {
            return new AgentServiceStartupResult
            {
                Health = health,
                StartConfigured = false
            };
        }

        internal static AgentServiceStartupResult StartTimedOut(AgentRuntimeHealth health)
        {
            return new AgentServiceStartupResult
            {
                Health = health,
                StartConfigured = true,
                TimedOut = true
            };
        }

        internal static AgentServiceStartupResult ProcessExited(AgentRuntimeHealth health)
        {
            return new AgentServiceStartupResult
            {
                Health = health,
                StartConfigured = true,
                ProcessExitedEarly = true
            };
        }
    }
}
