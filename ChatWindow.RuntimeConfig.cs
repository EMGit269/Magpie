using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;
using Newtonsoft.Json.Linq;

namespace Magpie
{
    public static partial class ChatWindow
    {
        private class ApiResponse { public string Content; public string Reasoning; }

        private enum AttachmentKind
        {
            Image,
            Text,
            Document,
            Unsupported
        }

        private class AttachmentItem
        {
            public string Path { get; set; }
            public string FileName { get; set; }
            public string MimeType { get; set; }
            public AttachmentKind Kind { get; set; }
            public string Base64 { get; set; }
            public string ExtractedText { get; set; }
            public long SizeBytes { get; set; }
            public string Error { get; set; }
        }

        private static AttachmentItem CloneAttachmentItem(AttachmentItem source)
        {
            if (source == null) return null;
            return new AttachmentItem
            {
                Path = source.Path,
                FileName = source.FileName,
                MimeType = source.MimeType,
                Kind = source.Kind,
                Base64 = source.Base64,
                ExtractedText = source.ExtractedText,
                SizeBytes = source.SizeBytes,
                Error = source.Error
            };
        }

        private static List<AttachmentItem> CloneAttachments(IEnumerable<AttachmentItem> source)
        {
            return (source ?? Enumerable.Empty<AttachmentItem>())
                .Select(CloneAttachmentItem)
                .Where(a => a != null)
                .ToList();
        }

        private class ChatHistoryConversation
        {
            public string Id { get; set; }
            public string Title { get; set; }
            public DateTime CreatedAtUtc { get; set; }
            public DateTime UpdatedAtUtc { get; set; }
            public JArray Messages { get; set; }
        }

        private class ModelProviderConfig
        {
            public string ProviderId { get; set; }
            public string DisplayName { get; set; }
            public string DefaultBaseUrl { get; set; }
            public string DefaultModel { get; set; }
            public bool SupportsTools { get; set; } = true;
            public bool SupportsVision { get; set; } = true;
            public string DefaultReasoningEffort { get; set; }
            public bool EnableThinking { get; set; }
            public string ImageContentFormat { get; set; } = "image_url";
        }

        private class ProviderRuntimeSettings
        {
            public ModelProviderConfig Config { get; set; }
            public string ApiKey { get; set; }
            public string BaseUrl { get; set; }
            public string ModelName { get; set; }
            public string ProxyUrl { get; set; }
        }

        private class EndpointCandidate
        {
            public string Url { get; set; }
            public bool IsFallback { get; set; }
        }

        private static List<ModelProviderConfig> GetProviderConfigs()
        {
            return new List<ModelProviderConfig>
            {
                new ModelProviderConfig
                {
                    ProviderId = "deepseek",
                    DisplayName = "DeepSeek",
                    DefaultBaseUrl = "https://api.deepseek.com/chat/completions",
                    DefaultModel = "deepseek-v4-flash",
                    SupportsVision = false,
                    EnableThinking = true,
                    DefaultReasoningEffort = "high"
                },
                new ModelProviderConfig
                {
                    ProviderId = "seed",
                    DisplayName = "Seed / 火山方舟",
                    DefaultBaseUrl = "https://ark.cn-beijing.volces.com/api/v3/chat/completions",
                    DefaultModel = "doubao-seed-2-0-lite-260215",
                    DefaultReasoningEffort = "medium"
                },
                new ModelProviderConfig
                {
                    ProviderId = "qwen",
                    DisplayName = "Qwen / 通义千问",
                    DefaultBaseUrl = "https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions",
                    DefaultModel = "qwen3.6-plus"
                },
                new ModelProviderConfig
                {
                    ProviderId = "kimi",
                    DisplayName = "Kimi / Moonshot",
                    DefaultBaseUrl = "https://api.moonshot.cn/v1/chat/completions",
                    DefaultModel = "kimi-k2.6"
                },
                new ModelProviderConfig
                {
                    ProviderId = "gemini-flash",
                    DisplayName = "Gemini 3 Flash",
                    DefaultBaseUrl = "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions",
                    DefaultModel = "gemini-3-flash-preview"
                },
                new ModelProviderConfig
                {
                    ProviderId = "gemini-pro",
                    DisplayName = "Gemini 3.1 Pro",
                    DefaultBaseUrl = "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions",
                    DefaultModel = "gemini-3.1-pro-preview"
                },
                new ModelProviderConfig
                {
                    ProviderId = "openai",
                    DisplayName = "OpenAI / GPT 5.5",
                    DefaultBaseUrl = "https://api.openai.com/v1/chat/completions",
                    DefaultModel = "gpt-5.5-medium"
                },
                new ModelProviderConfig
                {
                    ProviderId = "vibelearning",
                    DisplayName = "VibeLearning / GPT 5.4",
                    DefaultBaseUrl = "https://api.vibelearning.top/v1",
                    DefaultModel = "gpt-5.4-medium"
                },
                new ModelProviderConfig
                {
                    ProviderId = "custom",
                    DisplayName = "Custom",
                    DefaultBaseUrl = "https://api.deepseek.com/chat/completions",
                    DefaultModel = "deepseek-v4-flash"
                },
                new ModelProviderConfig
                {
                    ProviderId = "nanobanana-relay",
                    DisplayName = "VibeLearning Image Relay",
                    DefaultBaseUrl = "https://api.vibelearning.top",
                    DefaultModel = "gemini-3.1-flash-image-preview",
                    SupportsTools = false,
                    SupportsVision = true
                },
                new ModelProviderConfig
                {
                    ProviderId = "seedance-relay",
                    DisplayName = "Seedance Video / Relay",
                    DefaultBaseUrl = "https://your-relay-host",
                    DefaultModel = "doubao-seedance-1-0-pro-250528",
                    SupportsTools = false,
                    SupportsVision = true
                }
            };
        }

        private static ModelProviderConfig GetProviderConfig(string providerId)
        {
            var config = GetProviderConfigs().FirstOrDefault(p => p.ProviderId.Equals(providerId, StringComparison.OrdinalIgnoreCase));
            return config ?? GetProviderConfigs().First();
        }

        private static string GetCurrentProviderId()
        {
            return Grasshopper.Instances.Settings.GetValue("AI_CurrentProvider", "deepseek");
        }

        private static string GetCurrentVisionProviderId()
        {
            return Grasshopper.Instances.Settings.GetValue("AI_VisionProvider", "qwen");
        }

        private static string GetCurrentImageProviderId()
        {
            return Grasshopper.Instances.Settings.GetValue("AI_ImageProvider", "nanobanana-relay");
        }

        private static string GetProviderSettingKey(string providerId, string name)
        {
            return $"AI_{providerId}_{name}";
        }

        private static string GetImageProviderSettingKey(string providerId, string name)
        {
            return $"AI_Image_{providerId}_{name}";
        }

        private static string GetVisionProviderSettingKey(string providerId, string name)
        {
            return $"AI_Vision_{providerId}_{name}";
        }

        private static string ReadFirstNonEmptyEnvironmentVariable(params string[] names)
        {
            if (names == null) return "";
            foreach (string name in names)
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                string value = Environment.GetEnvironmentVariable(name);
                if (!string.IsNullOrWhiteSpace(value))
                    return value.Trim();
            }
            return "";
        }

        private static string NormalizeProxyUrl(string raw)
        {
            string value = (raw ?? "").Trim();
            if (string.IsNullOrWhiteSpace(value))
                return "";

            // Guard against users pasting an API endpoint into the proxy field.
            string lowered = value.Replace('\\', '/').ToLowerInvariant();
            if (lowered.Contains("/chat/completions") ||
                lowered.Contains("/responses") ||
                lowered.Contains("/embeddings") ||
                lowered.Contains("/images") ||
                lowered.Contains("/audio") ||
                lowered.Contains("/v1beta/openai/") ||
                lowered.Contains("/compatible-mode/") ||
                lowered.Contains("/api/v3/"))
            {
                AddGhLog.Warn("Ignoring proxy-like field because it looks like an API endpoint: " + value);
                return "";
            }

            if (!value.Contains("://"))
                value = "http://" + value;

            return value;
        }

        private static string ReadResolvedProxyUrl(string providerId)
        {
            string perProvider = Grasshopper.Instances.Settings.GetValue(GetProviderSettingKey(providerId, "ProxyUrl"), "");
            if (!string.IsNullOrWhiteSpace(perProvider))
                return NormalizeProxyUrl(perProvider);

            string shared = Grasshopper.Instances.Settings.GetValue("AI_ProxyUrl", "");
            if (!string.IsNullOrWhiteSpace(shared))
                return NormalizeProxyUrl(shared);

            return NormalizeProxyUrl(ReadFirstNonEmptyEnvironmentVariable(
                "MAGPIE_HTTPS_PROXY",
                "MAGPIE_HTTP_PROXY",
                "HTTPS_PROXY",
                "https_proxy",
                "HTTP_PROXY",
                "http_proxy"));
        }

        private static string ReadResolvedImageProxyUrl(string providerId)
        {
            string perProvider = Grasshopper.Instances.Settings.GetValue(GetImageProviderSettingKey(providerId, "ProxyUrl"), "");
            if (!string.IsNullOrWhiteSpace(perProvider))
                return NormalizeProxyUrl(perProvider);

            return NormalizeProxyUrl(ReadFirstNonEmptyEnvironmentVariable(
                "MAGPIE_HTTPS_PROXY",
                "MAGPIE_HTTP_PROXY",
                "HTTPS_PROXY",
                "https_proxy",
                "HTTP_PROXY",
                "http_proxy"));
        }

        private static string ReadResolvedVisionProxyUrl(string providerId)
        {
            string perProvider = Grasshopper.Instances.Settings.GetValue(GetVisionProviderSettingKey(providerId, "ProxyUrl"), "");
            if (!string.IsNullOrWhiteSpace(perProvider))
                return NormalizeProxyUrl(perProvider);

            return ReadResolvedProxyUrl(providerId);
        }

        private static string ReadResolvedApiKey(string providerId)
        {
            string dpapiEnc = Grasshopper.Instances.Settings.GetValue(GetProviderSettingKey(providerId, "API_Key_DPAPI"), "");
            if (!string.IsNullOrWhiteSpace(dpapiEnc) && ApiCredentialStore.TryUnprotectFromBase64(dpapiEnc, out string dec) && dec != null)
                return dec;

            string per = Grasshopper.Instances.Settings.GetValue(GetProviderSettingKey(providerId, "API_Key"), "");
            string legacy = providerId.Equals("deepseek", StringComparison.OrdinalIgnoreCase)
                ? Grasshopper.Instances.Settings.GetValue("AI_API_Key", "")
                : "";
            return string.IsNullOrWhiteSpace(per) ? legacy : per;
        }

        private static string ReadResolvedImageApiKey(string providerId)
        {
            string dpapiEnc = Grasshopper.Instances.Settings.GetValue(GetImageProviderSettingKey(providerId, "API_Key_DPAPI"), "");
            if (!string.IsNullOrWhiteSpace(dpapiEnc) && ApiCredentialStore.TryUnprotectFromBase64(dpapiEnc, out string dec) && dec != null)
                return dec;

            return Grasshopper.Instances.Settings.GetValue(GetImageProviderSettingKey(providerId, "API_Key"), "");
        }

        private static string ReadResolvedVisionApiKey(string providerId)
        {
            string dpapiEnc = Grasshopper.Instances.Settings.GetValue(GetVisionProviderSettingKey(providerId, "API_Key_DPAPI"), "");
            if (!string.IsNullOrWhiteSpace(dpapiEnc) && ApiCredentialStore.TryUnprotectFromBase64(dpapiEnc, out string dec) && dec != null)
                return dec;

            string per = Grasshopper.Instances.Settings.GetValue(GetVisionProviderSettingKey(providerId, "API_Key"), "");
            return string.IsNullOrWhiteSpace(per) ? ReadResolvedApiKey(providerId) : per;
        }

        private static void PersistApiKey(string providerId, string apiKeyPlain)
        {
            string key = apiKeyPlain ?? "";
            if (string.IsNullOrEmpty(key))
            {
                Grasshopper.Instances.Settings.SetValue(GetProviderSettingKey(providerId, "API_Key_DPAPI"), "");
                Grasshopper.Instances.Settings.SetValue(GetProviderSettingKey(providerId, "API_Key"), "");
                if (providerId.Equals("deepseek", StringComparison.OrdinalIgnoreCase))
                    Grasshopper.Instances.Settings.SetValue("AI_API_Key", "");
                return;
            }

            if (DeploymentOptions.UseDpapiForApiKeys)
            {
                if (ApiCredentialStore.TryProtectToBase64(key, out string enc))
                {
                    Grasshopper.Instances.Settings.SetValue(GetProviderSettingKey(providerId, "API_Key_DPAPI"), enc);
                    Grasshopper.Instances.Settings.SetValue(GetProviderSettingKey(providerId, "API_Key"), "");
                    if (providerId.Equals("deepseek", StringComparison.OrdinalIgnoreCase))
                        Grasshopper.Instances.Settings.SetValue("AI_API_Key", "");
                    return;
                }
                AddGhLog.Warn("Magpie: DPAPI protect failed; storing API key as plaintext for provider " + providerId);
                AppendQuietDiagnosticCard("密钥存储",
                    "系统加密不可用，密钥已暂时以明文写入 Grasshopper 设置。详细信息见本地日志。");
            }

            Grasshopper.Instances.Settings.SetValue(GetProviderSettingKey(providerId, "API_Key_DPAPI"), "");
            Grasshopper.Instances.Settings.SetValue(GetProviderSettingKey(providerId, "API_Key"), key);
            if (providerId.Equals("deepseek", StringComparison.OrdinalIgnoreCase))
                Grasshopper.Instances.Settings.SetValue("AI_API_Key", key);
        }

        private static void PersistImageApiKey(string providerId, string apiKeyPlain)
        {
            string key = apiKeyPlain ?? "";
            if (string.IsNullOrEmpty(key))
            {
                Grasshopper.Instances.Settings.SetValue(GetImageProviderSettingKey(providerId, "API_Key_DPAPI"), "");
                Grasshopper.Instances.Settings.SetValue(GetImageProviderSettingKey(providerId, "API_Key"), "");
                return;
            }

            if (DeploymentOptions.UseDpapiForApiKeys)
            {
                if (ApiCredentialStore.TryProtectToBase64(key, out string enc))
                {
                    Grasshopper.Instances.Settings.SetValue(GetImageProviderSettingKey(providerId, "API_Key_DPAPI"), enc);
                    Grasshopper.Instances.Settings.SetValue(GetImageProviderSettingKey(providerId, "API_Key"), "");
                    return;
                }

                AddGhLog.Warn("Magpie: DPAPI protect failed; storing image API key as plaintext for provider " + providerId);
            }

            Grasshopper.Instances.Settings.SetValue(GetImageProviderSettingKey(providerId, "API_Key_DPAPI"), "");
            Grasshopper.Instances.Settings.SetValue(GetImageProviderSettingKey(providerId, "API_Key"), key);
        }

        private static void PersistVisionApiKey(string providerId, string apiKeyPlain)
        {
            string key = apiKeyPlain ?? "";
            if (string.IsNullOrEmpty(key))
            {
                Grasshopper.Instances.Settings.SetValue(GetVisionProviderSettingKey(providerId, "API_Key_DPAPI"), "");
                Grasshopper.Instances.Settings.SetValue(GetVisionProviderSettingKey(providerId, "API_Key"), "");
                return;
            }

            if (DeploymentOptions.UseDpapiForApiKeys)
            {
                if (ApiCredentialStore.TryProtectToBase64(key, out string enc))
                {
                    Grasshopper.Instances.Settings.SetValue(GetVisionProviderSettingKey(providerId, "API_Key_DPAPI"), enc);
                    Grasshopper.Instances.Settings.SetValue(GetVisionProviderSettingKey(providerId, "API_Key"), "");
                    return;
                }

                AddGhLog.Warn("Magpie: DPAPI protect failed; storing vision API key as plaintext for provider " + providerId);
            }

            Grasshopper.Instances.Settings.SetValue(GetVisionProviderSettingKey(providerId, "API_Key_DPAPI"), "");
            Grasshopper.Instances.Settings.SetValue(GetVisionProviderSettingKey(providerId, "API_Key"), key);
        }

        private static ProviderRuntimeSettings GetProviderRuntimeSettings()
        {
            return GetProviderRuntimeSettings(GetCurrentProviderId());
        }

        private static ProviderRuntimeSettings GetImageProviderRuntimeSettings()
        {
            return GetImageProviderRuntimeSettings(GetCurrentImageProviderId());
        }

        private static ProviderRuntimeSettings GetVisionProviderRuntimeSettings()
        {
            return GetVisionProviderRuntimeSettings(GetCurrentVisionProviderId());
        }

        private static ProviderRuntimeSettings GetProviderRuntimeSettings(string providerId)
        {
            if (string.IsNullOrWhiteSpace(providerId))
                providerId = GetCurrentProviderId();

            var config = GetProviderConfig(providerId);
            string legacyBaseUrl = providerId == "deepseek" ? Grasshopper.Instances.Settings.GetValue("AI_API_BaseUrl", config.DefaultBaseUrl) : config.DefaultBaseUrl;
            string legacyModel = providerId == "deepseek" ? Grasshopper.Instances.Settings.GetValue("AI_ModelName", config.DefaultModel) : config.DefaultModel;

            return new ProviderRuntimeSettings
            {
                Config = config,
                ApiKey = ReadResolvedApiKey(providerId),
                BaseUrl = Grasshopper.Instances.Settings.GetValue(GetProviderSettingKey(providerId, "BaseUrl"), legacyBaseUrl),
                ModelName = Grasshopper.Instances.Settings.GetValue(GetProviderSettingKey(providerId, "ModelName"), legacyModel),
                ProxyUrl = ReadResolvedProxyUrl(providerId)
            };
        }

        private static ProviderRuntimeSettings GetImageProviderRuntimeSettings(string providerId)
        {
            if (string.IsNullOrWhiteSpace(providerId))
                providerId = GetCurrentImageProviderId();

            var config = GetProviderConfig(providerId);
            string baseUrl = Grasshopper.Instances.Settings.GetValue(GetImageProviderSettingKey(providerId, "BaseUrl"), config.DefaultBaseUrl);
            string modelName = Grasshopper.Instances.Settings.GetValue(GetImageProviderSettingKey(providerId, "ModelName"), config.DefaultModel);
            if (providerId.Equals("nanobanana-relay", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(baseUrl)
                    || baseUrl.IndexOf("your-relay-host", StringComparison.OrdinalIgnoreCase) >= 0
                    || baseUrl.IndexOf("ai.comfly.chat", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    baseUrl = config.DefaultBaseUrl;
                }
                if (string.IsNullOrWhiteSpace(modelName))
                    modelName = config.DefaultModel;
            }

            return new ProviderRuntimeSettings
            {
                Config = config,
                ApiKey = ReadResolvedImageApiKey(providerId),
                BaseUrl = baseUrl,
                ModelName = modelName,
                ProxyUrl = ReadResolvedImageProxyUrl(providerId)
            };
        }

        private static ProviderRuntimeSettings GetVisionProviderRuntimeSettings(string providerId)
        {
            if (string.IsNullOrWhiteSpace(providerId))
                providerId = GetCurrentVisionProviderId();

            var config = GetProviderConfig(providerId);
            string baseFallback = Grasshopper.Instances.Settings.GetValue(GetProviderSettingKey(providerId, "BaseUrl"), config.DefaultBaseUrl);
            string modelFallback = Grasshopper.Instances.Settings.GetValue(GetProviderSettingKey(providerId, "ModelName"), config.DefaultModel);
            return new ProviderRuntimeSettings
            {
                Config = config,
                ApiKey = ReadResolvedVisionApiKey(providerId),
                BaseUrl = Grasshopper.Instances.Settings.GetValue(GetVisionProviderSettingKey(providerId, "BaseUrl"), baseFallback),
                ModelName = Grasshopper.Instances.Settings.GetValue(GetVisionProviderSettingKey(providerId, "ModelName"), modelFallback),
                ProxyUrl = ReadResolvedVisionProxyUrl(providerId)
            };
        }

        private static void PopulateProviderCombo()
        {
            var providers = GetProviderConfigs();

            if (_comboProvider != null)
            {
                _comboProvider.Items.Clear();
                foreach (var provider in providers)
                    _comboProvider.Items.Add(new ComboBoxItem { Content = provider.DisplayName, Tag = provider.ProviderId });
            }

            if (_comboVisionProvider != null)
            {
                _comboVisionProvider.Items.Clear();
                foreach (var provider in providers)
                    _comboVisionProvider.Items.Add(new ComboBoxItem { Content = provider.DisplayName, Tag = provider.ProviderId });
            }

            if (_comboImageProvider != null)
            {
                _comboImageProvider.Items.Clear();
                foreach (var provider in providers)
                    _comboImageProvider.Items.Add(new ComboBoxItem { Content = provider.DisplayName, Tag = provider.ProviderId });
            }
        }

        private static string GetSelectedProviderId()
        {
            if (_comboProvider?.SelectedItem is ComboBoxItem item && item.Tag != null) return item.Tag.ToString();
            return GetCurrentProviderId();
        }

        private static void SelectProviderComboItem(string providerId)
        {
            if (_comboProvider == null) return;

            foreach (var item in _comboProvider.Items.OfType<ComboBoxItem>())
            {
                if ((item.Tag?.ToString() ?? "").Equals(providerId, StringComparison.OrdinalIgnoreCase))
                {
                    _comboProvider.SelectedItem = item;
                    return;
                }
            }

            if (_comboProvider.Items.Count > 0) _comboProvider.SelectedIndex = 0;
        }

        private static string GetSelectedVisionProviderId()
        {
            if (_comboVisionProvider?.SelectedItem is ComboBoxItem item && item.Tag != null) return item.Tag.ToString();
            return GetCurrentVisionProviderId();
        }

        private static void SelectVisionProviderComboItem(string providerId)
        {
            if (_comboVisionProvider == null) return;

            foreach (var item in _comboVisionProvider.Items.OfType<ComboBoxItem>())
            {
                if ((item.Tag?.ToString() ?? "").Equals(providerId, StringComparison.OrdinalIgnoreCase))
                {
                    _comboVisionProvider.SelectedItem = item;
                    return;
                }
            }

            foreach (var item in _comboVisionProvider.Items.OfType<ComboBoxItem>())
            {
                if ((item.Tag?.ToString() ?? "").Equals("qwen", StringComparison.OrdinalIgnoreCase))
                {
                    _comboVisionProvider.SelectedItem = item;
                    return;
                }
            }

            if (_comboVisionProvider.Items.Count > 0) _comboVisionProvider.SelectedIndex = 0;
        }

        private static string GetSelectedImageProviderId()
        {
            if (_comboImageProvider?.SelectedItem is ComboBoxItem item && item.Tag != null) return item.Tag.ToString();
            return GetCurrentImageProviderId();
        }

        private static void SelectImageProviderComboItem(string providerId)
        {
            if (_comboImageProvider == null) return;

            foreach (var item in _comboImageProvider.Items.OfType<ComboBoxItem>())
            {
                if ((item.Tag?.ToString() ?? "").Equals(providerId, StringComparison.OrdinalIgnoreCase))
                {
                    _comboImageProvider.SelectedItem = item;
                    return;
                }
            }

            foreach (var item in _comboImageProvider.Items.OfType<ComboBoxItem>())
            {
                if ((item.Tag?.ToString() ?? "").Equals("nanobanana-relay", StringComparison.OrdinalIgnoreCase))
                {
                    _comboImageProvider.SelectedItem = item;
                    return;
                }
            }

            if (_comboImageProvider.Items.Count > 0) _comboImageProvider.SelectedIndex = 0;
        }

        private static void LoadProviderSettingsToUI(string providerId)
        {
            if (_txtApiKey == null || _txtApiBaseUrl == null || _txtModel == null || _txtProxyUrl == null) return;

            _isLoadingProviderSettings = true;
            try
            {
                var config = GetProviderConfig(providerId);
                string legacyBaseUrl = providerId == "deepseek" ? Grasshopper.Instances.Settings.GetValue("AI_API_BaseUrl", config.DefaultBaseUrl) : config.DefaultBaseUrl;
                string legacyModel = providerId == "deepseek" ? Grasshopper.Instances.Settings.GetValue("AI_ModelName", config.DefaultModel) : config.DefaultModel;
                string sharedProxy = Grasshopper.Instances.Settings.GetValue("AI_ProxyUrl", "");

                _txtApiKey.Text = ReadResolvedApiKey(providerId);
                _txtApiBaseUrl.Text = Grasshopper.Instances.Settings.GetValue(GetProviderSettingKey(providerId, "BaseUrl"), legacyBaseUrl);
                _txtModel.Text = Grasshopper.Instances.Settings.GetValue(GetProviderSettingKey(providerId, "ModelName"), legacyModel);
                _txtProxyUrl.Text = Grasshopper.Instances.Settings.GetValue(GetProviderSettingKey(providerId, "ProxyUrl"),
                    !string.IsNullOrWhiteSpace(sharedProxy) ? NormalizeProxyUrl(sharedProxy) : ReadResolvedProxyUrl(providerId));
            }
            finally
            {
                _isLoadingProviderSettings = false;
            }
        }

        private static void LoadImageProviderSettingsToUI(string providerId)
        {
            if (_txtImageApiKey == null || _txtImageApiBaseUrl == null || _txtImageModel == null || _txtImageProxyUrl == null) return;

            _isLoadingProviderSettings = true;
            try
            {
                var settings = GetImageProviderRuntimeSettings(providerId);
                _txtImageApiKey.Text = ReadResolvedImageApiKey(providerId);
                _txtImageApiBaseUrl.Text = settings.BaseUrl ?? "";
                _txtImageModel.Text = settings.ModelName ?? "";
                _txtImageProxyUrl.Text = Grasshopper.Instances.Settings.GetValue(GetImageProviderSettingKey(providerId, "ProxyUrl"), ReadResolvedImageProxyUrl(providerId));
            }
            finally
            {
                _isLoadingProviderSettings = false;
            }
        }

        private static void LoadVisionProviderSettingsToUI(string providerId)
        {
            if (_txtVisionApiKey == null || _txtVisionApiBaseUrl == null || _txtVisionModel == null || _txtVisionProxyUrl == null) return;

            _isLoadingProviderSettings = true;
            try
            {
                var settings = GetVisionProviderRuntimeSettings(providerId);
                _txtVisionApiKey.Text = settings.ApiKey ?? "";
                _txtVisionApiBaseUrl.Text = settings.BaseUrl ?? "";
                _txtVisionModel.Text = settings.ModelName ?? "";
                _txtVisionProxyUrl.Text = settings.ProxyUrl ?? "";
            }
            finally
            {
                _isLoadingProviderSettings = false;
            }
        }

        private static void SaveSelectedProviderSettings()
        {
            string providerId = GetSelectedProviderId();
            Grasshopper.Instances.Settings.SetValue("AI_CurrentProvider", providerId);
            PersistApiKey(providerId, _txtApiKey?.Text);
            if (_txtApiBaseUrl != null) Grasshopper.Instances.Settings.SetValue(GetProviderSettingKey(providerId, "BaseUrl"), _txtApiBaseUrl.Text);
            if (_txtModel != null) Grasshopper.Instances.Settings.SetValue(GetProviderSettingKey(providerId, "ModelName"), _txtModel.Text);
            if (_txtProxyUrl != null) Grasshopper.Instances.Settings.SetValue(GetProviderSettingKey(providerId, "ProxyUrl"), NormalizeProxyUrl(_txtProxyUrl.Text));

            // Keep legacy URL/model keys populated so older builds can still read defaults for DeepSeek.
            if (_txtApiBaseUrl != null) Grasshopper.Instances.Settings.SetValue("AI_API_BaseUrl", _txtApiBaseUrl.Text);
            if (_txtModel != null) Grasshopper.Instances.Settings.SetValue("AI_ModelName", _txtModel.Text);
            if (_txtProxyUrl != null) Grasshopper.Instances.Settings.SetValue("AI_ProxyUrl", NormalizeProxyUrl(_txtProxyUrl.Text));
        }

        private static void SaveSelectedImageProviderSettings()
        {
            string providerId = GetSelectedImageProviderId();
            Grasshopper.Instances.Settings.SetValue("AI_ImageProvider", providerId);
            PersistImageApiKey(providerId, _txtImageApiKey?.Text);
            if (_txtImageApiBaseUrl != null) Grasshopper.Instances.Settings.SetValue(GetImageProviderSettingKey(providerId, "BaseUrl"), _txtImageApiBaseUrl.Text);
            if (_txtImageModel != null) Grasshopper.Instances.Settings.SetValue(GetImageProviderSettingKey(providerId, "ModelName"), _txtImageModel.Text);
            if (_txtImageProxyUrl != null) Grasshopper.Instances.Settings.SetValue(GetImageProviderSettingKey(providerId, "ProxyUrl"), NormalizeProxyUrl(_txtImageProxyUrl.Text));
        }

        private static void SaveSelectedVisionProviderSetting()
        {
            string providerId = GetSelectedVisionProviderId();
            Grasshopper.Instances.Settings.SetValue("AI_VisionProvider", providerId);
            PersistVisionApiKey(providerId, _txtVisionApiKey?.Text);
            if (_txtVisionApiBaseUrl != null) Grasshopper.Instances.Settings.SetValue(GetVisionProviderSettingKey(providerId, "BaseUrl"), _txtVisionApiBaseUrl.Text);
            if (_txtVisionModel != null) Grasshopper.Instances.Settings.SetValue(GetVisionProviderSettingKey(providerId, "ModelName"), _txtVisionModel.Text);
            if (_txtVisionProxyUrl != null) Grasshopper.Instances.Settings.SetValue(GetVisionProviderSettingKey(providerId, "ProxyUrl"), NormalizeProxyUrl(_txtVisionProxyUrl.Text));
        }
    }
}
