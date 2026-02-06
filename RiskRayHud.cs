using System;
using NinjaTrader.Cbi;

namespace NinjaTrader.NinjaScript.Strategies
{
    public sealed class RiskRayHud
    {
        public struct Snapshot
        {
            public Snapshot(
                double entryPrice,
                double stopPrice,
                double targetPrice,
                double fixedRiskUSD,
                bool commissionOn,
                double commissionPerContractRoundTurn,
                int maxContracts,
                double maxRiskWarningUSD)
            {
                EntryPrice = entryPrice;
                StopPrice = stopPrice;
                TargetPrice = targetPrice;
                FixedRiskUSD = fixedRiskUSD;
                CommissionOn = commissionOn;
                CommissionPerContractRoundTurn = commissionPerContractRoundTurn;
                MaxContracts = maxContracts;
                MaxRiskWarningUSD = maxRiskWarningUSD;
            }

            public double EntryPrice { get; }
            public double StopPrice { get; }
            public double TargetPrice { get; }
            public double FixedRiskUSD { get; }
            public bool CommissionOn { get; }
            public double CommissionPerContractRoundTurn { get; }
            public int MaxContracts { get; }
            public double MaxRiskWarningUSD { get; }
        }

        private readonly RiskRaySizing sizing;
        private readonly Func<Instrument> instrumentProvider;
        private readonly Func<double> entryReferenceForRiskProvider;
        private readonly Func<int> displayQuantityProvider;
        private readonly Func<string> currencySymbolProvider;

        private string cachedEntryLabelText;
        private string cachedStopLabelText;
        private string cachedTargetLabelText;
        private string cachedQtyLabelText;
        private string cachedRrLabelText;

        public RiskRayHud(
            RiskRaySizing sizing,
            Func<Instrument> instrumentProvider,
            Func<double> entryReferenceForRiskProvider,
            Func<int> displayQuantityProvider,
            Func<string> currencySymbolProvider)
        {
            this.sizing = sizing;
            this.instrumentProvider = instrumentProvider;
            this.entryReferenceForRiskProvider = entryReferenceForRiskProvider;
            this.displayQuantityProvider = displayQuantityProvider;
            this.currencySymbolProvider = currencySymbolProvider;
        }

        public void ResetCaches()
        {
            cachedEntryLabelText = null;
            cachedStopLabelText = null;
            cachedTargetLabelText = null;
            cachedQtyLabelText = null;
            cachedRrLabelText = null;
        }

        public string GetEntryLabelSafe(Snapshot s)
        {
            string qtyLabel = GetQtyLabel(s);
            string rrText = GetRiskRewardText(s);
            if (string.IsNullOrEmpty(qtyLabel) || qtyLabel == "0 contracts")
                qtyLabel = cachedQtyLabelText ?? "CALC…";

            if (string.IsNullOrEmpty(rrText) || rrText == "R0.00" || rrText == "R—")
                rrText = cachedRrLabelText ?? "R?";

            string combined = $"{qtyLabel} | {rrText}";
            cachedEntryLabelText = combined;
            return combined;
        }

        public string GetQtyLabel(Snapshot s)
        {
            double tick;
            double tickValue;
            double entryRefUnused;
            string reasonUnused;
            if (!TryComputeSizing(s, out tick, out tickValue, out entryRefUnused, out reasonUnused))
                return UseCachedOrPlaceholder(ref cachedQtyLabelText);

            double stopTicks = Math.Abs(s.EntryPrice - s.StopPrice) / tick;
            double perContractRisk = (stopTicks * tickValue) + (s.CommissionOn ? s.CommissionPerContractRoundTurn : 0);
            double rawQty = perContractRisk > 0 ? s.FixedRiskUSD / perContractRisk : 0;
            int roundedQty = (int)Math.Floor(rawQty + 0.5);
            int cappedQty = Math.Min(roundedQty, s.MaxContracts);

            string label = cappedQty < 1
                ? $"{rawQty:F2} (min 1)"
                : $"{cappedQty} contracts";

            cachedQtyLabelText = label;
            return label;
        }

        public string GetStopLabel(Snapshot s)
        {
            double tick;
            double tickValue;
            double entryRef;
            string reasonUnused;
            if (!TryComputeSizing(s, out tick, out tickValue, out entryRef, out reasonUnused))
                return UseCachedOrPlaceholder(ref cachedStopLabelText);

            double stopDistanceTicks = Math.Abs(entryRef - s.StopPrice) / tick;
            if (double.IsNaN(stopDistanceTicks) || double.IsInfinity(stopDistanceTicks))
                return UseCachedOrPlaceholder(ref cachedStopLabelText);

            if (stopDistanceTicks <= double.Epsilon)
            {
                cachedStopLabelText = "SL: BE";
                return cachedStopLabelText;
            }

            double perContractRisk = (stopDistanceTicks * tickValue) + (s.CommissionOn ? s.CommissionPerContractRoundTurn : 0);
            double riskQty = Math.Max(1, displayQuantityProvider != null ? displayQuantityProvider() : 0);
            double totalRisk = perContractRisk * riskQty;
            string distanceText = FormatPointsAndTicks(stopDistanceTicks, tick);
            string label = $"SL: -{currencySymbolProvider()}{totalRisk:F2} ({distanceText})";

            const double legacyWarn = 200d;
            double effectiveWarn = s.MaxRiskWarningUSD > 0 ? s.MaxRiskWarningUSD : s.FixedRiskUSD;
            if (s.MaxRiskWarningUSD > 0
                && Math.Abs(s.MaxRiskWarningUSD - legacyWarn) < 0.0001
                && Math.Abs(s.FixedRiskUSD - legacyWarn) > 0.0001)
            {
                effectiveWarn = s.FixedRiskUSD;
            }
            if (totalRisk > effectiveWarn)
                label = $"!! {label} !!";

            cachedStopLabelText = label;
            return label;
        }

        public string GetTargetLabel(Snapshot s)
        {
            double tick;
            double tickValue;
            double entryRef;
            string reasonUnused;
            if (!TryComputeSizing(s, out tick, out tickValue, out entryRef, out reasonUnused))
                return UseCachedOrPlaceholder(ref cachedTargetLabelText);

            double rewardTicks = Math.Abs(s.TargetPrice - entryRef) / tick;
            if (double.IsNaN(rewardTicks) || double.IsInfinity(rewardTicks))
                return UseCachedOrPlaceholder(ref cachedTargetLabelText);

            double rewardQty = Math.Max(1, displayQuantityProvider != null ? displayQuantityProvider() : 0);
            double reward = rewardTicks * tickValue * rewardQty;
            string ptsTicks = FormatPointsAndTicks(rewardTicks, tick);
            string label = $"TP: +{currencySymbolProvider()}{reward:F2} ({ptsTicks})";
            cachedTargetLabelText = label;
            return label;
        }

        public string GetRiskRewardText(Snapshot s)
        {
            double tick;
            double tickValueUnused;
            double entryRef;
            string reasonUnused;
            if (!TryComputeSizing(s, out tick, out tickValueUnused, out entryRef, out reasonUnused))
                return UseCachedOrPlaceholder(ref cachedRrLabelText);

            double stopTicks = Math.Abs(entryRef - s.StopPrice) / tick;
            double rewardTicks = Math.Abs(s.TargetPrice - entryRef) / tick;
            if (stopTicks <= double.Epsilon || double.IsNaN(stopTicks) || double.IsInfinity(stopTicks))
                return UseCachedOrPlaceholder(ref cachedRrLabelText);

            double rr = rewardTicks / stopTicks;
            string rrText = Math.Abs(rr - 1.0) < 0.005 ? "R1" : $"R{rr:F2}";
            cachedRrLabelText = rrText;
            return rrText;
        }

        public bool TryComputeSizing(Snapshot s, out double tick, out double tickValue, out double entryRef, out string reason)
        {
            tick = sizing.TickSize();
            tickValue = sizing.TickValue();
            entryRef = double.NaN;

            Instrument instrument = instrumentProvider != null ? instrumentProvider() : null;
            if (instrument == null || instrument.MasterInstrument == null)
            {
                reason = "instrument missing";
                return false;
            }
            if (tick <= 0 || double.IsNaN(tick) || double.IsInfinity(tick))
            {
                reason = "TickSize<=0";
                return false;
            }
            if (tickValue <= 0 || double.IsNaN(tickValue) || double.IsInfinity(tickValue))
            {
                reason = "TickValue<=0";
                return false;
            }

            entryRef = entryReferenceForRiskProvider != null ? entryReferenceForRiskProvider() : double.NaN;
            if (double.IsNaN(entryRef) || double.IsInfinity(entryRef) || entryRef <= 0)
            {
                reason = "entryRef invalid";
                return false;
            }

            reason = null;
            return true;
        }

        private string UseCachedOrPlaceholder(ref string cache)
        {
            if (!string.IsNullOrEmpty(cache))
                return cache;
            return "CALC…";
        }

        private string FormatPointsAndTicks(double distanceTicks, double tick)
        {
            if (tick <= 0 || double.IsNaN(distanceTicks) || double.IsInfinity(distanceTicks))
                return "CALC…";

            double points = distanceTicks * tick;
            double wholePoints = Math.Floor(points + 1e-9);
            int ticksPerPoint = Math.Max(1, (int)Math.Round(1.0 / tick));
            int remainingTicks = (int)Math.Round((points - wholePoints) / tick);
            remainingTicks = Math.Max(0, Math.Min(remainingTicks, ticksPerPoint - 1));
            if (remainingTicks >= ticksPerPoint)
            {
                wholePoints += 1;
                remainingTicks = 0;
            }
            return $"{wholePoints}.{remainingTicks}";
        }
    }
}
