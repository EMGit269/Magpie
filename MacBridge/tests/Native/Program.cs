using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Magpie.MacBridge;
using Newtonsoft.Json.Linq;

// Small executable harness: real HTTP/SSE and process startup, without launching Rhino.
namespace Magpie.MacBridge { internal static class Bridge { internal static int Port => 8765; } }
internal static class Program
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static async Task Main()
    {
        var launcher = MacAgentSettings.Read(MacAgentSettings.SettingsPath);
        var previousDirectory = Environment.GetEnvironmentVariable("MAGPIE_MAC_SUPPORT_DIR");
        var temp = Path.Combine(Path.GetTempPath(), "magpie-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        Environment.SetEnvironmentVariable("MAGPIE_MAC_SUPPORT_DIR", temp);
        var settings = MacAgentSettings.Load();
        try
        {
            foreach (var url in new[] { "https://example.com", "http://evil.test", "http://localhost:8123/path", "http://localhost:8123/?secret=1" })
            {
                try { MacAgentSettings.ValidateServiceUrl(url); throw new Exception("URL accepted: " + url); }
                catch (InvalidOperationException) { }
            }
            var model = new JObject { ["apiKey"] = "test-private-key", ["baseUrl"] = "https://api.deepseek.com", ["model"] = "test-model" };
            MacAgentSettings.WritePrivate(MacAgentSettings.ModelPath, model);
            Check(!settings.PublicModelSettings().ToString().Contains("test-private-key"), "Secret leaked to WebView");
            if (!OperatingSystem.IsWindows()) Check(File.GetUnixFileMode(MacAgentSettings.ModelPath) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "Credential permissions must be 0600");
            var listen = new TcpListener(IPAddress.Loopback, 0); listen.Start();
            var port = ((IPEndPoint)listen.LocalEndpoint).Port; listen.Stop();
            MacAgentSettings.WritePrivate(MacAgentSettings.SettingsPath, new JObject {
                ["serviceUrl"] = "http://127.0.0.1:" + port, ["pythonPath"] = "/not/a/python",
                ["serviceDirectory"] = temp });
            settings = MacAgentSettings.Load();
            var service = new MacAgentService(settings);
            using (var server = new HttpListener())
            {
                server.Prefixes.Add(settings.ServiceUrl + "/"); server.Start();
                var worker = Task.Run(async () => {
                    while (server.IsListening)
                    {
                        HttpListenerContext ctx;
                        try { ctx = await server.GetContextAsync(); } catch { return; }
                        _ = Task.Run(async () => {
                            try
                            {
                                if (ctx.Request.Url.AbsolutePath == "/health")
                                {
                                    var bytes = Encoding.UTF8.GetBytes("{\"success\":true,\"data\":{\"status\":\"ok\",\"model_configured\":true}}");
                                    await ctx.Response.OutputStream.WriteAsync(bytes);
                                }
                                else
                                {
                                    ctx.Response.ContentType = "text/event-stream";
                                    using var reader = new StreamReader(ctx.Request.InputStream);
                                    var payload = JObject.Parse(await reader.ReadToEndAsync());
                                    if (payload["user_input"]?.ToString() == "cancel") { await Task.Delay(5000); }
                                    else
                                    {
                                        var bytes = Encoding.UTF8.GetBytes("data: {\"type\":\"agent_event\",\"payload\":{\"type\":\"final\",\"text\":\"Hello Mac\"}}\n\ndata: {\"type\":\"done\"}\n\n");
                                        await ctx.Response.OutputStream.WriteAsync(bytes);
                                    }
                                }
                            }
                            catch { }
                            finally { ctx.Response.Close(); }
                        });
                    }
                });
                Check(await service.EnsureRunning(CancellationToken.None) != null, "Reuse healthy external service even with invalid launcher paths");
                try {
                    await service.Stream("test", "hello", _ => { }, CancellationToken.None, "bound-document");
                    throw new Exception("Old service silently accepted a bound request");
                } catch (InvalidOperationException error) {
                    Check(error.Message.Contains("版本过旧"), "Old service must explicitly reject bound requests");
                }
                var count = 0;
                await service.Stream("test", "hello", evt => { Check(evt["text"].ToString() == "Hello Mac", "SSE payload"); count++; }, CancellationToken.None);
                Check(count == 1, "SSE delivered once");
                using (var ct = new CancellationTokenSource(250))
                {
                    try { await service.Stream("test", "cancel", _ => { }, ct.Token); throw new Exception("Cancellation ignored"); }
                    catch (OperationCanceledException) { }
                }
                MacAgentService.StopOwnedProcess();
                Check(await service.Health(CancellationToken.None) != null, "Closing owned process must preserve external service");
                server.Stop(); await worker;
            }
            Console.WriteLine("PASS: local URL validation, secret redaction, file permissions, service reuse, SSE, cancellation, external ownership");

            // Start the actual launcher in a fresh port, without disturbing the user's service.
            MacAgentSettings.WritePrivate(MacAgentSettings.ModelPath, new JObject());
            var source = launcher;
            source["serviceUrl"] = "http://127.0.0.1:" + port;
            MacAgentSettings.WritePrivate(MacAgentSettings.SettingsPath, source);
            var auto = new MacAgentService(MacAgentSettings.Load());
            Check(await auto.EnsureRunning(CancellationToken.None) != null, "Actual Python auto-start failed");
            Check(await auto.EnsureRunning(CancellationToken.None) != null, "Auto-start must be idempotent");
            MacAgentService.StopOwnedProcess();
            Console.WriteLine("PASS: real Python auto-start, reuse and owned-process shutdown");
        }
        finally
        {
            MacAgentService.StopOwnedProcess();
            Environment.SetEnvironmentVariable("MAGPIE_MAC_SUPPORT_DIR", previousDirectory);
            Directory.Delete(temp, true);
        }
    }
}
