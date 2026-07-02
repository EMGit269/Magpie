using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace Magpie.Agent
{
    public sealed class SkillCatalog
    {
        public const string IndexFileName = "skills.index.json";
        private const int MaxDescriptionChars = 280;
        private const int MaxSummaryChars = 12000;

        public SkillIndex LoadOrBuildIndex(string skillsDirectory)
        {
            var scanned = ScanSkills(skillsDirectory);
            SaveIndex(skillsDirectory, scanned);
            return scanned;
        }

        public SkillIndex LoadIndex(string skillsDirectory)
        {
            try
            {
                string path = GetIndexPath(skillsDirectory);
                if (!File.Exists(path))
                    return LoadOrBuildIndex(skillsDirectory);

                var index = JsonConvert.DeserializeObject<SkillIndex>(File.ReadAllText(path, Encoding.UTF8));
                return index ?? LoadOrBuildIndex(skillsDirectory);
            }
            catch
            {
                return LoadOrBuildIndex(skillsDirectory);
            }
        }

        public IReadOnlyList<SkillIndexEntry> Search(string skillsDirectory, string query, WorkflowIntent? intent, int limit)
        {
            var index = LoadIndex(skillsDirectory);
            string q = (query ?? "").Trim().ToLowerInvariant();
            IEnumerable<SkillIndexEntry> entries = index.Skills ?? new List<SkillIndexEntry>();

            if (intent.HasValue)
            {
                entries = entries.Where(s => s.Workflows != null && s.Workflows.Contains(intent.Value));
            }

            if (!string.IsNullOrWhiteSpace(q))
            {
                entries = entries
                    .Select(s => new { Entry = s, Score = Score(s, q) })
                    .Where(x => x.Score > 0)
                    .OrderByDescending(x => x.Score)
                    .ThenBy(x => x.Entry.Id)
                    .Select(x => x.Entry);
            }
            else
            {
                entries = entries.OrderByDescending(s => QualityRank(s.Quality)).ThenBy(s => s.Id);
            }

            return entries.Take(Math.Max(1, limit)).ToList();
        }

        public SkillIndexEntry FindByFileName(string skillsDirectory, string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return null;
            string safeName = Path.GetFileName(fileName);
            if (!safeName.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                safeName += ".md";

            return LoadIndex(skillsDirectory).Skills.FirstOrDefault(s =>
                string.Equals(s.FileName, safeName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetFileName(s.Path), safeName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(s.Id, Path.GetFileNameWithoutExtension(safeName), StringComparison.OrdinalIgnoreCase));
        }

        public string LoadSkillBody(string skillsDirectory, SkillIndexEntry entry)
        {
            if (entry == null) return "Error: skill entry is null.";
            string path = ResolveSkillPath(skillsDirectory, entry);
            if (!File.Exists(path)) return "Error: 找不到技能文件 " + (entry.FileName ?? entry.Path ?? entry.Id);

            string body = File.ReadAllText(path, Encoding.UTF8);
            return FormatSkillBody(entry, path, body);
        }

        public string RenderSummary(string skillsDirectory)
        {
            var index = LoadOrBuildIndex(skillsDirectory);
            if (index.Skills == null || index.Skills.Count == 0) return "";

            var lines = new List<string>();
            int used = 0;
            foreach (var skill in index.Skills.OrderByDescending(s => QualityRank(s.Quality)).ThenBy(s => s.Id))
            {
                string line = string.Format(
                    "- [{0}]: {1} (文件: {2}; quality: {3}; verified: {4})",
                    skill.Id,
                    Compact(skill.Description, MaxDescriptionChars),
                    skill.FileName,
                    skill.Quality,
                    skill.Verified ? "true" : "false");
                if (used + line.Length > MaxSummaryChars)
                {
                    lines.Add("- ... additional skills omitted from prompt budget.");
                    break;
                }
                lines.Add(line);
                used += line.Length + 1;
            }

            return "\n\n【当前项目可用技能库】:\n"
                + string.Join("\n", lines)
                + "\n规则：先按用户任务、workflow、关键词匹配 skill 摘要；只有相关时才调用 read_skill_file 读取正文。不要全量读取 skill，也不要只看摘要就凭记忆实现。";
        }

        public void Upsert(string skillsDirectory, SkillIndexEntry entry)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.FileName)) return;
            var index = LoadIndex(skillsDirectory);
            index.Skills.RemoveAll(s =>
                string.Equals(s.FileName, entry.FileName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(s.Id, entry.Id, StringComparison.OrdinalIgnoreCase));
            index.Skills.Add(entry);
            SaveIndex(skillsDirectory, index);
        }

        public SkillIndexEntry BuildEntryFromFile(string skillsDirectory, string fileName, string qualityOverride = null, bool? verifiedOverride = null)
        {
            string safeName = Path.GetFileName(fileName);
            string path = Path.Combine(skillsDirectory, safeName);
            if (!File.Exists(path)) return null;
            string content = File.ReadAllText(path, Encoding.UTF8);
            return ParseSkillFile(skillsDirectory, path, content, qualityOverride, verifiedOverride);
        }

        private static SkillIndex ScanSkills(string skillsDirectory)
        {
            var index = new SkillIndex();
            if (string.IsNullOrWhiteSpace(skillsDirectory) || !Directory.Exists(skillsDirectory))
                return index;

            foreach (string path in Directory.GetFiles(skillsDirectory, "*.md").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                string fileName = Path.GetFileName(path);
                if (fileName.Equals("index.md", StringComparison.OrdinalIgnoreCase)) continue;
                if (fileName.Equals("skills.index.md", StringComparison.OrdinalIgnoreCase)) continue;

                try
                {
                    string content = File.ReadAllText(path, Encoding.UTF8);
                    var entry = ParseSkillFile(skillsDirectory, path, content, null, null);
                    if (entry != null)
                        index.Skills.Add(entry);
                }
                catch
                {
                    // Keep index generation best-effort; invalid skills simply do not enter the catalog.
                }
            }

            return index;
        }

        private static SkillIndexEntry ParseSkillFile(string skillsDirectory, string path, string content, string qualityOverride, bool? verifiedOverride)
        {
            string fileName = Path.GetFileName(path);
            var metadata = ParseFrontmatter(content);
            string title = Get(metadata, "name");
            if (string.IsNullOrWhiteSpace(title))
                title = Path.GetFileNameWithoutExtension(fileName).Replace("_", "-");

            string description = Get(metadata, "description");
            if (string.IsNullOrWhiteSpace(description))
                description = FirstMarkdownHeadingOrLine(StripFrontmatter(content));

            string quality = qualityOverride ?? InferQuality(fileName, title);
            bool verified = verifiedOverride ?? string.Equals(quality, "official", StringComparison.OrdinalIgnoreCase);

            var entry = new SkillIndexEntry
            {
                Id = NormalizeId(title),
                Title = title.Trim(),
                Description = Compact(description, MaxDescriptionChars),
                FileName = fileName,
                Path = fileName,
                Quality = quality,
                Verified = verified,
                LastVerifiedAt = "",
                TokenEstimate = EstimateTokens(content)
            };
            foreach (var tag in InferTags(fileName, title, description, content))
                entry.Tags.Add(tag);
            foreach (var workflow in InferWorkflows(entry.Tags, fileName, title, description))
                entry.Workflows.Add(workflow);
            return entry;
        }

        private static Dictionary<string, string> ParseFrontmatter(string content)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(content)) return map;

            string normalized = content.Replace("\r\n", "\n");
            if (!normalized.StartsWith("---\n", StringComparison.Ordinal)) return map;
            int end = normalized.IndexOf("\n---", 4, StringComparison.Ordinal);
            if (end < 0) return map;
            string front = normalized.Substring(4, end - 4);
            foreach (string raw in front.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                int idx = line.IndexOf(':');
                if (idx <= 0) continue;
                string key = line.Substring(0, idx).Trim();
                string value = line.Substring(idx + 1).Trim().Trim('"', '\'');
                if (!map.ContainsKey(key)) map[key] = value;
            }
            return map;
        }

        private static string FormatSkillBody(SkillIndexEntry entry, string absolutePath, string body)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Skill: " + entry.Id);
            if (!string.IsNullOrWhiteSpace(entry.Description))
                sb.AppendLine("> " + entry.Description.Trim());
            sb.AppendLine();
            sb.AppendLine("Source: `" + absolutePath + "`");
            sb.AppendLine("Quality: `" + (entry.Quality ?? "unknown") + "`, verified: `" + (entry.Verified ? "true" : "false") + "`");
            if (entry.Tags != null && entry.Tags.Count > 0)
                sb.AppendLine("Tags: `" + string.Join("`, `", entry.Tags) + "`");
            sb.AppendLine();
            sb.AppendLine("## SKILL.md");
            sb.AppendLine();
            sb.AppendLine((body ?? "").Trim());
            return sb.ToString();
        }

        private static string ResolveSkillPath(string skillsDirectory, SkillIndexEntry entry)
        {
            string fileName = Path.GetFileName(!string.IsNullOrWhiteSpace(entry.FileName) ? entry.FileName : entry.Path);
            return Path.GetFullPath(Path.Combine(skillsDirectory, fileName));
        }

        private static string GetIndexPath(string skillsDirectory)
        {
            return Path.Combine(skillsDirectory, IndexFileName);
        }

        private static void SaveIndex(string skillsDirectory, SkillIndex index)
        {
            if (string.IsNullOrWhiteSpace(skillsDirectory)) return;
            Directory.CreateDirectory(skillsDirectory);
            string json = JsonConvert.SerializeObject(index, Formatting.Indented);
            File.WriteAllText(GetIndexPath(skillsDirectory), json, Encoding.UTF8);
        }

        private static int Score(SkillIndexEntry entry, string query)
        {
            int score = 0;
            string id = (entry.Id ?? "").ToLowerInvariant();
            string title = (entry.Title ?? "").ToLowerInvariant();
            string desc = (entry.Description ?? "").ToLowerInvariant();
            if (id == query || title == query) score += 10;
            if (id.Contains(query)) score += 4;
            if (title.Contains(query)) score += 4;
            if (desc.Contains(query)) score += 2;
            if (entry.Tags != null && entry.Tags.Any(t => (t ?? "").ToLowerInvariant().Contains(query))) score += 2;
            return score;
        }

        private static int QualityRank(string quality)
        {
            switch ((quality ?? "").Trim().ToLowerInvariant())
            {
                case "official": return 3;
                case "trained": return 2;
                case "experimental": return 1;
                default: return 0;
            }
        }

        private static string InferQuality(string fileName, string title)
        {
            string s = ((fileName ?? "") + " " + (title ?? "")).ToLowerInvariant();
            if (s.Contains("trained")) return "trained";
            if (s.Contains("system_") || s.Contains("official") || s.Contains("reference-index")) return "official";
            return "experimental";
        }

        private static IEnumerable<string> InferTags(string fileName, string title, string description, string content)
        {
            string s = ((fileName ?? "") + " " + (title ?? "") + " " + (description ?? "") + " " + (content ?? "")).ToLowerInvariant();
            var tags = new List<string>();
            AddIf(tags, s, "grasshopper", "grasshopper", " gh ", "电池", "画布");
            AddIf(tags, s, "csharp", "c#", "csharp", "script", "脚本");
            AddIf(tags, s, "visual", "visual", "视觉", "预览", "图片");
            AddIf(tags, s, "reference", "reference", "参考");
            AddIf(tags, s, "web", "web", "联网", "api", "官方文档");
            AddIf(tags, s, "self-training", "trained", "自训练", "沉淀");
            if (tags.Count == 0) tags.Add("general");
            return tags.Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private static IEnumerable<WorkflowIntent> InferWorkflows(IEnumerable<string> tags, string fileName, string title, string description)
        {
            var set = new HashSet<WorkflowIntent>();
            var tagSet = new HashSet<string>(tags ?? new string[0], StringComparer.OrdinalIgnoreCase);
            if (tagSet.Contains("csharp"))
            {
                set.Add(WorkflowIntent.CSharpScriptCreate);
                set.Add(WorkflowIntent.CSharpScriptFix);
            }
            if (tagSet.Contains("visual"))
            {
                set.Add(WorkflowIntent.VisualModeling);
                set.Add(WorkflowIntent.VisualUnderstanding);
            }
            if (tagSet.Contains("reference"))
            {
                set.Add(WorkflowIntent.ReferenceLookup);
                set.Add(WorkflowIntent.ReferenceImport);
            }
            if (tagSet.Contains("web")) set.Add(WorkflowIntent.WebResearch);
            if (tagSet.Contains("self-training")) set.Add(WorkflowIntent.SelfTraining);
            if (tagSet.Contains("grasshopper") || set.Count == 0)
            {
                set.Add(WorkflowIntent.GrasshopperCreate);
                set.Add(WorkflowIntent.GrasshopperModify);
            }
            return set;
        }

        private static void AddIf(List<string> tags, string source, string tag, params string[] needles)
        {
            if (needles.Any(n => source.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0))
                tags.Add(tag);
        }

        private static string StripFrontmatter(string content)
        {
            if (string.IsNullOrWhiteSpace(content)) return "";
            return Regex.Replace(content.Trim(), @"\A---\s*[\s\S]*?\s*---\s*", "").Trim();
        }

        private static string FirstMarkdownHeadingOrLine(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return "";
            foreach (var line in body.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string s = line.Trim().TrimStart('#').Trim();
                if (!string.IsNullOrWhiteSpace(s)) return Compact(s, MaxDescriptionChars);
            }
            return "";
        }

        private static string NormalizeId(string value)
        {
            string s = (value ?? "").Trim().ToLowerInvariant();
            s = Regex.Replace(s, @"[^a-z0-9\u4e00-\u9fff]+", "-").Trim('-');
            return string.IsNullOrWhiteSpace(s) ? "skill" : s;
        }

        private static string Get(Dictionary<string, string> map, string key)
        {
            return map.TryGetValue(key, out string value) ? value : "";
        }

        private static string Compact(string value, int maxChars)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            string s = string.Join(" ", value.Split(new[] { '\r', '\n', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries));
            return s.Length <= maxChars ? s : s.Substring(0, Math.Max(0, maxChars - 3)) + "...";
        }

        private static int EstimateTokens(string value)
        {
            return string.IsNullOrEmpty(value) ? 0 : Math.Max(1, value.Length / 3);
        }
    }
}
