using System;

namespace NinjaTrader.NinjaScript.Strategies
{
    public sealed class RiskRayTagNames
    {
        private readonly string prefix;

        public RiskRayTagNames(string orderTagPrefix)
        {
            string normalized = string.IsNullOrWhiteSpace(orderTagPrefix) ? "RR_" : orderTagPrefix.Trim();
            prefix = string.IsNullOrWhiteSpace(normalized) ? "RR_" : normalized;
        }

        public string Tag(string suffix)
        {
            return $"{prefix}{suffix}";
        }

        public string EntryLineTag => Tag("ENTRY_LINE");
        public string StopLineTag => Tag("STOP_LINE");
        public string TargetLineTag => Tag("TARGET_LINE");

        public string EntryLabelTag => Tag("ENTRY_LABEL");
        public string StopLabelTag => Tag("STOP_LABEL");
        public string TargetLabelTag => Tag("TARGET_LABEL");

        public string EntrySignalLong => Tag("ENTRY_LONG");
        public string EntrySignalShort => Tag("ENTRY_SHORT");
        public string StopSignal => Tag("SL");
        public string TargetSignal => Tag("TP");
        public string CloseSignal => Tag("CLOSE");
        public string BeSignal => Tag("BE");
        public string TrailSignal => Tag("TRAIL");
        public string HudNotifyTag => Tag("HUD_NOTIFY");
    }
}
