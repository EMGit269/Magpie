using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Magpie
{
    public static partial class ChatWindow
    {
        private static readonly object _webResearchTextCacheLock = new object();
        private static readonly Dictionary<string, string> _webResearchTextCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static async Task<string> ExecuteWebResearchAsync(
            string mode,
            string query,
            string url,
            JArray allowedDomains,
            int maxResults,
            int maxChars,
            System.Threading.CancellationToken ct)
        {
            string normalizedMode = (mode ?? "").Trim().ToLowerInvariant();
            if (normalizedMode != "fetch" && normalizedMode != "api_pipeline")
                normalizedMode = "search";

            maxResults = Math.Max(1, Math.Min(maxResults <= 0 ? 5 : maxResults, 10));
            maxChars = Math.Max(800, Math.Min(maxChars <= 0 ? 6000 : maxChars, 16000));
            var domains = ReadAllowedDomains(allowedDomains);
            int timeoutSeconds = GetWebResearchTimeoutSeconds(normalizedMode);
            var state = new WebResearchRunState(normalizedMode, timeoutSeconds);

            using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
                CancellationToken runToken = timeoutCts.Token;

                try
                {
                    string result;
                    if (normalizedMode == "fetch")
                        result = await FetchWebPageAsync(url, domains, maxChars, runToken, state).ConfigureAwait(false);
                    else if (normalizedMode == "api_pipeline")
                        result = await ExecuteApiDocPipelineAsync(query, domains, maxResults, maxChars, runToken, state).ConfigureAwait(false);
                    else
                        result = await SearchWebAsync(query, domains, maxResults, maxChars, runToken, state).ConfigureAwait(false);

                    return AttachWebResearchDiagnostics(result, state, maxChars);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return BuildWebResearchTimeoutPayload(normalizedMode, query, url, state, maxChars);
                }
            }
        }

        private static int GetWebResearchTimeoutSeconds(string mode)
        {
            if (string.Equals(mode, "api_pipeline", StringComparison.OrdinalIgnoreCase))
                return DeploymentOptions.WebResearchApiPipelineTimeoutSeconds;
            if (string.Equals(mode, "fetch", StringComparison.OrdinalIgnoreCase))
                return DeploymentOptions.WebResearchFetchTimeoutSeconds;
            return DeploymentOptions.WebResearchSearchTimeoutSeconds;
        }

        private sealed class WebResearchRunState
        {
            private readonly Stopwatch _clock = Stopwatch.StartNew();
            private readonly DateTime _deadlineUtc;

            public WebResearchRunState(string mode, int timeoutSeconds)
            {
                Mode = mode ?? "search";
                TimeoutSeconds = Math.Max(1, timeoutSeconds);
                _deadlineUtc = DateTime.UtcNow.AddSeconds(TimeoutSeconds);
            }

            public string Mode { get; private set; }
            public int TimeoutSeconds { get; private set; }
            public int RequestCount { get; set; }
            public int CacheHits { get; set; }
            public bool PartialTimeout { get; set; }
            public long ElapsedMs { get { return _clock.ElapsedMilliseconds; } }
            public bool IsTimedOut { get { return DateTime.UtcNow >= _deadlineUtc; } }

            public TimeSpan Remaining
            {
                get
                {
                    TimeSpan remaining = _deadlineUtc - DateTime.UtcNow;
                    return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
                }
            }
        }

        private static List<string> ReadAllowedDomains(JArray allowedDomains)
        {
            var result = new List<string>();
            if (allowedDomains == null)
                return result;

            foreach (var item in allowedDomains)
            {
                string domain = (item?.ToString() ?? "").Trim().TrimStart('.').ToLowerInvariant();
                if (string.IsNullOrWhiteSpace(domain))
                    continue;
                if (domain.Contains("/") || domain.Contains("\\") || domain.Contains(":"))
                    continue;
                if (!result.Contains(domain))
                    result.Add(domain);
            }
            return result;
        }

        private static bool ShouldStopWebResearch(WebResearchRunState state, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (state != null && state.IsTimedOut)
            {
                state.PartialTimeout = true;
                return true;
            }
            return false;
        }

        private static string AttachWebResearchDiagnostics(string raw, WebResearchRunState state, int maxChars)
        {
            if (state == null || string.IsNullOrWhiteSpace(raw))
                return raw;

            try
            {
                var payload = JObject.Parse(raw);
                if (payload["status"] == null)
                    payload["status"] = state.PartialTimeout ? "partial_timeout" : "ok";
                payload["elapsed_ms"] = state.ElapsedMs;
                payload["timeout_ms"] = state.TimeoutSeconds * 1000;
                payload["request_count"] = state.RequestCount;
                payload["cache_hits"] = state.CacheHits;

                string json = payload.ToString(Formatting.None);
                return json.Length <= maxChars ? json : json.Substring(0, maxChars) + "...";
            }
            catch
            {
                return raw;
            }
        }

        private static string BuildWebResearchTimeoutPayload(string mode, string query, string url, WebResearchRunState state, int maxChars)
        {
            if (state != null)
                state.PartialTimeout = true;

            var payload = new JObject
            {
                ["mode"] = mode ?? "search",
                ["status"] = "timeout",
                ["query"] = query ?? "",
                ["url"] = url ?? "",
                ["elapsed_ms"] = state != null ? state.ElapsedMs : 0,
                ["timeout_ms"] = state != null ? state.TimeoutSeconds * 1000 : 0,
                ["request_count"] = state != null ? state.RequestCount : 0,
                ["cache_hits"] = state != null ? state.CacheHits : 0,
                ["results"] = new JArray()
            };

            string json = payload.ToString(Formatting.None);
            return json.Length <= maxChars ? json : json.Substring(0, maxChars) + "...";
        }

        private static async Task<string> SearchWebAsync(string query, List<string> allowedDomains, int maxResults, int maxChars, CancellationToken ct, WebResearchRunState state)
        {
            if (string.IsNullOrWhiteSpace(query))
                return "Error: query is required for local documentation search.";

            try
            {
                var mcneelResults = await SearchMcNeelApiDocsAsync(query, allowedDomains, maxResults, ct, state).ConfigureAwait(false);
                if (mcneelResults.Count > 0)
                {
                    var mcneelPayload = new JObject
                    {
                        ["mode"] = "search",
                        ["query"] = query.Trim(),
                        ["provider"] = "mcneel_api_index",
                        ["search_url"] = "https://mcneel.github.io/",
                        ["result_count"] = mcneelResults.Count,
                        ["results"] = new JArray(mcneelResults)
                    };
                    string mcneelJson = mcneelPayload.ToString(Formatting.None);
                    return mcneelJson.Length <= maxChars ? mcneelJson : mcneelJson.Substring(0, maxChars) + "...";
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AddGhLog.Warn("McNeel API lookup failed: " + ex.Message);
            }

            string effectiveQuery = query.Trim();
            var results = SearchLocalDocumentation(effectiveQuery, allowedDomains, maxResults, ct, state);

            var payload = new JObject
            {
                ["mode"] = "search",
                ["query"] = query.Trim(),
                ["provider"] = "local_documentation",
                ["search_url"] = "",
                ["result_count"] = results.Count,
                ["results"] = new JArray(results)
            };

            string json = payload.ToString(Formatting.None);
            return json.Length <= maxChars ? json : json.Substring(0, maxChars) + "...";
        }

        private static async Task<string> ExecuteApiDocPipelineAsync(
            string query,
            List<string> allowedDomains,
            int maxResults,
            int maxChars,
            CancellationToken ct,
            WebResearchRunState state)
        {
            if (string.IsNullOrWhiteSpace(query))
                return "Error: query is required for api_pipeline.";

            var normalizedDomains = NormalizeApiDocDomains(allowedDomains);
            var expandedQueries = BuildApiDocPipelineQueries(query)
                .Take(Math.Max(1, DeploymentOptions.ApiDocPipelineExpandedQueryLimit))
                .ToList();
            var stageResults = new JArray();

            stageResults.Add(new JObject
            {
                ["stage"] = "parse_api_intent",
                ["input_query"] = query.Trim(),
                ["candidate_symbols"] = new JArray(ExtractApiSymbolCandidates(query)),
                ["expanded_queries"] = new JArray(expandedQueries),
                ["official_domains"] = new JArray(normalizedDomains)
            });

            var allCandidates = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
            var mcneelDomains = new List<string> { "mcneel.github.io" };
            foreach (string q in expandedQueries)
            {
                if (ShouldStopWebResearch(state, ct))
                    break;

                var candidates = await SearchMcNeelApiDocsAsync(q, mcneelDomains, maxResults, ct, state).ConfigureAwait(false);
                foreach (var candidate in candidates)
                    UpsertPipelineCandidate(allCandidates, candidate, "mcneel_api_index", q);
                if (allCandidates.Count >= maxResults)
                    break;
            }

            stageResults.Add(new JObject
            {
                ["stage"] = "search_official_api_index",
                ["provider"] = "mcneel_api_index",
                ["result_count"] = allCandidates.Count,
                ["results"] = new JArray(allCandidates.Values.Take(maxResults))
            });

            if (allCandidates.Count == 0)
            {
                var fallbackResults = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
                foreach (string q in expandedQueries.Take(Math.Max(1, DeploymentOptions.ApiDocPipelineFallbackQueryLimit)))
                {
                    if (ShouldStopWebResearch(state, ct))
                        break;

                    string raw = await SearchWebAsync(q, normalizedDomains, maxResults, maxChars, ct, state).ConfigureAwait(false);
                    TryMergePipelineSearchResults(fallbackResults, raw, q);
                    if (fallbackResults.Count >= maxResults)
                        break;
                }

                stageResults.Add(new JObject
                {
                    ["stage"] = "fallback_local_documentation_search",
                    ["provider"] = "local_documentation",
                    ["result_count"] = fallbackResults.Count,
                    ["results"] = new JArray(fallbackResults.Values.Take(maxResults))
                });

                foreach (var pair in fallbackResults)
                    allCandidates[pair.Key] = pair.Value;
            }

            string diagnosis = allCandidates.Count > 0
                ? (state != null && state.PartialTimeout ? "candidate_docs_found_partial" : "candidate_docs_found")
                : "no_candidate_docs_found";

            var payload = new JObject
            {
                ["mode"] = "api_pipeline",
                ["query"] = query.Trim(),
                ["diagnosis"] = diagnosis,
                ["status"] = state != null && state.PartialTimeout ? "partial_timeout" : "ok",
                ["result_count"] = allCandidates.Count,
                ["stages"] = stageResults,
                ["next_actions"] = BuildApiPipelineNextActions(allCandidates.Count)
            };

            string json = payload.ToString(Formatting.None);
            return json.Length <= maxChars ? json : json.Substring(0, maxChars) + "...";
        }

        private static List<string> NormalizeApiDocDomains(List<string> allowedDomains)
        {
            var domains = new List<string>();
            if (allowedDomains != null)
            {
                foreach (string domain in allowedDomains)
                {
                    if (!string.IsNullOrWhiteSpace(domain) && !domains.Contains(domain, StringComparer.OrdinalIgnoreCase))
                        domains.Add(domain);
                }
            }

            if (!domains.Contains("developer.rhino3d.com", StringComparer.OrdinalIgnoreCase))
                domains.Add("developer.rhino3d.com");
            if (!domains.Contains("mcneel.github.io", StringComparer.OrdinalIgnoreCase))
                domains.Add("mcneel.github.io");
            return domains;
        }

        private static IEnumerable<string> BuildApiDocPipelineQueries(string query)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Action<string> add = q =>
            {
                q = (q ?? "").Trim();
                if (!string.IsNullOrWhiteSpace(q)) seen.Add(q);
            };

            add(query);
            string lower = (query ?? "").ToLowerInvariant();

            foreach (string symbol in ExtractApiSymbolCandidates(query))
                add(symbol + " RhinoCommon");

            if (lower.Contains("revolution") || lower.Contains("revsurface") || lower.Contains("revsurface") || lower.Contains("rev surface"))
            {
                add("RhinoCommon RevSurface Create");
                add("Rhino.Geometry.RevSurface Create");
                add("RhinoCommon Brep CreateFromRevSurface");
                add("surface of revolution RhinoCommon Brep");
            }

            if (lower.Contains("brep"))
            {
                add("Rhino.Geometry.Brep methods RhinoCommon");
                add("Brep Create RhinoCommon");
            }

            if (lower.Contains("curve"))
                add("Rhino.Geometry.Curve RhinoCommon methods");

            foreach (string q in seen)
                yield return q;
        }

        private static IEnumerable<string> ExtractApiSymbolCandidates(string query)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match match in Regex.Matches(query ?? "", @"\b(?:Rhino|Grasshopper)(?:\.[A-Za-z_][A-Za-z0-9_]*)+"))
            {
                string value = match.Value.Trim('.');
                if (seen.Add(value)) yield return value;
            }

            foreach (Match match in Regex.Matches(query ?? "", @"\b[A-Z][A-Za-z0-9_]+\.[A-Z][A-Za-z0-9_]+\b"))
            {
                string value = match.Value.Trim('.');
                if (seen.Add(value)) yield return value;
            }
        }

        private static void UpsertPipelineCandidate(Dictionary<string, JObject> map, JObject candidate, string provider, string query)
        {
            if (map == null || candidate == null) return;
            string url = candidate["url"]?.ToString();
            if (string.IsNullOrWhiteSpace(url)) return;
            var clone = (JObject)candidate.DeepClone();
            clone["provider"] = provider;
            clone["matched_query"] = query;
            if (!map.ContainsKey(url))
                map[url] = clone;
        }

        private static void TryMergePipelineSearchResults(Dictionary<string, JObject> map, string rawJson, string query)
        {
            try
            {
                var root = JObject.Parse(rawJson);
                var results = root["results"] as JArray;
                if (results == null) return;
                foreach (var item in results.OfType<JObject>())
                    UpsertPipelineCandidate(map, item, root["provider"]?.ToString() ?? "site_search", query);
            }
            catch
            {
                // Search failures are represented by empty fallback results.
            }
        }

        private static JArray BuildApiPipelineNextActions(int resultCount)
        {
            var actions = new JArray();
            if (resultCount > 0)
            {
                actions.Add("Pick the closest type/member candidate from stages[].results.");
                actions.Add("Call web_research with mode=fetch on the selected official URL before relying on a signature.");
                actions.Add("If candidate names differ from the guessed API, prefer the documented symbol and explain the correction.");
            }
            else
            {
                actions.Add("Classify as no_direct_hit or possible_api_name_mismatch, not as proof the API does not exist.");
                actions.Add("Retry api_pipeline with broader concept terms and candidate type names.");
                actions.Add("Use local compile/error feedback if available before generating final C#.");
            }
            return actions;
        }

        private sealed class ApiDocRoot
        {
            public string Name;
            public string BaseUrl;
            public string RootUrl;
            public string LocalHtmlRoot;
            public string[] NamespaceHints;
        }

        private sealed class ApiDocCandidate
        {
            public string Title;
            public string Url;
            public string Snippet;
            public int Score;
        }

        private static async Task<List<JObject>> SearchMcNeelApiDocsAsync(
            string query,
            List<string> allowedDomains,
            int maxResults,
            CancellationToken ct,
            WebResearchRunState state)
        {
            if (string.IsNullOrWhiteSpace(query))
                return new List<JObject>();

            if (allowedDomains != null
                && allowedDomains.Count > 0
                && !allowedDomains.Any(d => string.Equals(d, "mcneel.github.io", StringComparison.OrdinalIgnoreCase)))
            {
                return new List<JObject>();
            }

            string normalized = query.Trim();
            string lower = normalized.ToLowerInvariant();
            bool constrainedToMcNeel = allowedDomains != null
                && allowedDomains.Any(d => string.Equals(d, "mcneel.github.io", StringComparison.OrdinalIgnoreCase));
            bool looksLikeApiQuery = constrainedToMcNeel
                || lower.Contains("api")
                || lower.Contains("doc")
                || lower.Contains("rhino")
                || lower.Contains("rhinocommon")
                || lower.Contains("grasshopper")
                || lower.Contains("gh_")
                || lower.Contains("igh");

            if (!looksLikeApiQuery)
                return new List<JObject>();

            var roots = new List<ApiDocRoot>();
            bool includeGrasshopper = constrainedToMcNeel
                || lower.Contains("grasshopper")
                || lower.Contains("gh_")
                || lower.Contains("igh")
                || lower.Contains("kernel");
            bool includeRhino = constrainedToMcNeel
                || !includeGrasshopper
                || lower.Contains("rhino")
                || lower.Contains("rhinocommon")
                || lower.Contains("geometry")
                || lower.Contains("docobjects")
                || lower.Contains("clipping")
                || lower.Contains("hiddenline")
                || lower.Contains("objecttable");

            if (includeRhino)
            {
                roots.Add(new ApiDocRoot
                {
                    Name = "RhinoCommon",
                    BaseUrl = "https://mcneel.github.io/rhinocommon-api-docs/api/RhinoCommon/html/",
                    RootUrl = "https://mcneel.github.io/rhinocommon-api-docs/api/RhinoCommon/html/R_Project_RhinoCommon.htm",
                    LocalHtmlRoot = GetLocalDocumentationPath("rhinocommon-api-docs-gh-pages", "api", "RhinoCommon", "html"),
                    NamespaceHints = new[] { "Rhino", "Rhino.Geometry", "Rhino.DocObjects", "Rhino.DocObjects.Tables", "Rhino.Display", "Rhino.FileIO" }
                });
            }

            if (includeGrasshopper)
            {
                roots.Add(new ApiDocRoot
                {
                    Name = "Grasshopper",
                    BaseUrl = "https://mcneel.github.io/grasshopper-api-docs/api/grasshopper/html/",
                    RootUrl = "https://mcneel.github.io/grasshopper-api-docs/api/grasshopper/html/723c01da-9986-4db2-8f53-6f3a7494df75.htm",
                    LocalHtmlRoot = GetLocalDocumentationPath("grasshopper-api-docs-gh-pages", "api", "grasshopper", "html"),
                    NamespaceHints = new[] { "Grasshopper", "Grasshopper.Kernel", "Grasshopper.Kernel.Data", "Grasshopper.Kernel.Types" }
                });
            }

            var queryTokens = BuildSearchTokens(normalized);
            var allCandidates = new Dictionary<string, ApiDocCandidate>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in roots)
            {
                if (ShouldStopWebResearch(state, ct))
                    break;

                var pagesToFetch = new List<string> { root.RootUrl };

                foreach (string ns in ExtractNamespaces(normalized))
                {
                    if (ns.StartsWith("Rhino.", StringComparison.OrdinalIgnoreCase) && root.Name == "RhinoCommon")
                        pagesToFetch.Add(root.BaseUrl + "N_" + ns.Replace(".", "_") + ".htm");
                    else if (ns.StartsWith("Grasshopper.", StringComparison.OrdinalIgnoreCase) && root.Name == "Grasshopper")
                        pagesToFetch.Add(root.BaseUrl + "N_" + ns.Replace(".", "_") + ".htm");
                }

                foreach (string directTypeUrl in BuildDirectTypeUrls(root, normalized))
                    pagesToFetch.Add(directTypeUrl);

                string rootHtml = await TryDownloadTextAsync(root.RootUrl, ct, state).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(rootHtml))
                {
                    var rootLinks = ParseApiDocLinks(rootHtml, root.RootUrl, root.BaseUrl, queryTokens, includeZeroScore: true);
                    foreach (var link in rootLinks.Where(l => IsApiNamespacePage(l.Url)))
                    {
                        int nsScore = ScoreApiDocCandidate(link.Title, link.Url, queryTokens);
                        if (nsScore > 0 || constrainedToMcNeel || NamespaceLooksRelevant(lower, link.Title))
                            pagesToFetch.Add(link.Url);
                    }
                }

                foreach (string ns in root.NamespaceHints)
                {
                    if (NamespaceLooksRelevant(lower, ns))
                        pagesToFetch.Add(root.BaseUrl + "N_" + ns.Replace(".", "_") + ".htm");
                }

                var parsed = new List<ApiDocCandidate>();
                foreach (string pageUrl in pagesToFetch
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(Math.Max(1, DeploymentOptions.ApiDocPipelineIndexPageFetchLimit)))
                {
                    if (ShouldStopWebResearch(state, ct))
                        break;

                    string html = await TryDownloadTextAsync(pageUrl, ct, state).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(html))
                        continue;
                    parsed.AddRange(ParseApiDocLinks(html, pageUrl, root.BaseUrl, queryTokens, includeZeroScore: false));
                }

                foreach (var candidate in parsed)
                    UpsertApiDocCandidate(allCandidates, candidate);

                foreach (var typePage in parsed
                    .Where(c => c.Score > 0 && IsApiTypePage(c.Url))
                    .OrderByDescending(c => c.Score)
                    .Take(Math.Max(1, DeploymentOptions.ApiDocPipelineTypePageFetchLimit)))
                {
                    if (ShouldStopWebResearch(state, ct))
                        break;

                    string html = await TryDownloadTextAsync(typePage.Url, ct, state).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(html))
                        continue;
                    foreach (var child in ParseApiDocLinks(html, typePage.Url, root.BaseUrl, queryTokens, includeZeroScore: false))
                        UpsertApiDocCandidate(allCandidates, child);
                }
            }

            return allCandidates.Values
                .Where(c => c.Score > 0)
                .OrderByDescending(c => c.Score)
                .ThenBy(c => c.Title)
                .Take(Math.Max(1, Math.Min(maxResults, 10)))
                .Select(c => new JObject
                {
                    ["title"] = c.Title,
                    ["url"] = c.Url,
                    ["snippet"] = c.Snippet
                })
                .ToList();
        }

        private static async Task<string> TryDownloadTextAsync(string url, CancellationToken ct, WebResearchRunState state)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(url))
                    return "";

                lock (_webResearchTextCacheLock)
                {
                    if (_webResearchTextCache.TryGetValue(url, out string cached))
                    {
                        if (state != null) state.CacheHits++;
                        return cached;
                    }
                }

                string text = await DownloadTextAsync(url, ct, state).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(text)
                    && Uri.TryCreate(url, UriKind.Absolute, out Uri uri)
                    && string.Equals(uri.Host, "mcneel.github.io", StringComparison.OrdinalIgnoreCase))
                {
                    lock (_webResearchTextCacheLock)
                    {
                        if (_webResearchTextCache.Count > 300)
                            _webResearchTextCache.Clear();
                        _webResearchTextCache[url] = text;
                    }
                }
                return text;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                if (state != null) state.PartialTimeout = true;
                return "";
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return "";
            }
        }

        private static bool NamespaceLooksRelevant(string lowerQuery, string ns)
        {
            string lowerNs = ns.ToLowerInvariant();
            if (lowerQuery.Contains(lowerNs))
                return true;
            string last = lowerNs.Split('.').LastOrDefault() ?? "";
            return last.Length > 0 && lowerQuery.Contains(last);
        }

        private static IEnumerable<string> ExtractNamespaces(string query)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match match in Regex.Matches(query ?? "", @"\b(?:Rhino|Grasshopper)(?:\.[A-Za-z_][A-Za-z0-9_]*)+"))
            {
                string value = match.Value.Trim('.');
                string[] parts = value.Split('.');
                for (int i = parts.Length; i >= 2; i--)
                {
                    string ns = string.Join(".", parts.Take(i));
                    if (seen.Add(ns))
                        yield return ns;
                }
            }
        }

        private static IEnumerable<string> BuildDirectTypeUrls(ApiDocRoot root, string query)
        {
            foreach (Match match in Regex.Matches(query ?? "", @"\b(?:Rhino|Grasshopper)(?:\.[A-Za-z_][A-Za-z0-9_]*)+"))
            {
                string fullName = match.Value.Trim('.');
                yield return root.BaseUrl + "T_" + fullName.Replace(".", "_") + ".htm";
            }
        }

        private static List<string> BuildSearchTokens(string query)
        {
            var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "api", "apis", "doc", "docs", "official", "reference", "rhinocommon", "rhino", "grasshopper",
                "class", "type", "method", "property", "namespace", "csharp", "c", "cs", "sdk", "html"
            };
            return Regex.Split(query ?? "", @"[^A-Za-z0-9_]+")
                .Select(t => t.Trim().ToLowerInvariant())
                .Where(t => t.Length >= 2 && !stopWords.Contains(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static List<ApiDocCandidate> ParseApiDocLinks(string html, string pageUrl, string baseUrl, List<string> queryTokens, bool includeZeroScore)
        {
            var results = new List<ApiDocCandidate>();
            if (string.IsNullOrWhiteSpace(html))
                return results;

            foreach (Match match in Regex.Matches(html, "<a[^>]+href=\"(?<href>[^\"]+)\"[^>]*>(?<title>[\\s\\S]*?)</a>", RegexOptions.IgnoreCase))
            {
                string href = WebUtility.HtmlDecode(match.Groups["href"].Value ?? "").Trim();
                string title = CleanText(match.Groups["title"].Value);
                if (string.IsNullOrWhiteSpace(href) || string.IsNullOrWhiteSpace(title))
                    continue;
                if (!href.EndsWith(".htm", StringComparison.OrdinalIgnoreCase))
                    continue;

                Uri pageUri = new Uri(pageUrl);
                Uri uri = Uri.TryCreate(href, UriKind.Absolute, out Uri absolute)
                    ? absolute
                    : new Uri(pageUri, href);

                if (!uri.AbsoluteUri.StartsWith(baseUrl, StringComparison.OrdinalIgnoreCase))
                    continue;

                string file = uri.Segments.LastOrDefault() ?? "";
                if (!IsApiDocPage(file))
                    continue;

                int score = ScoreApiDocCandidate(title, uri.AbsoluteUri, queryTokens);
                if (score <= 0 && IsApiNamespacePage(uri.AbsoluteUri))
                    score = ScoreApiDocCandidate(file, uri.AbsoluteUri, queryTokens);
                if (score <= 0 && !includeZeroScore)
                    continue;

                results.Add(new ApiDocCandidate
                {
                    Title = title,
                    Url = uri.AbsoluteUri,
                    Snippet = "Official McNeel API documentation.",
                    Score = Math.Max(0, score)
                });
            }
            return results;
        }

        private static bool IsApiDocPage(string file)
        {
            return file.StartsWith("N_", StringComparison.OrdinalIgnoreCase)
                || file.StartsWith("T_", StringComparison.OrdinalIgnoreCase)
                || file.StartsWith("M_", StringComparison.OrdinalIgnoreCase)
                || file.StartsWith("P_", StringComparison.OrdinalIgnoreCase)
                || file.StartsWith("Overload_", StringComparison.OrdinalIgnoreCase)
                || file.StartsWith("Methods_", StringComparison.OrdinalIgnoreCase)
                || file.StartsWith("Properties_", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsApiNamespacePage(string url)
        {
            string file = new Uri(url).Segments.LastOrDefault() ?? "";
            return file.StartsWith("N_", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsApiTypePage(string url)
        {
            string file = new Uri(url).Segments.LastOrDefault() ?? "";
            return file.StartsWith("T_", StringComparison.OrdinalIgnoreCase);
        }

        private static int ScoreApiDocCandidate(string title, string url, List<string> queryTokens)
        {
            if (queryTokens == null || queryTokens.Count == 0)
                return 0;

            string haystack = ((title ?? "") + " " + (url ?? "")).ToLowerInvariant();
            int score = 0;
            foreach (string token in queryTokens)
            {
                if (haystack.Contains(token))
                    score += token.Length >= 8 ? 4 : 2;
            }
            if (url.IndexOf("/html/T_", StringComparison.OrdinalIgnoreCase) >= 0)
                score += 2;
            if (url.IndexOf("/html/M_", StringComparison.OrdinalIgnoreCase) >= 0)
                score += 2;
            if (url.IndexOf("/html/P_", StringComparison.OrdinalIgnoreCase) >= 0)
                score += 1;
            return score;
        }

        private static void UpsertApiDocCandidate(Dictionary<string, ApiDocCandidate> candidates, ApiDocCandidate candidate)
        {
            if (candidate == null || string.IsNullOrWhiteSpace(candidate.Url))
                return;

            if (!candidates.TryGetValue(candidate.Url, out ApiDocCandidate existing) || candidate.Score > existing.Score)
                candidates[candidate.Url] = candidate;
        }

        private sealed class LocalDocRoot
        {
            public string Name;
            public string Host;
            public string BaseUrl;
            public string LocalRoot;
        }

        private sealed class LocalDocHit
        {
            public string Title;
            public string Url;
            public string Snippet;
            public int Score;
        }

        private static string GetLocalDocumentationPath(params string[] parts)
        {
            try
            {
                string projectRoot = GetProjectRootDirectory();
                string parent = Directory.GetParent(projectRoot)?.FullName ?? projectRoot;
                return Path.Combine(new[] { parent }.Concat(parts ?? new string[0]).ToArray());
            }
            catch
            {
                return "";
            }
        }

        private static List<LocalDocRoot> GetLocalDocumentationRoots(List<string> allowedDomains)
        {
            var roots = new List<LocalDocRoot>
            {
                new LocalDocRoot
                {
                    Name = "RhinoCommon API",
                    Host = "mcneel.github.io",
                    BaseUrl = "https://mcneel.github.io/rhinocommon-api-docs/api/RhinoCommon/html/",
                    LocalRoot = GetLocalDocumentationPath("rhinocommon-api-docs-gh-pages", "api", "RhinoCommon", "html")
                },
                new LocalDocRoot
                {
                    Name = "Grasshopper API",
                    Host = "mcneel.github.io",
                    BaseUrl = "https://mcneel.github.io/grasshopper-api-docs/api/grasshopper/html/",
                    LocalRoot = GetLocalDocumentationPath("grasshopper-api-docs-gh-pages", "api", "grasshopper", "html")
                },
                new LocalDocRoot
                {
                    Name = "Rhino Developer Docs",
                    Host = "developer.rhino3d.com",
                    BaseUrl = "https://developer.rhino3d.com/",
                    LocalRoot = GetLocalDocumentationPath("developer.rhino3d.com-main", "content")
                }
            };

            return roots
                .Where(r => Directory.Exists(r.LocalRoot))
                .Where(r => allowedDomains == null
                    || allowedDomains.Count == 0
                    || allowedDomains.Any(d => string.Equals(d, r.Host, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        private static List<JObject> SearchLocalDocumentation(
            string query,
            List<string> allowedDomains,
            int maxResults,
            CancellationToken ct,
            WebResearchRunState state)
        {
            var tokens = BuildSearchTokens(query);
            if (tokens.Count == 0)
                return new List<JObject>();

            var hits = new Dictionary<string, LocalDocHit>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in GetLocalDocumentationRoots(allowedDomains))
            {
                if (ShouldStopWebResearch(state, ct))
                    break;

                IEnumerable<string> files;
                try
                {
                    files = Directory.EnumerateFiles(root.LocalRoot, "*.*", SearchOption.AllDirectories)
                        .Where(IsLocalSearchableDocument)
                        .Take(DeploymentOptions.ApiDocPipelineIndexPageFetchLimit * 20)
                        .ToList();
                }
                catch
                {
                    continue;
                }

                foreach (string file in files)
                {
                    if (ShouldStopWebResearch(state, ct))
                        break;

                    string text = TryReadLocalText(file, state);
                    if (string.IsNullOrWhiteSpace(text))
                        continue;

                    string title = ExtractTitle(text);
                    string plain = HtmlToPlainText(text);
                    int score = ScoreLocalDocument(file, title, plain, tokens);
                    if (score <= 0)
                        continue;

                    string url = BuildLocalDocUrl(root, file);
                    hits[url] = new LocalDocHit
                    {
                        Title = string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(file) : title,
                        Url = url,
                        Snippet = BuildLocalSnippet(plain, tokens),
                        Score = score
                    };
                }
            }

            return hits.Values
                .OrderByDescending(h => h.Score)
                .ThenBy(h => h.Title)
                .Take(Math.Max(1, Math.Min(maxResults, 10)))
                .Select(h => new JObject
                {
                    ["title"] = h.Title,
                    ["url"] = h.Url,
                    ["snippet"] = h.Snippet,
                    ["source"] = "local_documentation"
                })
                .ToList();
        }

        private static bool IsLocalSearchableDocument(string path)
        {
            string ext = Path.GetExtension(path);
            return string.Equals(ext, ".htm", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ext, ".html", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ext, ".md", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ext, ".txt", StringComparison.OrdinalIgnoreCase);
        }

        private static string TryReadLocalText(string path, WebResearchRunState state)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    return "";
                if (state != null)
                    state.RequestCount++;
                return File.ReadAllText(path);
            }
            catch
            {
                return "";
            }
        }

        private static int ScoreLocalDocument(string path, string title, string content, List<string> tokens)
        {
            string file = Path.GetFileName(path).ToLowerInvariant();
            string titleLower = (title ?? "").ToLowerInvariant();
            string contentLower = (content ?? "").ToLowerInvariant();
            int score = 0;
            foreach (string token in tokens)
            {
                if (file.Contains(token)) score += 8;
                if (titleLower.Contains(token)) score += 6;
                if (contentLower.Contains(token)) score += token.Length >= 8 ? 4 : 2;
            }
            if (file.StartsWith("T_", StringComparison.OrdinalIgnoreCase)) score += 2;
            if (file.StartsWith("M_", StringComparison.OrdinalIgnoreCase)) score += 2;
            return score;
        }

        private static string BuildLocalSnippet(string content, List<string> tokens)
        {
            if (string.IsNullOrWhiteSpace(content))
                return "";
            string compact = CleanText(content);
            int idx = -1;
            foreach (string token in tokens ?? new List<string>())
            {
                idx = compact.IndexOf(token, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0) break;
            }
            if (idx < 0) idx = 0;
            int start = Math.Max(0, idx - 120);
            int length = Math.Min(360, compact.Length - start);
            return compact.Substring(start, length);
        }

        private static string BuildLocalDocUrl(LocalDocRoot root, string file)
        {
            string relative = "";
            try
            {
                string rootPath = Path.GetFullPath(root.LocalRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string full = Path.GetFullPath(file);
                if (full.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
                    relative = full.Substring(rootPath.Length).Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
            }
            catch
            {
                relative = Path.GetFileName(file);
            }
            return (root.BaseUrl ?? "").TrimEnd('/') + "/" + relative;
        }

        private static bool TryResolveLocalDocumentationPath(string url, out string path)
        {
            path = null;
            if (string.IsNullOrWhiteSpace(url))
                return false;

            if (File.Exists(url))
            {
                path = Path.GetFullPath(url);
                return true;
            }

            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri uri))
                return false;

            string absolute = uri.AbsoluteUri;
            foreach (var root in GetLocalDocumentationRoots(null))
            {
                string baseUrl = (root.BaseUrl ?? "").TrimEnd('/') + "/";
                if (!absolute.StartsWith(baseUrl, StringComparison.OrdinalIgnoreCase))
                    continue;

                string relative = Uri.UnescapeDataString(absolute.Substring(baseUrl.Length))
                    .Replace('/', Path.DirectorySeparatorChar);
                string candidate = Path.GetFullPath(Path.Combine(root.LocalRoot, relative));
                string rootPath = Path.GetFullPath(root.LocalRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (candidate.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase) && File.Exists(candidate))
                {
                    path = candidate;
                    return true;
                }
            }

            return false;
        }

        private static async Task<string> FetchWebPageAsync(string url, List<string> allowedDomains, int maxChars, CancellationToken ct, WebResearchRunState state)
        {
            if (string.IsNullOrWhiteSpace(url))
                return "Error: url is required.";
            if (!TryResolveLocalDocumentationPath(url, out string localPath))
                return "Error: URL is not available in the local documentation mirror.";

            string html = await DownloadTextAsync(url, ct, state).ConfigureAwait(false);
            string title = ExtractTitle(html);
            string text = HtmlToPlainText(html);
            if (text.Length > maxChars)
                text = text.Substring(0, maxChars) + "...";

            return new JObject
            {
                ["mode"] = "fetch",
                ["url"] = url,
                ["local_path"] = localPath,
                ["title"] = title,
                ["content"] = text
            }.ToString(Formatting.None);
        }

        private static async Task<string> DownloadTextAsync(string url, CancellationToken ct, WebResearchRunState state)
        {
            if (ShouldStopWebResearch(state, ct))
                throw new OperationCanceledException(ct);

            if (!TryResolveLocalDocumentationPath(url, out string localPath))
                throw new InvalidOperationException("Local documentation mirror does not contain URL: " + url);

            string text = TryReadLocalText(localPath, state);
            return await Task.FromResult(text ?? "").ConfigureAwait(false);
        }

        private static string ExtractTitle(string html)
        {
            var match = Regex.Match(html ?? "", "<title[^>]*>(?<title>.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            return match.Success ? CleanText(match.Groups["title"].Value) : "";
        }

        private static string HtmlToPlainText(string html)
        {
            if (string.IsNullOrWhiteSpace(html))
                return "";

            string text = Regex.Replace(html, "<script[\\s\\S]*?</script>", " ", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, "<style[\\s\\S]*?</style>", " ", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, "<[^>]+>", " ");
            return CleanText(text);
        }

        private static string CleanText(string value)
        {
            string text = WebUtility.HtmlDecode(value ?? "");
            text = Regex.Replace(text, "<[^>]+>", " ");
            text = Regex.Replace(text, "\\s+", " ").Trim();
            return text;
        }
    }
}
