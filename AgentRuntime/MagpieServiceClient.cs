using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace Magpie.AgentRuntime
{
    internal static class MagpieServiceClient
    {
        private static readonly HttpClient Http = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        internal static async Task<AgentRuntimeHealth> GetHealthAsync()
        {
            try
            {
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4)))
                using (var response = await Http.GetAsync(MagpieSettings.ServiceBaseUrl + "/health", cts.Token).ConfigureAwait(false))
                {
                    string raw = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                        return AgentRuntimeHealth.Failed("health returned HTTP " + (int)response.StatusCode + ": " + raw);

                var root = JObject.Parse(raw);
                var data = root["data"];
                return new AgentRuntimeHealth
                {
                    Success = root["success"]?.Value<bool>() == true,
                    ModelConfigured = data?["model_configured"]?.Value<bool>() == true,
                    HostBridgeAvailable = data?["host_bridge_available"]?.Value<bool?>() != false,
                    GraphConfigured = data?["graph_configured"]?.Value<bool?>(),
                    Runtime = data?["runtime"]?.ToString(),
                    HostBridgeError = data?["host_bridge_error"]?.ToString()
                };
                }
            }
            catch (Exception ex)
            {
                return AgentRuntimeHealth.Failed(ex.Message);
            }
        }

        internal static async Task<string> GetHealthStatusTextAsync()
        {
            var startup = await AgentServiceProcessManager.EnsureRunningAsync(CancellationToken.None).ConfigureAwait(false);
            return DescribeHealth(startup);
        }

        internal static string DescribeHealth(AgentServiceStartupResult startup)
        {
            var health = startup.Health;
            if (!health.Success)
            {
                if (!startup.StartConfigured)
                    return "agent_service unavailable at " + MagpieSettings.ServiceBaseUrl + ". Set MAGPIE_AGENT_SERVICE_COMMAND to enable auto-start. Detail: " + health.Error;
                if (startup.ProcessExitedEarly)
                    return "agent_service process exited before /health became ready at " + MagpieSettings.ServiceBaseUrl + ". Detail: " + health.Error;
                if (startup.TimedOut)
                    return "agent_service auto-start timed out at " + MagpieSettings.ServiceBaseUrl + ". Detail: " + health.Error;
                return "agent_service unavailable at " + MagpieSettings.ServiceBaseUrl + ": " + health.Error;
            }

            string runtime = string.IsNullOrWhiteSpace(health.Runtime)
                ? MagpieSettings.RuntimeMode
                : health.Runtime;
            if (health.IsGraphReady)
                return startup.StartedProcess
                    ? "agent_service auto-started. LangGraph runtime is configured."
                    : "agent_service connected. LangGraph runtime is configured.";
            if (!health.HostBridgeAvailable)
                return "agent_service connected. Host bridge is still connecting to Grasshopper.";
            if (health.ModelConfigured)
                return startup.StartedProcess
                    ? "agent_service auto-started. " + runtime + " model is configured."
                    : "agent_service connected. " + runtime + " model is configured.";
            return "agent_service connected. Model is not configured; compatibility workflow fallback will be used.";
        }

        internal static async Task SendStreamingAsync(
            string sessionId,
            string text,
            string userGoal,
            string endpoint,
            Action<JObject> onEvent,
            CancellationToken cancellationToken)
        {
            AddGhLog.Info("MagpieServiceClient.SendStreamingAsync begin " + endpoint);
            var startup = await AgentServiceProcessManager.EnsureRunningAsync(cancellationToken).ConfigureAwait(false);
            var health = startup.Health;
            if (!health.Success && string.IsNullOrWhiteSpace(MagpieSettings.InvokePath))
            {
                AddGhLog.Warn("MagpieServiceClient.SendStreamingAsync startup failed: " + health.Error);
                if (!startup.StartConfigured)
                    throw new InvalidOperationException("agent_service is not reachable at " + MagpieSettings.ServiceBaseUrl + ". Set MAGPIE_AGENT_SERVICE_COMMAND to auto-start it, or start the service manually. Detail: " + health.Error);
                if (startup.ProcessExitedEarly)
                    throw new InvalidOperationException("agent_service process exited before /health became ready at " + MagpieSettings.ServiceBaseUrl + ". Detail: " + health.Error);
                if (startup.TimedOut)
                    throw new InvalidOperationException("agent_service auto-start command ran, but /health did not become ready at " + MagpieSettings.ServiceBaseUrl + ". Detail: " + health.Error);
                throw new InvalidOperationException("agent_service is not reachable at " + MagpieSettings.ServiceBaseUrl + ". Detail: " + health.Error);
            }

            var payload = new JObject
            {
                ["session_id"] = sessionId,
                ["user_input"] = text,
                ["user_goal"] = userGoal,
                ["runtime"] = MagpieSettings.RuntimeMode
            };

            using (var requestCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                requestCts.CancelAfter(TimeSpan.FromSeconds(90));
                using (var content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json"))
                using (var request = new HttpRequestMessage(HttpMethod.Post, MagpieSettings.ServiceBaseUrl + endpoint) { Content = content })
                using (var response = await Http.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    requestCts.Token).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        string raw = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        string friendly = TryExtractApiError(raw);
                        throw new InvalidOperationException(
                            string.IsNullOrWhiteSpace(friendly)
                                ? $"Request failed at {endpoint}: {raw}"
                                : $"Request failed at {endpoint}: {friendly}");
                    }

                    using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        while (!reader.EndOfStream)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            string line = await reader.ReadLineAsync().ConfigureAwait(false);
                            if (string.IsNullOrWhiteSpace(line))
                                continue;
                            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                                continue;

                            string json = line.Substring("data:".Length).Trim();
                            if (json == "[DONE]")
                                return;

                            JObject envelope;
                            try
                            {
                                envelope = JObject.Parse(json);
                            }
                            catch
                            {
                                AddGhLog.Warn("SendStreamingAsync failed to parse SSE line: " + line);
                                continue;
                            }

                            string type = envelope["type"]?.ToString() ?? "";
                            JObject payloadEvt = envelope["payload"] as JObject;
                            if (type == "agent_event" && payloadEvt != null)
                            {
                                onEvent(payloadEvt);
                            }
                            else if (type == "error")
                            {
                                string message = payloadEvt?["message"]?.ToString() ?? "Unknown streaming error.";
                                throw new InvalidOperationException($"Stream error: {message}");
                            }
                            else if (type == "done")
                            {
                                return;
                            }
                        }
                    }
                }
            }
        }

        internal static IEnumerable<string> ResolveStreamEndpoints(AgentRuntimeHealth health)
        {
            string overridePath = MagpieSettings.InvokePath;
            if (!string.IsNullOrWhiteSpace(overridePath))
            {
                yield return overridePath;
                yield break;
            }

            if (MagpieSettings.RuntimeMode == "langchain")
            {
                yield return health.ModelConfigured ? "/agent/invoke/stream" : "/workflow/run/stream";
                yield break;
            }

            yield return "/graph/invoke/stream";
            yield return "/langgraph/invoke/stream";
            yield return health.ModelConfigured ? "/agent/invoke/stream" : "/workflow/run/stream";
        }

        internal static async Task<AgentServiceResponse> SendAsync(string sessionId, string text, string userGoal, CancellationToken cancellationToken)
        {
            AddGhLog.Info("MagpieServiceClient.SendAsync begin");
            var startup = await AgentServiceProcessManager.EnsureRunningAsync(cancellationToken).ConfigureAwait(false);
            var health = startup.Health;
            if (!health.Success && string.IsNullOrWhiteSpace(MagpieSettings.InvokePath))
            {
                AddGhLog.Warn("MagpieServiceClient.SendAsync startup failed: " + health.Error);
                if (!startup.StartConfigured)
                    return AgentServiceResponse.Error("agent_service is not reachable at " + MagpieSettings.ServiceBaseUrl + ". Set MAGPIE_AGENT_SERVICE_COMMAND to auto-start it, or start the service manually. Detail: " + health.Error);
                if (startup.ProcessExitedEarly)
                    return AgentServiceResponse.Error("agent_service process exited before /health became ready at " + MagpieSettings.ServiceBaseUrl + ". Detail: " + health.Error);
                if (startup.TimedOut)
                    return AgentServiceResponse.Error("agent_service auto-start command ran, but /health did not become ready at " + MagpieSettings.ServiceBaseUrl + ". Detail: " + health.Error);
                return AgentServiceResponse.Error("agent_service is not reachable at " + MagpieSettings.ServiceBaseUrl + ". Detail: " + health.Error);
            }

            var payload = new JObject
            {
                ["session_id"] = sessionId,
                ["user_input"] = text,
                ["user_goal"] = userGoal,
                ["runtime"] = MagpieSettings.RuntimeMode
            };

            string lastFailure = "";
            foreach (string endpoint in ResolveInvokeEndpoints(health))
            {
                try
                {
                    AddGhLog.Info("MagpieServiceClient.SendAsync POST " + endpoint);
                    using (var requestCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                    {
                        requestCts.CancelAfter(TimeSpan.FromSeconds(30));
                        using (var content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json"))
                        using (var response = await Http.PostAsync(MagpieSettings.ServiceBaseUrl + endpoint, content, requestCts.Token).ConfigureAwait(false))
                        {
                            string raw = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                            if (response.IsSuccessStatusCode)
                            {
                                AddGhLog.Info("MagpieServiceClient.SendAsync success " + endpoint);
                                return ParseAgentResponse(raw);
                            }

                            string friendly = TryExtractApiError(raw);
                            if (!string.IsNullOrWhiteSpace(friendly))
                                lastFailure = "Request failed at " + endpoint + ": " + friendly;
                            else
                                lastFailure = "Request failed at " + endpoint + ": " + raw;
                            if (response.StatusCode != HttpStatusCode.NotFound && response.StatusCode != HttpStatusCode.MethodNotAllowed)
                                return AgentServiceResponse.Error(lastFailure);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    AddGhLog.Warn("MagpieServiceClient.SendAsync canceled");
                    throw;
                }
                catch (Exception ex)
                {
                    AddGhLog.Warn("MagpieServiceClient.SendAsync request failed: " + ex.Message);
                    lastFailure = "Request failed at " + endpoint + ": " + ex.Message;
                    if (!string.IsNullOrWhiteSpace(MagpieSettings.InvokePath))
                        return AgentServiceResponse.Error(lastFailure);
                }
            }

            return AgentServiceResponse.Error(string.IsNullOrWhiteSpace(lastFailure)
                ? "Request failed: no agent endpoint was available."
                : lastFailure);
        }

        private static IEnumerable<string> ResolveInvokeEndpoints(AgentRuntimeHealth health)
        {
            string overridePath = MagpieSettings.InvokePath;
            if (!string.IsNullOrWhiteSpace(overridePath))
                yield return overridePath;

            if (MagpieSettings.RuntimeMode == "langchain")
            {
                yield return health.ModelConfigured ? "/agent/invoke" : "/workflow/run";
                yield break;
            }

            yield return "/graph/invoke";
            yield return "/langgraph/invoke";
            yield return health.ModelConfigured ? "/agent/invoke" : "/workflow/run";
        }

        internal static async Task<JArray> ListSessionsAsync(CancellationToken cancellationToken)
        {
            try
            {
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cts.Token))
                using (var response = await Http.GetAsync(MagpieSettings.ServiceBaseUrl + "/sessions", linked.Token).ConfigureAwait(false))
                {
                    string raw = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        AddGhLog.Warn("MagpieServiceClient.ListSessionsAsync failed: " + (int)response.StatusCode + " " + raw);
                        return new JArray();
                    }
                    var root = JObject.Parse(raw);
                    var data = root["data"] as JArray;
                    return data ?? new JArray();
                }
            }
            catch (Exception ex)
            {
                AddGhLog.Warn("MagpieServiceClient.ListSessionsAsync failed: " + ex.Message);
                return new JArray();
            }
        }

        internal static async Task<JObject> GetSessionAsync(string sessionId, CancellationToken cancellationToken)
        {
            try
            {
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cts.Token))
                using (var response = await Http.GetAsync(MagpieSettings.ServiceBaseUrl + "/sessions/" + Uri.EscapeDataString(sessionId), linked.Token).ConfigureAwait(false))
                {
                    string raw = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        AddGhLog.Warn("MagpieServiceClient.GetSessionAsync failed: " + (int)response.StatusCode + " " + raw);
                        return null;
                    }
                    var root = JObject.Parse(raw);
                    return root["data"] as JObject;
                }
            }
            catch (Exception ex)
            {
                AddGhLog.Warn("MagpieServiceClient.GetSessionAsync failed: " + ex.Message);
                return null;
            }
        }

        internal static async Task<bool> DeleteSessionAsync(string sessionId, CancellationToken cancellationToken)
        {
            try
            {
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cts.Token))
                using (var response = await Http.DeleteAsync(MagpieSettings.ServiceBaseUrl + "/sessions/" + Uri.EscapeDataString(sessionId), linked.Token).ConfigureAwait(false))
                {
                    string raw = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        AddGhLog.Warn("MagpieServiceClient.DeleteSessionAsync failed: " + (int)response.StatusCode + " " + raw);
                        return false;
                    }
                    var root = JObject.Parse(raw);
                    return root["success"]?.Value<bool>() == true;
                }
            }
            catch (Exception ex)
            {
                AddGhLog.Warn("MagpieServiceClient.DeleteSessionAsync failed: " + ex.Message);
                return false;
            }
        }

        internal static async Task<bool> RenameSessionAsync(string sessionId, string title, CancellationToken cancellationToken)
        {
            try
            {
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cts.Token))
                using (var content = new StringContent(new JObject { ["title"] = title ?? "" }.ToString(), Encoding.UTF8, "application/json"))
                using (var request = new HttpRequestMessage(new HttpMethod("PATCH"), MagpieSettings.ServiceBaseUrl + "/sessions/" + Uri.EscapeDataString(sessionId)) { Content = content })
                using (var response = await Http.SendAsync(request, linked.Token).ConfigureAwait(false))
                {
                    string raw = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        AddGhLog.Warn("MagpieServiceClient.RenameSessionAsync failed: " + (int)response.StatusCode + " " + raw);
                        return false;
                    }
                    var root = JObject.Parse(raw);
                    return root["success"]?.Value<bool>() == true;
                }
            }
            catch (Exception ex)
            {
                AddGhLog.Warn("MagpieServiceClient.RenameSessionAsync failed: " + ex.Message);
                return false;
            }
        }

        internal static string TryExtractAgentText(string raw)
        {
            try
            {
                var root = JObject.Parse(raw);
                var data = root["data"];
                if (data == null) return raw;

                string outputText = data["output_text"]?.ToString();
                if (!string.IsNullOrWhiteSpace(outputText))
                    return outputText;

                outputText = data["data"]?["output_text"]?.ToString();
                if (!string.IsNullOrWhiteSpace(outputText))
                    return outputText;

                return data.ToString();
            }
            catch
            {
                return raw;
            }
        }

        internal static AgentServiceResponse ParseAgentResponse(string raw)
        {
            try
            {
                var root = JObject.Parse(raw);
                var data = root["data"] as JObject;
                if (data == null)
                    return AgentServiceResponse.Text(TryExtractAgentText(raw));

                var response = new AgentServiceResponse
                {
                    Mode = data["mode"]?.ToString() ?? "agent",
                    Status = data["status"]?.ToString() ?? "completed",
                    FinalText = data["final_text"]?.ToString() ?? data["output_text"]?.ToString() ?? "",
                    OutputText = data["output_text"]?.ToString() ?? data["final_text"]?.ToString() ?? "",
                    Raw = data
                };

                var events = data["events"] as JArray;
                if (events != null)
                {
                    foreach (var item in events)
                    {
                        if (item is JObject evt)
                            response.Events.Add(evt);
                    }
                }

                if (response.Events.Count == 0 && !string.IsNullOrWhiteSpace(response.OutputText))
                    response.Events.Add(new JObject { ["type"] = "final", ["text"] = response.OutputText, ["status"] = response.Status });

                return response;
            }
            catch
            {
                return AgentServiceResponse.Text(TryExtractAgentText(raw));
            }
        }

        private static string TryExtractApiError(string raw)
        {
            try
            {
                var root = JObject.Parse(raw);
                string error = root["error"]?.ToString();
                if (!string.IsNullOrWhiteSpace(error))
                    return error;

                error = root["detail"]?.ToString();
                if (!string.IsNullOrWhiteSpace(error))
                    return error;

                return "";
            }
            catch
            {
                return "";
            }
        }
    }

    internal sealed class AgentRuntimeHealth
    {
        internal static AgentRuntimeHealth Failed(string error)
        {
            return new AgentRuntimeHealth
            {
                Success = false,
                Error = string.IsNullOrWhiteSpace(error) ? "unknown error" : error
            };
        }

        internal bool Success { get; set; }
        internal bool ModelConfigured { get; set; }
        internal bool HostBridgeAvailable { get; set; } = true;
        internal bool? GraphConfigured { get; set; }
        internal string Runtime { get; set; }
        internal string Error { get; set; }
        internal string HostBridgeError { get; set; }

        internal bool IsGraphReady
        {
            get
            {
                if (GraphConfigured.HasValue)
                    return GraphConfigured.Value;
                return ModelConfigured && string.Equals(Runtime, "langgraph", StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    internal sealed class AgentServiceResponse
    {
        internal string Mode { get; set; }
        internal string Status { get; set; }
        internal string FinalText { get; set; }
        internal string OutputText { get; set; }
        internal JObject Raw { get; set; }
        internal List<JObject> Events { get; } = new List<JObject>();
        internal bool IsError { get; set; }
        internal string ErrorText { get; set; }

        internal static AgentServiceResponse Error(string text)
        {
            return new AgentServiceResponse
            {
                IsError = true,
                ErrorText = text ?? "Unknown error."
            };
        }

        internal static AgentServiceResponse Text(string text)
        {
            var response = new AgentServiceResponse
            {
                Mode = "agent",
                Status = "completed",
                FinalText = text ?? "",
                OutputText = text ?? ""
            };
            response.Events.Add(new JObject
            {
                ["type"] = "final",
                ["text"] = text ?? "",
                ["status"] = "completed"
            });
            return response;
        }
    }
}
