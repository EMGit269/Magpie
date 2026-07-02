using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Magpie.Agent
{
    public sealed class SkillQualityGate
    {
        public SkillQualityReport Evaluate(string fileName, string markdown)
        {
            var report = new SkillQualityReport
            {
                FileName = fileName ?? "",
                Pass = true,
                RecommendedQuality = "trained",
                Verified = true
            };

            string body = markdown ?? "";
            Require(report, !string.IsNullOrWhiteSpace(body), "skill markdown is empty");
            Require(report, HasFrontmatter(body, "name"), "missing frontmatter name");
            Require(report, HasFrontmatter(body, "description"), "missing frontmatter description");
            Require(report, Regex.IsMatch(body, @"(?im)^##\s+"), "missing at least one markdown section");
            Require(report, ContainsAny(body, "验证", "检查", "review", "check", "recompute", "error"), "missing validation/check guidance");
            Require(report, body.Length <= 16000, "skill is too long for prompt-efficient reuse");

            if (!report.Pass)
            {
                report.RecommendedQuality = "experimental";
                report.Verified = false;
            }

            return report;
        }

        private static bool HasFrontmatter(string markdown, string key)
        {
            if (string.IsNullOrWhiteSpace(markdown) || string.IsNullOrWhiteSpace(key)) return false;
            string normalized = markdown.Replace("\r\n", "\n");
            if (!normalized.StartsWith("---\n", StringComparison.Ordinal)) return false;
            int end = normalized.IndexOf("\n---", 4, StringComparison.Ordinal);
            if (end < 0) return false;
            string front = normalized.Substring(4, end - 4);
            return front.Split('\n').Any(line => line.TrimStart().StartsWith(key + ":", StringComparison.OrdinalIgnoreCase));
        }

        private static bool ContainsAny(string value, params string[] needles)
        {
            if (string.IsNullOrWhiteSpace(value) || needles == null) return false;
            return needles.Any(n => value.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static void Require(SkillQualityReport report, bool condition, string message)
        {
            if (condition) return;
            report.Pass = false;
            report.Issues.Add(message);
        }
    }

    public sealed class SkillQualityReport
    {
        public string FileName { get; set; }
        public bool Pass { get; set; }
        public string RecommendedQuality { get; set; }
        public bool Verified { get; set; }
        public List<string> Issues { get; private set; } = new List<string>();

        public string ToLogLine()
        {
            return (Pass ? "pass" : "fail")
                + "; quality=" + (RecommendedQuality ?? "")
                + "; verified=" + Verified.ToString().ToLowerInvariant()
                + "; issues=" + string.Join(", ", Issues ?? new List<string>());
        }
    }
}
