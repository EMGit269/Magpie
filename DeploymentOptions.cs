using System;

namespace Magpie
{
    /// <summary>
    /// 运行环境与分发策略相关的默认值。内测或调试可通过环境变量放宽策略。
    /// </summary>
    public static class DeploymentOptions
    {
        /// <summary>
        /// 设为 "1" 时不再使用 DPAPI 加密 API Key，仍写入 Grasshopper 明文设置（仅建议本地调试）。
        /// </summary>
        public static bool UseDpapiForApiKeys =>
            !string.Equals(Environment.GetEnvironmentVariable("MAGPIE_PLAINTEXT_API_KEYS"), "1", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// 设为 "1" 时写入调试级日志（噪声较大）。
        /// </summary>
        public static bool EnableVerboseLogging =>
            string.Equals(Environment.GetEnvironmentVariable("MAGPIE_LOG_VERBOSE"), "1", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// 设为 "1" 时，<see cref="AddGhLog.Error"/> 与 <see cref="AddGhLog.UserAlert"/> 会弹出简单 MessageBox（临时排错；平时勿开）。
        /// </summary>
        public static bool EnableTemporaryErrorPopup =>
            string.Equals(Environment.GetEnvironmentVariable("MAGPIE_ERROR_POPUP"), "1", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// 会话内存中保留的最大消息条数（含 system / user / assistant / tool）；超出则从紧邻 system 与滚动摘要之后按「消息组」丢弃最早条目。
        /// </summary>
        public const int MaxPersistedChatMessages = 320;

        /// <summary>Tier1 滚动摘要 assistant 消息内容固定前缀（整段替换）。</summary>
        public const string RollingSummaryHeader = "【上文摘要（为节省上下文自动生成）】\n";

        /// <summary>用于估算「是否触发压缩」的全局预算（近似 token，启发式）。</summary>
        public const int ContextBudgetTokens = 128000;

        // Reserved output budget used by the agent context budget. Keep modest for
        // current providers; provider-specific dynamic windows can replace this later.
        public const int ContextReservedOutputTokens = 8192;

        /// <summary>达到预算的该比例时尝试 LLM 摘要（预留 headroom 给摘要请求与回复）。</summary>
        public const double ContextCompressTriggerRatio = 0.72;

        /// <summary>摘要后 Tier2 尾部保留的完整消息条数。</summary>
        public const int ContextVerbatimTailCount = 16;

        /// <summary>喂给摘要模型的拼接文本上限（字符），超出截断。</summary>
        public const int SummaryRequestMaxChars = 48000;

        /// <summary>软预算：仅用于计量 UI 与日志，硬限制仍以 <see cref="ContextBudgetTokens"/> 为主。</summary>
        public const int Tier0SoftBudgetTokens = 8000;
        public const int Tier1SoftBudgetTokens = 6000;
        public const int Tier2SoftBudgetTokens = 110000;

        /// <summary>超过该字符长度的 tool content 在机械降载时可被折叠（保留每种 tool 名最后一次大体量结果）。</summary>
        public const int LargeToolFoldMinChars = 10000;

        /// <summary>画布导出 JSON 是否带时间戳；默认关闭以利于云端前缀缓存稳定。环境变量 MAGPIE_CANVAS_TIMESTAMP=1 时开启。</summary>
        public static bool IncludeCanvasExportTimestamp =>
            string.Equals(Environment.GetEnvironmentVariable("MAGPIE_CANVAS_TIMESTAMP"), "1", StringComparison.OrdinalIgnoreCase);

        /// <summary>Tier0 首条 system 中「电池全名索引」段落的最大字符数（含小节标题）；超出截断以利于网关与上下文上限。</summary>
        public const int ComponentLibraryNameIndexMaxChars = 450_000;

        /// <summary>
        /// 设为 "1" 时仍将技能摘要并入首条 system；默认拆成紧随其后的第二条 system，利于前缀缓存不因技能摘要变动整段失效。
        /// </summary>
        public static bool MergeSkillsIntoSameSystemPromptAsLibraryIndex =>
            string.Equals(Environment.GetEnvironmentVariable("MAGPIE_MERGE_TIER0_SYSTEM"), "1", StringComparison.OrdinalIgnoreCase);

        /// <summary>启用 workflow router 记录；默认开启，但 P0 只记录 route，不改变工具暴露与执行行为。</summary>
        public static bool UseWorkflowRouter =>
            !string.Equals(Environment.GetEnvironmentVariable("MAGPIE_USE_WORKFLOW_ROUTER"), "0", StringComparison.OrdinalIgnoreCase);

        /// <summary>启用 ContextLedger prompt 注入；默认关闭，避免 P0 改变模型行为。</summary>
        public static bool UseContextLedgerPrompt =>
            string.Equals(Environment.GetEnvironmentVariable("MAGPIE_USE_CONTEXT_LEDGER_PROMPT"), "1", StringComparison.OrdinalIgnoreCase);

        /// <summary>启用结构化 skill 索引；默认开启，可用 MAGPIE_USE_SKILL_CATALOG_INDEX=0 回退旧扫描逻辑。</summary>
        public static bool UseSkillCatalogIndex =>
            !string.Equals(Environment.GetEnvironmentVariable("MAGPIE_USE_SKILL_CATALOG_INDEX"), "0", StringComparison.OrdinalIgnoreCase);

        /// <summary>启用 workflow-aware 工具面过滤；默认关闭，避免 P2 初期改变模型行为。</summary>
        public static bool UseToolSurfacePolicy =>
            string.Equals(Environment.GetEnvironmentVariable("MAGPIE_USE_TOOL_SURFACE_POLICY"), "1", StringComparison.OrdinalIgnoreCase);

        /// <summary>ContextLedger 注入 system prompt 的最大字符数。</summary>
        public const int ContextLedgerPromptMaxChars = 4000;

        public static bool UseContextPackPrompt =>
            !string.Equals(Environment.GetEnvironmentVariable("MAGPIE_USE_CONTEXT_PACK_PROMPT"), "0", StringComparison.OrdinalIgnoreCase);

        public static bool UseReferenceCatalogIndex =>
            !string.Equals(Environment.GetEnvironmentVariable("MAGPIE_USE_REFERENCE_CATALOG_INDEX"), "0", StringComparison.OrdinalIgnoreCase);

        public const int ContextPackPromptMaxChars = 10000;

        // Web research uses its own budgets so LLM/image transport timeouts can
        // stay generous while tool calls remain responsive inside an agent turn.
        public const int WebResearchSearchTimeoutSeconds = 15;
        public const int WebResearchFetchTimeoutSeconds = 15;
        public const int WebResearchApiPipelineTimeoutSeconds = 35;
        public const int WebResearchRequestTimeoutSeconds = 8;
        public const int ApiDocPipelineExpandedQueryLimit = 5;
        public const int ApiDocPipelineFallbackQueryLimit = 2;
        public const int ApiDocPipelineIndexPageFetchLimit = 30;
        public const int ApiDocPipelineTypePageFetchLimit = 4;
    }
}
