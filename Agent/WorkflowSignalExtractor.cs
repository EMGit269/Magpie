using System;
using System.Text.RegularExpressions;

namespace Magpie.Agent
{
    public sealed class WorkflowSignalExtractor
    {
        public WorkflowSignals Extract(AgentTurnContext context)
        {
            var signals = new WorkflowSignals();
            if (context == null) return signals;

            string text = context.UserText ?? "";
            string lower = text.ToLowerInvariant();

            signals.HasImageAttachments = context.HasImageAttachments;
            signals.HasCSharpCodeBlock = Regex.IsMatch(text, @"```(?:csharp|cs|c#)?[\s\S]*?```", RegexOptions.IgnoreCase);
            signals.HasCSharpSignal = ContainsAny(lower, "c#", "csharp", "script", "csx", "using rhino", "using grasshopper", "script component", "脚本", "编译");
            signals.HasCompileError = Regex.IsMatch(text, @"\bCS\d{4}\b", RegexOptions.IgnoreCase)
                || ContainsAny(lower, "compile error", "compiler error", "编译错误", "报错", "exception");

            signals.HasRhinoCommonSymbol = Regex.IsMatch(text, @"\bRhino(?:\.[A-Za-z_][A-Za-z0-9_]*)+\b")
                || ContainsAny(lower, "rhinocommon", "rhino.geometry", "rhino.docobjects", "rhino.display");
            signals.HasGrasshopperSymbol = Regex.IsMatch(text, @"\bGrasshopper(?:\.[A-Za-z_][A-Za-z0-9_]*)+\b")
                || ContainsAny(lower, "grasshopper.kernel", "igh_", "gh_");
            signals.HasApiMemberPattern = Regex.IsMatch(text, @"\b[A-Z][A-Za-z0-9_]+\.[A-Z][A-Za-z0-9_]+\b")
                || Regex.IsMatch(text, @"\b(?:Rhino|Grasshopper)(?:\.[A-Za-z_][A-Za-z0-9_]*){2,}\b");
            signals.MentionsSignatureOrOverload = ContainsAny(lower,
                "signature", "overload", "constructor", "method", "property", "return type",
                "签名", "重载", "构造函数", "方法", "属性", "返回值", "参数类型");
            signals.UserAskedExternalVerification = ContainsAny(lower,
                "official", "docs", "documentation", "api doc", "verify", "check api",
                "官方", "文档", "查证", "核实", "确认 api", "api签名", "api 签名");
            signals.UserAskedWebResearch = ContainsAny(lower,
                "url", "http", "docs", "documentation", "official", "search", "api doc", "文档", "网页", "搜索", "官方");

            signals.UserAskedReferenceImport = ContainsAny(lower, "import reference", "import_reference", "导入参考", "复用参考", "参考画布");
            signals.UserAskedReferenceLookup = ContainsAny(lower, "reference", "查参考", "读取参考", "参考");
            signals.UserAskedSkillLookup = ContainsAny(lower, "skill", "技能", "经验", "沉淀", "训练");
            signals.UserAskedCreate = ContainsAny(lower, "create", "add", "generate", "新建", "创建", "生成", "做一个", "画一个", "参数化");
            signals.UserAskedModify = ContainsAny(lower, "edit", "modify", "delete", "remove", "fix", "修改", "调整", "修复", "优化", "继续", "删除");
            signals.UserAskedImageGeneration = ContainsAny(lower, "image generation", "ai image", "生成图片", "创作图片", "改图", "图生图", "ai 图片", "ai图片");
            signals.UserAskedVisualModeling = ContainsAny(lower, "grasshopper", "生成gh", "生成 gh", "建模", "还原", "复刻", "做成", "参数化");
            signals.UserAskedGrasshopper = ContainsAny(lower, "grasshopper", "gh", "电池", "画布", "参数化");

            return signals;
        }

        private static bool ContainsAny(string lower, params string[] needles)
        {
            if (string.IsNullOrEmpty(lower) || needles == null) return false;
            foreach (string needle in needles)
            {
                if (!string.IsNullOrWhiteSpace(needle)
                    && lower.IndexOf(needle.ToLowerInvariant(), StringComparison.Ordinal) >= 0)
                    return true;
            }
            return false;
        }
    }
}
