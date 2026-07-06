using System;
using Grasshopper;
using System.IO;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Magpie.AgentRuntime
{
    internal static class MagpieSettings
    {
        private const string DefaultServiceBaseUrl = "http://127.0.0.1:8000";
        private const string DefaultRuntimeMode = "langgraph";
        private static string _autoServiceBaseUrl;

        internal static string ServiceBaseUrl => ResolveServiceBaseUrl();
        internal static string RuntimeMode => ResolveRuntimeMode();
        internal static bool IsDefaultServiceUrl => string.Equals(ServiceBaseUrl, DefaultServiceBaseUrl, StringComparison.OrdinalIgnoreCase);
        internal static string InvokePath => ResolvePathOverride("MAGPIE_AGENT_INVOKE_PATH", "Agent_Invoke_Path");
        internal static string ServiceStartCommand => ReadSettingOrEnvironment("MAGPIE_AGENT_SERVICE_COMMAND", "Agent_Service_Command");
        internal static string ServiceWorkingDirectory => ReadSettingOrEnvironment("MAGPIE_AGENT_SERVICE_WORKDIR", "Agent_Service_Workdir");
        internal static TimeSpan ServiceStartupTimeout => TimeSpan.FromSeconds(30);
        internal const string SessionId = "magpie-window";
        internal const string DefaultUserGoal = "Use the standalone Magpie window to work with Grasshopper through the local host bridge.";
        internal const string WindowTitle = "Magpie";
        internal const string WindowSubtitle = "Standalone LangGraph runtime with local Grasshopper host tools";

        internal static AgentModelLaunchSettings GetCurrentAgentModelLaunchSettings()
        {
            string providerId = ReadProviderId(Instances.Settings.GetValue("AI_CurrentProvider", "deepseek"));
            string defaultBaseUrl = providerId == "qwen"
                ? "https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions"
                : "https://api.deepseek.com/chat/completions";
            string defaultModel = providerId == "qwen"
                ? "qwen3.6-plus"
                : "deepseek-v4-flash";

            string baseUrl = Instances.Settings.GetValue(GetProviderSettingKey(providerId, "BaseUrl"), "");
            if (string.IsNullOrWhiteSpace(baseUrl) && providerId == "deepseek")
                baseUrl = Instances.Settings.GetValue("AI_API_BaseUrl", "");
            if (string.IsNullOrWhiteSpace(baseUrl))
                baseUrl = defaultBaseUrl;

            string modelName = Instances.Settings.GetValue(GetProviderSettingKey(providerId, "ModelName"), "");
            if (string.IsNullOrWhiteSpace(modelName) && providerId == "deepseek")
                modelName = Instances.Settings.GetValue("AI_ModelName", "");
            if (string.IsNullOrWhiteSpace(modelName))
                modelName = defaultModel;

            return new AgentModelLaunchSettings
            {
                ProviderId = providerId,
                BaseUrl = NormalizeOpenAiCompatibleBaseUrl(baseUrl),
                ModelName = modelName.Trim(),
                ApiKey = ReadStoredApiKey(providerId)
            };
        }

        internal static IReadOnlyDictionary<string, string> BuildAgentServiceEnvironment()
        {
            var settings = GetCurrentAgentModelLaunchSettings();
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["OPENAI_BASE_URL"] = settings.BaseUrl ?? "",
                ["OPENAI_MODEL"] = settings.ModelName ?? "",
                ["OPENAI_API_KEY"] = settings.ApiKey ?? ""
            };
            return values;
        }

        internal static string AgentServiceModelConfigPath
        {
            get
            {
                string root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Magpie");
                return Path.Combine(root, "agent-service-model.json");
            }
        }

        internal static void PersistAgentServiceModelConfig()
        {
            try
            {
                var settings = GetCurrentAgentModelLaunchSettings();
                string path = AgentServiceModelConfigPath;
                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);

                var payload = new JObject
                {
                    ["providerId"] = settings.ProviderId ?? "",
                    ["baseUrl"] = settings.BaseUrl ?? "",
                    ["model"] = settings.ModelName ?? "",
                    ["apiKey"] = settings.ApiKey ?? ""
                };
                File.WriteAllText(path, payload.ToString(), new UTF8Encoding(false));
                AddGhLog.Info("Persisted agent model config: " + path);
            }
            catch (Exception ex)
            {
                AddGhLog.Warn("PersistAgentServiceModelConfig failed: " + ex.Message);
            }
        }

        internal static void SetAutoServiceBaseUrl(string url)
        {
            _autoServiceBaseUrl = string.IsNullOrWhiteSpace(url) ? null : url.Trim().TrimEnd('/');
        }

        private static string ResolveServiceBaseUrl()
        {
            if (!string.IsNullOrWhiteSpace(_autoServiceBaseUrl))
                return _autoServiceBaseUrl;

            string raw = ReadSettingOrEnvironment("MAGPIE_AGENT_SERVICE_URL", "Agent_Service_Url");
            if (string.IsNullOrWhiteSpace(raw))
                return DefaultServiceBaseUrl;

            return raw.Trim().TrimEnd('/');
        }

        private static string ResolveRuntimeMode()
        {
            string raw = Environment.GetEnvironmentVariable("MAGPIE_AGENT_RUNTIME");
            if (string.IsNullOrWhiteSpace(raw))
                return DefaultRuntimeMode;

            raw = raw.Trim().ToLowerInvariant();
            return raw == "langchain" ? "langchain" : "langgraph";
        }

        private static string ResolvePathOverride(string environmentName, string settingName)
        {
            string raw = ReadSettingOrEnvironment(environmentName, settingName);
            if (string.IsNullOrWhiteSpace(raw))
                return "";

            raw = raw.Trim();
            return raw.StartsWith("/", StringComparison.Ordinal) ? raw : "/" + raw;
        }

        private static string ReadEnvironment(string name)
        {
            return Environment.GetEnvironmentVariable(name) ?? "";
        }

        private static string ReadSettingOrEnvironment(string environmentName, string settingName)
        {
            string fromSettings = "";
            try
            {
                fromSettings = Instances.Settings.GetValue(settingName, "");
            }
            catch
            {
            }

            if (!string.IsNullOrWhiteSpace(fromSettings))
                return fromSettings;

            string fromEnvironment = ReadEnvironment(environmentName);
            if (!string.IsNullOrWhiteSpace(fromEnvironment))
                return fromEnvironment;

            if (string.Equals(settingName, "Agent_Service_Command", StringComparison.Ordinal))
                return ResolveDefaultAgentServiceCommand();
            if (string.Equals(settingName, "Agent_Service_Workdir", StringComparison.Ordinal))
                return ResolveDefaultAgentServiceWorkingDirectory();

            return "";
        }

        private static string ResolveDefaultAgentServiceWorkingDirectory()
        {
            string repoRoot = ResolveRepoRoot();
            if (string.IsNullOrWhiteSpace(repoRoot))
                return "";

            string candidate = Path.Combine(repoRoot, "agent_service");
            return Directory.Exists(candidate) ? candidate : "";
        }

        private static string ResolveDefaultAgentServiceCommand()
        {
            string workdir = ResolveDefaultAgentServiceWorkingDirectory();
            if (string.IsNullOrWhiteSpace(workdir))
                return "";

            string runService = Path.Combine(workdir, "run_service.py");
            if (!File.Exists(runService))
                return "";

            return "python -m uvicorn app.main:app --host 127.0.0.1 --port 8000";
        }

        private static string ResolveRepoRoot()
        {
            try
            {
                string assemblyDir = Path.GetDirectoryName(typeof(MagpieSettings).Assembly.Location);
                string[] candidates =
                {
                    assemblyDir,
                    Path.Combine(assemblyDir ?? "", "..", "..", ".."),
                    AppDomain.CurrentDomain.BaseDirectory,
                    Environment.CurrentDirectory
                };

                foreach (string candidate in candidates)
                {
                    if (string.IsNullOrWhiteSpace(candidate))
                        continue;

                    string full = Path.GetFullPath(candidate);
                    if (Directory.Exists(Path.Combine(full, "agent_service")))
                        return full;
                }
            }
            catch
            {
            }

            return "";
        }

        private static string NormalizeOpenAiCompatibleBaseUrl(string raw)
        {
            string value = (raw ?? "").Trim().TrimEnd('/');
            if (string.IsNullOrWhiteSpace(value))
                return "";

            if (value.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
                return value.Substring(0, value.Length - "/chat/completions".Length);

            return value;
        }

        private static string ReadStoredApiKey(string providerId)
        {
            string dpapi = Instances.Settings.GetValue(GetProviderSettingKey(providerId, "API_Key_DPAPI"), "");
            if (!string.IsNullOrWhiteSpace(dpapi) && ApiCredentialStore.TryUnprotectFromBase64(dpapi, out string plain))
                return plain ?? "";

            string plainFallback = Instances.Settings.GetValue(GetProviderSettingKey(providerId, "API_Key"), "");
            if (!string.IsNullOrWhiteSpace(plainFallback))
                return plainFallback;

            return providerId == "deepseek"
                ? Instances.Settings.GetValue("AI_API_Key", "")
                : "";
        }

        private static string ReadProviderId(string providerId)
        {
            return string.Equals(providerId, "qwen", StringComparison.OrdinalIgnoreCase)
                ? "qwen"
                : "deepseek";
        }

        private static string GetProviderSettingKey(string providerId, string name)
        {
            return "AI_" + providerId + "_" + name;
        }
    }

    internal sealed class AgentModelLaunchSettings
    {
        internal string ProviderId { get; set; }
        internal string BaseUrl { get; set; }
        internal string ModelName { get; set; }
        internal string ApiKey { get; set; }
    }
}
