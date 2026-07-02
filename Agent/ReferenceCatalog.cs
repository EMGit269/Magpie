using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace Magpie.Agent
{
    public sealed class ReferenceCatalog
    {
        public const string IndexFileName = "reference.index.json";
        private const int MaxDescriptionChars = 320;
        private const int MaxSummaryChars = 10000;

        public ReferenceIndex LoadOrBuildIndex(string referenceDirectory, string referenceIndexMarkdownPath)
        {
            var index = BuildIndex(referenceDirectory, referenceIndexMarkdownPath);
            SaveIndex(referenceDirectory, index);
            return index;
        }

        public ReferenceIndex LoadIndex(string referenceDirectory, string referenceIndexMarkdownPath)
        {
            try
            {
                string path = GetIndexPath(referenceDirectory);
                if (!File.Exists(path))
                    return LoadOrBuildIndex(referenceDirectory, referenceIndexMarkdownPath);

                var index = JsonConvert.DeserializeObject<ReferenceIndex>(File.ReadAllText(path, Encoding.UTF8));
                return index ?? LoadOrBuildIndex(referenceDirectory, referenceIndexMarkdownPath);
            }
            catch
            {
                return LoadOrBuildIndex(referenceDirectory, referenceIndexMarkdownPath);
            }
        }

        public string RenderSummary(string referenceDirectory, string referenceIndexMarkdownPath)
        {
            var index = LoadOrBuildIndex(referenceDirectory, referenceIndexMarkdownPath);
            if (index.References == null || index.References.Count == 0)
                return "";

            var lines = new List<string>();
            int used = 0;
            foreach (var entry in index.References.OrderBy(e => e.FileName, StringComparer.OrdinalIgnoreCase))
            {
                string availability = entry.JsonExists ? "json" : (entry.GhExists ? "gh" : "missing");
                string skill = string.IsNullOrWhiteSpace(entry.SkillFileName) ? "" : "; skill: " + entry.SkillFileName;
                string line = string.Format(
                    "- [{0}]: {1} (file: {2}; available: {3}{4})",
                    entry.Id,
                    Compact(entry.Description, MaxDescriptionChars),
                    entry.FileName,
                    availability,
                    skill);
                if (used + line.Length > MaxSummaryChars)
                {
                    lines.Add("- ... additional references omitted from prompt budget.");
                    break;
                }
                lines.Add(line);
                used += line.Length + 1;
            }

            return "\n\n[Reference Catalog]\n"
                + string.Join("\n", lines)
                + "\nRule: plan the GH logic first, then use this catalog to choose a clearly relevant reference. Read JSON only with read_reference_json; import GH/GHX only when reuse is intended.";
        }

        public ReferenceIndexEntry FindByFileName(string referenceDirectory, string referenceIndexMarkdownPath, string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return null;
            string safeName = Path.GetFileName(fileName);
            return LoadIndex(referenceDirectory, referenceIndexMarkdownPath).References.FirstOrDefault(r =>
                string.Equals(r.FileName, safeName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(r.Id, Path.GetFileNameWithoutExtension(safeName), StringComparison.OrdinalIgnoreCase));
        }

        private static ReferenceIndex BuildIndex(string referenceDirectory, string referenceIndexMarkdownPath)
        {
            var index = new ReferenceIndex();
            if (!string.IsNullOrWhiteSpace(referenceIndexMarkdownPath) && File.Exists(referenceIndexMarkdownPath))
            {
                string content = File.ReadAllText(referenceIndexMarkdownPath, Encoding.UTF8);
                foreach (var entry in ParseReferenceIndexMarkdown(content, referenceDirectory))
                    Upsert(index, entry);
            }

            if (!string.IsNullOrWhiteSpace(referenceDirectory) && Directory.Exists(referenceDirectory))
            {
                foreach (string path in Directory.GetFiles(referenceDirectory, "*.json").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                {
                    string fileName = Path.GetFileName(path);
                    if (string.Equals(fileName, IndexFileName, StringComparison.OrdinalIgnoreCase)) continue;
                    var existing = index.References.FirstOrDefault(r => string.Equals(r.FileName, fileName, StringComparison.OrdinalIgnoreCase));
                    if (existing != null)
                    {
                        existing.JsonExists = true;
                        existing.TokenEstimate = EstimateTokens(File.ReadAllText(path, Encoding.UTF8));
                        continue;
                    }

                    Upsert(index, new ReferenceIndexEntry
                    {
                        Id = NormalizeId(Path.GetFileNameWithoutExtension(fileName)),
                        FileName = fileName,
                        Description = "Saved Grasshopper reference JSON.",
                        JsonExists = true,
                        TokenEstimate = EstimateTokens(File.ReadAllText(path, Encoding.UTF8))
                    });
                }
            }

            return index;
        }

        private static IEnumerable<ReferenceIndexEntry> ParseReferenceIndexMarkdown(string content, string referenceDirectory)
        {
            if (string.IsNullOrWhiteSpace(content))
                yield break;

            var matches = Regex.Matches(
                content,
                @"-\s*描述：(?<desc>.*?)\r?\n\s*文件：reference/(?<file>[^\r\n]+)\r?\n\s*调用：(?<hint>[^\r\n]+)(?:\r?\n\s*技能：skills/(?<skill>[^\r\n]+))?",
                RegexOptions.Singleline);

            foreach (Match match in matches)
            {
                string fileName = Path.GetFileName((match.Groups["file"].Value ?? "").Trim());
                if (string.IsNullOrWhiteSpace(fileName))
                    continue;

                string path = string.IsNullOrWhiteSpace(referenceDirectory) ? "" : Path.Combine(referenceDirectory, fileName);
                string ext = Path.GetExtension(fileName);
                bool isJson = string.Equals(ext, ".json", StringComparison.OrdinalIgnoreCase);
                bool isGh = string.Equals(ext, ".gh", StringComparison.OrdinalIgnoreCase) || string.Equals(ext, ".ghx", StringComparison.OrdinalIgnoreCase);

                yield return new ReferenceIndexEntry
                {
                    Id = NormalizeId(Path.GetFileNameWithoutExtension(fileName)),
                    FileName = fileName,
                    Description = Compact(Clean(match.Groups["desc"].Value), MaxDescriptionChars),
                    SkillFileName = Path.GetFileName((match.Groups["skill"].Value ?? "").Trim()),
                    ImportHint = Clean(match.Groups["hint"].Value),
                    JsonExists = isJson && File.Exists(path),
                    GhExists = isGh && File.Exists(path),
                    TokenEstimate = File.Exists(path) && isJson ? EstimateTokens(File.ReadAllText(path, Encoding.UTF8)) : 0
                };
            }
        }

        private static void Upsert(ReferenceIndex index, ReferenceIndexEntry entry)
        {
            if (index == null || entry == null || string.IsNullOrWhiteSpace(entry.FileName)) return;
            index.References.RemoveAll(r => string.Equals(r.FileName, entry.FileName, StringComparison.OrdinalIgnoreCase));
            index.References.Add(entry);
        }

        private static void SaveIndex(string referenceDirectory, ReferenceIndex index)
        {
            if (string.IsNullOrWhiteSpace(referenceDirectory) || !Directory.Exists(referenceDirectory)) return;
            File.WriteAllText(GetIndexPath(referenceDirectory), JsonConvert.SerializeObject(index, Formatting.Indented), Encoding.UTF8);
        }

        private static string GetIndexPath(string referenceDirectory)
        {
            return Path.Combine(referenceDirectory ?? "", IndexFileName);
        }

        private static string NormalizeId(string value)
        {
            string s = (value ?? "").Trim().ToLowerInvariant();
            s = Regex.Replace(s, @"[^a-z0-9]+", "-").Trim('-');
            return string.IsNullOrWhiteSpace(s) ? "reference" : s;
        }

        private static string Clean(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            return string.Join(" ", value.Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries));
        }

        private static string Compact(string value, int maxChars)
        {
            string s = Clean(value);
            return s.Length <= maxChars ? s : s.Substring(0, Math.Max(0, maxChars - 3)) + "...";
        }

        private static int EstimateTokens(string content)
        {
            return string.IsNullOrEmpty(content) ? 0 : Math.Max(1, content.Length / 4);
        }
    }
}
