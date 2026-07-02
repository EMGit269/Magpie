using System;

namespace Magpie.Agent
{
    public enum ContextPressureLevel
    {
        Low,
        Medium,
        High,
        Critical
    }

    public sealed class ContextBudget
    {
        public const int DefaultHeadroomTokens = 1024;
        public const int DefaultMinInputBudgetTokens = 1024;
        public const double MediumPressurePercent = 40.0;
        public const double HighPressurePercent = 75.0;
        public const double CriticalPressurePercent = 90.0;

        public int WindowTokens { get; private set; }
        public int InputTokens { get; private set; }
        public int OutputCapTokens { get; private set; }
        public int HeadroomTokens { get; private set; }
        public int AvailableInputTokens { get; private set; }
        public int CompactionTriggerTokens { get; private set; }
        public ContextPressureLevel Pressure { get; private set; }

        private ContextBudget() { }

        public static ContextBudget Create(
            int windowTokens,
            int inputTokens,
            int configuredOutputCapTokens,
            double compactionTriggerRatio,
            int headroomTokens = DefaultHeadroomTokens)
        {
            windowTokens = Math.Max(0, windowTokens);
            inputTokens = Math.Max(0, inputTokens);
            configuredOutputCapTokens = Math.Max(0, configuredOutputCapTokens);
            headroomTokens = Math.Max(0, headroomTokens);

            int outputCap = ClampOutputCap(windowTokens, configuredOutputCapTokens, headroomTokens);
            int reserved = SaturatingAdd(outputCap, headroomTokens);
            int inputCeiling = Math.Max(0, windowTokens - reserved);
            int available = Math.Max(0, inputCeiling - inputTokens);
            int trigger = PercentOf(windowTokens, ClampRatio(compactionTriggerRatio));

            return new ContextBudget
            {
                WindowTokens = windowTokens,
                InputTokens = inputTokens,
                OutputCapTokens = outputCap,
                HeadroomTokens = headroomTokens,
                AvailableInputTokens = available,
                CompactionTriggerTokens = trigger,
                Pressure = PressureFromUsagePercent(UsagePercent(windowTokens, inputTokens))
            };
        }

        public double UsagePercent()
        {
            return UsagePercent(WindowTokens, InputTokens);
        }

        public bool ShouldCompact()
        {
            return WindowTokens > 0 && InputTokens >= CompactionTriggerTokens;
        }

        public bool FitsAdditional(int additionalInputTokens)
        {
            return Math.Max(0, additionalInputTokens) <= AvailableInputTokens;
        }

        public string ToLogLine()
        {
            return "window=" + WindowTokens
                + ", input=" + InputTokens
                + ", output_cap=" + OutputCapTokens
                + ", available=" + AvailableInputTokens
                + ", trigger=" + CompactionTriggerTokens
                + ", pressure=" + Pressure.ToString().ToLowerInvariant()
                + ", usage=" + UsagePercent().ToString("0.0") + "%";
        }

        private static int ClampOutputCap(int windowTokens, int configuredOutputCapTokens, int headroomTokens)
        {
            int protectedInputAndHeadroom = SaturatingAdd(DefaultMinInputBudgetTokens, headroomTokens);
            int maxOutput = Math.Max(0, windowTokens - protectedInputAndHeadroom);
            return Math.Min(configuredOutputCapTokens, maxOutput);
        }

        private static int SaturatingAdd(int a, int b)
        {
            long sum = (long)Math.Max(0, a) + Math.Max(0, b);
            return sum > int.MaxValue ? int.MaxValue : (int)sum;
        }

        private static double ClampRatio(double ratio)
        {
            if (double.IsNaN(ratio) || double.IsInfinity(ratio)) return 0.75;
            return Math.Max(0.0, Math.Min(1.0, ratio));
        }

        private static int PercentOf(int value, double ratio)
        {
            if (value <= 0) return 0;
            double raw = value * ratio;
            if (raw >= int.MaxValue) return int.MaxValue;
            return (int)Math.Round(raw);
        }

        private static double UsagePercent(int windowTokens, int inputTokens)
        {
            if (windowTokens <= 0) return 0.0;
            return Math.Max(0.0, Math.Min(100.0, (inputTokens * 100.0) / windowTokens));
        }

        private static ContextPressureLevel PressureFromUsagePercent(double percent)
        {
            if (percent >= CriticalPressurePercent) return ContextPressureLevel.Critical;
            if (percent >= HighPressurePercent) return ContextPressureLevel.High;
            if (percent >= MediumPressurePercent) return ContextPressureLevel.Medium;
            return ContextPressureLevel.Low;
        }
    }
}
