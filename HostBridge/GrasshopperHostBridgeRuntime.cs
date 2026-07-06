using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Magpie.HostBridge
{
    internal sealed class GrasshopperHostBridgeRuntime
    {
        private readonly GrasshopperHostToolExecutor _executor;
        private readonly object _serverLock = new object();
        private HttpListener _listener;
        private CancellationTokenSource _serverCts;
        private Task _serverTask;
        private readonly int _port;

        public GrasshopperHostBridgeRuntime(GrasshopperHostToolExecutor executor, int port = 8765)
        {
            _executor = executor ?? throw new ArgumentNullException(nameof(executor));
            _port = port;
        }

        public void EnsureServer()
        {
            lock (_serverLock)
            {
                if (_listener != null || _serverTask != null)
                    return;

                try
                {
                    _serverCts = new CancellationTokenSource();
                    _listener = new HttpListener();
                    _listener.Prefixes.Add("http://127.0.0.1:" + _port + "/");
                    _listener.Start();
                    _serverTask = Task.Run(() => RunServerLoopAsync(_serverCts.Token));
                    AddGhLog.Info("Host bridge server started at http://127.0.0.1:" + _port + "/");
                }
                catch (Exception ex)
                {
                    AddGhLog.Warn("Failed to start host bridge server: " + ex.Message);
                    try { _listener?.Close(); } catch { }
                    try { _serverCts?.Dispose(); } catch { }
                    _listener = null;
                    _serverCts = null;
                    _serverTask = null;
                }
            }
        }

        public void StopServer()
        {
            lock (_serverLock)
            {
                try { _serverCts?.Cancel(); } catch { }
                try { _listener?.Stop(); } catch { }
                try { _listener?.Close(); } catch { }
                try { _serverCts?.Dispose(); } catch { }
                _listener = null;
                _serverCts = null;
                _serverTask = null;
            }
        }

        private async Task RunServerLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                HttpListenerContext context = null;
                try
                {
                    context = await _listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (HttpListenerException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    AddGhLog.Warn("Host bridge listener loop failed: " + ex.Message);
                    break;
                }

                if (context == null)
                    continue;

                _ = Task.Run(() => HandleHttpRequestAsync(context), CancellationToken.None);
            }
        }

        private async Task HandleHttpRequestAsync(HttpListenerContext context)
        {
            var response = context.Response;
            response.ContentType = "application/json; charset=utf-8";
            response.Headers["Cache-Control"] = "no-store";

            try
            {
                string path = context.Request.Url?.AbsolutePath?.Trim('/') ?? "";
                string method = (context.Request.HttpMethod ?? "GET").ToUpperInvariant();

                if (method == "GET" && string.Equals(path, "health", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteJsonResponseAsync(response, new JObject
                    {
                        ["status"] = "ok",
                        ["service"] = "magpie-host-bridge",
                        ["port"] = _port
                    }).ConfigureAwait(false);
                    return;
                }

                if (method == "GET" && string.Equals(path, "tools/manifest", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteJsonResponseAsync(response, _executor.BuildManifestPayload()).ConfigureAwait(false);
                    return;
                }

                if (method == "POST" && string.Equals(path, "tools/invoke", StringComparison.OrdinalIgnoreCase))
                {
                    string rawBody;
                    using (var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8))
                    {
                        rawBody = await reader.ReadToEndAsync().ConfigureAwait(false);
                    }

                    JObject request = string.IsNullOrWhiteSpace(rawBody)
                        ? new JObject()
                        : JObject.Parse(rawBody);
                    JObject result = _executor.ExecuteRequest(request);
                    await WriteJsonResponseAsync(response, result).ConfigureAwait(false);
                    return;
                }

                response.StatusCode = 404;
                await WriteJsonResponseAsync(response, new JObject
                {
                    ["status"] = "error",
                    ["error"] = "Not found."
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                response.StatusCode = 500;
                await WriteJsonResponseAsync(response, new JObject
                {
                    ["status"] = "error",
                    ["error"] = ex.Message
                }).ConfigureAwait(false);
            }
            finally
            {
                try { response.OutputStream.Close(); } catch { }
            }
        }

        private static async Task WriteJsonResponseAsync(HttpListenerResponse response, JToken payload)
        {
            string json = (payload ?? new JObject()).ToString();
            byte[] buffer = Encoding.UTF8.GetBytes(json);
            response.ContentLength64 = buffer.Length;
            await response.OutputStream.WriteAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
        }
    }
}
