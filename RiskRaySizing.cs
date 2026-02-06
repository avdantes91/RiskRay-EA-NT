using System;
using NinjaTrader.Cbi;

namespace NinjaTrader.NinjaScript.Strategies
{
    public sealed class RiskRaySizing
    {
        private readonly Func<Instrument> instrumentProvider;
        private readonly Func<double> cachedTickGetter;
        private readonly Action<double> cachedTickSetter;

        public RiskRaySizing(Func<Instrument> instrumentProvider, Func<double> cachedTickGetter, Action<double> cachedTickSetter)
        {
            this.instrumentProvider = instrumentProvider;
            this.cachedTickGetter = cachedTickGetter;
            this.cachedTickSetter = cachedTickSetter;
        }

        public double TickSize()
        {
            Instrument instrument = instrumentProvider();
            if (instrument != null && instrument.MasterInstrument != null)
            {
                double tick = instrument.MasterInstrument.TickSize;
                if (tick > 0)
                    cachedTickSetter(tick);
                return tick;
            }

            double cachedTick = cachedTickGetter();
            return cachedTick > 0 ? cachedTick : 0.01;
        }

        public double TickValue()
        {
            Instrument instrument = instrumentProvider();
            return TickSize() * (instrument?.MasterInstrument.PointValue ?? 1);
        }

        public double RoundToTick(double price)
        {
            Instrument instrument = instrumentProvider();
            if (instrument != null && instrument.MasterInstrument != null)
                return instrument.MasterInstrument.RoundToTickSize(price);

            double cachedTick = cachedTickGetter();
            if (cachedTick > 0)
                return Math.Round(price / cachedTick) * cachedTick;

            return price;
        }

        public int CalculateQuantity(double entryPrice, double stopPrice, double fixedRiskUsd, bool commissionOn, double commissionPerContractRoundTurn, int maxContracts)
        {
            double tick = TickSize();
            double distanceTicks = Math.Abs(entryPrice - stopPrice) / tick;
            if (distanceTicks <= 0)
                return 0;

            double perContractRisk = distanceTicks * TickValue();
            if (commissionOn)
                perContractRisk += commissionPerContractRoundTurn;

            double rawQty = perContractRisk > 0 ? fixedRiskUsd / perContractRisk : 0;
            int qty = (int)Math.Floor(rawQty + 0.5); // half-up
            qty = Math.Min(qty, maxContracts);
            return qty;
        }
    }
}
