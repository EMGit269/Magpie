using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Magpie.MacBridge
{
    internal sealed class MacAgentSettings
    {
        internal static string SupportDirectory => Environment.GetEnvironmentVariable("MAGPIE_MAC_SUPPORT_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "Magpie");
        internal static string SettingsPath => Path.Combine(SupportDirectory, "mac-agent.json");
        internal static string ModelPath => Path.Combine(SupportDirectory, "agent-service-model.json");
        internal static string StateDirectory => Path.Combine(SupportDirectory, "agent-state");
        internal string ServiceUrl { get; private set; } = "http://127.0.0.1:8123";
        internal string PythonPath { get; private set; } = "";
        internal string ServiceDirectory { get; private set; } = "";
        internal string InvokePath { get; private set; } = "/graph/invoke/stream";
        internal Uri ServiceUri => new Uri(ServiceUrl);

        internal static MacAgentSettings Load()
        {
            var settings = new MacAgentSettings();
            var data = Read(SettingsPath);
            settings.ServiceUrl = ValidateServiceUrl(data["serviceUrl"]?.ToString() ?? settings.ServiceUrl);
            settings.PythonPath = data["pythonPath"]?.ToString() ?? "";
            settings.ServiceDirectory = data["serviceDirectory"]?.ToString() ?? "";
            settings.InvokePath = ValidateInvokePath(data["invokePath"]?.ToString());
            return settings;
        }

        internal static JObject Read(string path) => File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : new JObject();

        internal static void WritePrivate(string path, JObject value)
        {
            if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Use the Windows plugin on Windows.");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            // Set permissions at creation, before writing any credential bytes.
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temp, new FileStreamOptions {
                    Mode = FileMode.CreateNew, Access = FileAccess.Write,
                    UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite }))
                using (var writer = new StreamWriter(stream)) writer.Write(value.ToString(Formatting.Indented));
                File.Move(temp, path, true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        internal static string ValidateServiceUrl(string value)
        {
            if (!Uri.TryCreate(value.TrimEnd('/'), UriKind.Absolute, out var uri)
                || uri.Scheme != "http" || !uri.IsLoopback || uri.AbsolutePath != "/"
                || !string.IsNullOrEmpty(uri.UserInfo + uri.Query + uri.Fragment))
                throw new InvalidOperationException("Runtime URL must be a local HTTP address such as http://127.0.0.1:8123.");
            return uri.GetLeftPart(UriPartial.Authority);
        }

        private static string ValidateInvokePath(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "/graph/invoke/stream";
            if (!value.StartsWith("/") || value.StartsWith("//") || value.Any(char.IsControl) || value.Contains('?') || value.Contains('#'))
                throw new InvalidOperationException("Invoke Path must be an absolute local API path.");
            if (!value.EndsWith("/stream", StringComparison.Ordinal))
                throw new InvalidOperationException("The Mac window requires a streaming endpoint, such as /graph/invoke/stream.");
            return value;
        }

        internal JObject PublicModelSettings()
        {
            var model = Read(ModelPath);
            var current = model["provider"]?.ToString() ?? "deepseek";
            var saved = model["providers"] as JObject ?? new JObject();
            var providers = new JArray();
            foreach (var entry in new[] {
                ("deepseek", "DeepSeek", "https://api.deepseek.com", "deepseek-chat"),
                ("qwen", "Qwen", "https://dashscope.aliyuncs.com/compatible-mode/v1", "qwen-plus"),
                ("openai", "OpenAI", "https://api.openai.com/v1", "gpt-4.1-mini"),
                ("custom", "OpenAI compatible", "http://127.0.0.1:11434/v1", "") })
            {
                var profile = saved[entry.Item1] as JObject ?? (entry.Item1 == current ? model : new JObject());
                providers.Add(new JObject {
                    ["id"] = entry.Item1, ["displayName"] = entry.Item2,
                    ["defaultBaseUrl"] = entry.Item3, ["defaultModel"] = entry.Item4,
                    ["baseUrl"] = profile["baseUrl"]?.ToString() ?? entry.Item3,
                    ["modelName"] = profile["model"]?.ToString() ?? entry.Item4,
                    ["apiKeyConfigured"] = !string.IsNullOrWhiteSpace(profile["apiKey"]?.ToString()) });
            }
            return new JObject { ["currentProvider"] = current, ["providers"] = providers,
                ["service"] = new JObject { ["serviceUrl"] = ServiceUrl, ["startCommand"] = PythonPath,
                    ["workingDirectory"] = ServiceDirectory, ["invokePath"] = InvokePath } };
        }

        internal void Save(JObject payload)
        {
            var service = payload["service"] as JObject;
            var url = ValidateServiceUrl(service?["serviceUrl"]?.ToString() ?? ServiceUrl);
            var python = service?["startCommand"]?.ToString()?.Trim() ?? PythonPath;
            var directory = service?["workingDirectory"]?.ToString()?.Trim() ?? ServiceDirectory;
            var invokePath = ValidateInvokePath(service?["invokePath"]?.ToString() ?? InvokePath);
            var provider = payload["providerId"]?.ToString() ?? "deepseek";
            if (!new[] { "deepseek", "qwen", "openai", "custom" }.Contains(provider)) throw new InvalidOperationException("Unknown model provider.");
            var baseUrl = (payload["baseUrl"]?.ToString() ?? "").Trim().TrimEnd('/');
            if (baseUrl.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) baseUrl = baseUrl.Substring(0, baseUrl.Length - "/chat/completions".Length);
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var modelUri) || !(modelUri.Scheme == "https" || modelUri.Scheme == "http" && modelUri.IsLoopback))
                throw new InvalidOperationException("Use an HTTPS model URL, or HTTP for a local model.");
            var modelName = (payload["modelName"]?.ToString() ?? "").Trim();
            if (string.IsNullOrEmpty(modelName)) throw new InvalidOperationException("Enter a model name.");
            var model = Read(ModelPath);
            var profiles = model["providers"] as JObject ?? new JObject();
            var previous = model["provider"]?.ToString() ?? "deepseek";
            if (profiles[previous] == null && model["apiKey"] != null)
                profiles[previous] = new JObject { ["apiKey"] = model["apiKey"], ["baseUrl"] = model["baseUrl"], ["model"] = model["model"] };
            var profile = profiles[provider] as JObject ?? new JObject();
            var key = profile["apiKey"]?.ToString() ?? "";
            if (payload["clearApiKey"]?.Value<bool>() == true) key = "";
            else if (!string.IsNullOrWhiteSpace(payload["apiKey"]?.ToString())) key = payload["apiKey"].ToString().Trim();
            profiles[provider] = new JObject { ["apiKey"] = key, ["baseUrl"] = baseUrl, ["model"] = modelName };
            WritePrivate(ModelPath, new JObject { ["provider"] = provider, ["apiKey"] = key, ["baseUrl"] = baseUrl, ["model"] = modelName, ["providers"] = profiles });
            WritePrivate(SettingsPath, new JObject { ["serviceUrl"] = url, ["pythonPath"] = python,
                ["serviceDirectory"] = directory, ["invokePath"] = invokePath });
            ServiceUrl = url; PythonPath = python; ServiceDirectory = directory; InvokePath = invokePath;
        }
    }
}
