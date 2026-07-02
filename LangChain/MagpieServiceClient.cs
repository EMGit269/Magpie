using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Magpie.LangChain
{
    internal static class MagpieServiceClient
    {
        private static readonly HttpClient Http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(45)
        };

        internal static async Task<bool> IsModelConfiguredAsync()
        {
            try
            {
                string raw = await Http.GetStringAsync(MagpieSettings.ServiceBaseUrl + "/health").ConfigureAwait(false);
                var root = JObject.Parse(raw);
                return root["data"]?["model_configured"]?.Value<bool>() == true;
            }
            catch
            {
                return false;
            }
        }

        internal static async Task<string> GetHealthStatusTextAsync()
        {
            string raw = await Http.GetStringAsync(MagpieSettings.ServiceBaseUrl + "/health").ConfigureAwait(false);
            var root = JObject.Parse(raw);
            bool ok = root["success"]?.Value<bool>() == true;
            bool modelConfigured = root["data"]?["model_configured"]?.Value<bool>() == true;
            if (!ok)
                return "agent_service responded but reported failure.";
            return modelConfigured
                ? "agent_service connected. Main agent model is configured."
                : "agent_service connected. Main agent model is not configured; workflow fallback will be used.";
        }

        internal static async Task<string> SendAsync(string sessionId, string text, string userGoal)
        {
            bool modelConfigured = await IsModelConfiguredAsync().ConfigureAwait(false);
            string endpoint = modelConfigured ? "/agent/invoke" : "/workflow/run";

            var payload = new JObject
            {
                ["session_id"] = sessionId,
                ["user_input"] = text,
                ["user_goal"] = userGoal
            };

            using (var content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json"))
            using (var response = await Http.PostAsync(MagpieSettings.ServiceBaseUrl + endpoint, content).ConfigureAwait(false))
            {
                string raw = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    return "Request failed: " + raw;
                return TryExtractAgentText(raw);
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
    }
}
