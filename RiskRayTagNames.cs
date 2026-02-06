using System;

namespace NinjaTrader.NinjaScript.Strategies
{
public sealed class RiskRayTagNames
{
    private readonly string orderPrefix;
    private readonly string drawPrefix;

    public RiskRayTagNames(string orderTagPrefix, string drawInstanceScope = null)
    {
        string normalized = string.IsNullOrWhiteSpace(orderTagPrefix) ? "RR_" : orderTagPrefix.Trim();
        orderPrefix = string.IsNullOrWhiteSpace(normalized) ? "RR_" : normalized;
        drawPrefix = string.IsNullOrWhiteSpace(drawInstanceScope) ? orderPrefix : $"{orderPrefix}{drawInstanceScope}_";
    }

    private string OrderTag(string suffix)
    {
        return $"{orderPrefix}{suffix}";
    }

    private string DrawTag(string suffix)
    {
        return $"{drawPrefix}{suffix}";
    }

    public string EntryLineTag => DrawTag("ENTRY_LINE");
    public string StopLineTag => DrawTag("STOP_LINE");
    public string TargetLineTag => DrawTag("TARGET_LINE");

    public string EntryLabelTag => DrawTag("ENTRY_LABEL");
    public string StopLabelTag => DrawTag("STOP_LABEL");
    public string TargetLabelTag => DrawTag("TARGET_LABEL");

    public string EntrySignalLong => OrderTag("ENTRY_LONG");
    public string EntrySignalShort => OrderTag("ENTRY_SHORT");
    public string StopSignal => OrderTag("SL");
    public string TargetSignal => OrderTag("TP");
    public string CloseSignal => OrderTag("CLOSE");
    public string BeSignal => OrderTag("BE");
    public string TrailSignal => OrderTag("TRAIL");
    public string HudNotifyTag => DrawTag("HUD_NOTIFY");
}
}
