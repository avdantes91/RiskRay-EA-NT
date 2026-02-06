using System;
using System.Collections.Generic;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;

namespace NinjaTrader.NinjaScript.Strategies
{
    public sealed class RiskRayChartLines
    {
        public enum LineKind
        {
            Entry,
            Stop,
            Target
        }

        private readonly Strategy owner;
        private readonly RiskRayTagNames tags;
        private readonly Func<double> tickSizeProvider;
        private readonly Func<double, double> roundToTickProvider;
        private readonly Func<MarketPosition> workingDirectionProvider;
        private readonly Func<int> labelBarsAgoProvider;
        private readonly Func<double> labelOffsetTicksProvider;
        private readonly Func<bool> getSuppressLineEvents;
        private readonly Action<bool> setSuppressLineEvents;
        private readonly Func<string> normalizedPrefixProvider;
        private readonly Action<string> cleanupLogAction;

        private HorizontalLine entryLine;
        private HorizontalLine stopLine;
        private HorizontalLine targetLine;
        private readonly List<DrawingTool> trackedDrawObjects = new List<DrawingTool>();

        public RiskRayChartLines(
            Strategy owner,
            RiskRayTagNames tags,
            Func<double> tickSizeProvider,
            Func<double, double> roundToTickProvider,
            Func<MarketPosition> workingDirectionProvider,
            Func<int> labelBarsAgoProvider,
            Func<double> labelOffsetTicksProvider,
            Func<bool> getSuppressLineEvents,
            Action<bool> setSuppressLineEvents,
            Func<string> normalizedPrefixProvider,
            Action<string> cleanupLogAction)
        {
            this.owner = owner;
            this.tags = tags;
            this.tickSizeProvider = tickSizeProvider;
            this.roundToTickProvider = roundToTickProvider;
            this.workingDirectionProvider = workingDirectionProvider;
            this.labelBarsAgoProvider = labelBarsAgoProvider;
            this.labelOffsetTicksProvider = labelOffsetTicksProvider;
            this.getSuppressLineEvents = getSuppressLineEvents;
            this.setSuppressLineEvents = setSuppressLineEvents;
            this.normalizedPrefixProvider = normalizedPrefixProvider;
            this.cleanupLogAction = cleanupLogAction;
        }

        public void WithSuppressedEvents(Action action)
        {
            if (action == null)
                return;

            bool previous = getSuppressLineEvents != null && getSuppressLineEvents();
            setSuppressLineEvents?.Invoke(true);
            try
            {
                action();
            }
            finally
            {
                setSuppressLineEvents?.Invoke(previous);
            }
        }

        public void UpsertLine(LineKind kind, double price, string labelText)
        {
            WithSuppressedEvents(() =>
            {
                HorizontalLine line = GetLineRef(kind);
                Brush lineBrush = LineBrush(kind);
                if (line == null)
                {
                    line = Draw.HorizontalLine(owner, LineTag(kind), price, lineBrush);
                    if (line != null)
                    {
                        line.Stroke = new Stroke(lineBrush, DashStyleHelper.Solid, 2);
                        line.IsLocked = kind == LineKind.Entry;
                        line.IsAutoScale = false;
                        SetLineRef(kind, line);
                        TrackDrawObject(line);
                    }
                }
                else
                {
                    SetLineAnchors(line, price);
                }

                UpsertLabel(kind, price, labelText, lineBrush);
            });
        }

        public void UpdateLineLabel(LineKind kind, string text)
        {
            WithSuppressedEvents(() =>
            {
                HorizontalLine line = GetLineRef(kind);
                if (line == null || line.StartAnchor == null)
                    return;

                double linePrice = line.StartAnchor.Price;
                UpsertLabel(kind, linePrice, text, LineBrush(kind));
            });
        }

        public void SetLinePrice(LineKind kind, double price)
        {
            WithSuppressedEvents(() =>
            {
                HorizontalLine line = GetLineRef(kind);
                if (line != null)
                    SetLineAnchors(line, price);
            });
        }

        public double? GetLinePrice(LineKind kind)
        {
            HorizontalLine line = GetLineRef(kind);
            if (line == null || line.StartAnchor == null)
                return null;
            return line.StartAnchor.Price;
        }

        public bool HasActiveLines()
        {
            return entryLine != null || stopLine != null || targetLine != null;
        }

        public void RemoveAllDrawObjects()
        {
            WithSuppressedEvents(() =>
            {
                HashSet<string> drawTags = new HashSet<string>();
                drawTags.Add(tags.EntryLineTag);
                drawTags.Add(tags.StopLineTag);
                drawTags.Add(tags.TargetLineTag);
                drawTags.Add(tags.EntryLabelTag);
                drawTags.Add(tags.StopLabelTag);
                drawTags.Add(tags.TargetLabelTag);
                drawTags.Add(tags.HudNotifyTag);
                foreach (DrawingTool obj in trackedDrawObjects)
                {
                    if (obj == null || obj.Tag == null)
                        continue;

                    string tagStr = obj.Tag.ToString();
                    if (string.IsNullOrEmpty(tagStr))
                        continue;
                    drawTags.Add(tagStr);
                }

                foreach (string tag in drawTags)
                    TryRemoveDrawObject(tag);

                trackedDrawObjects.Clear();
                entryLine = null;
                stopLine = null;
                targetLine = null;
            });
        }

        public void ShowHudNotification(string text)
        {
            WithSuppressedEvents(() =>
            {
                TryRemoveDrawObject(tags.HudNotifyTag);
                DrawingTool note = Draw.TextFixed(
                    owner,
                    tags.HudNotifyTag,
                    text,
                    TextPosition.TopRight,
                    Brushes.White,
                    new SimpleFont("Segoe UI", 13),
                    Brushes.Black,
                    Brushes.White,
                    4,
                    DashStyleHelper.Solid,
                    1,
                    false,
                    null);

                TrackDrawObject(note);
            });
        }

        private void UpsertLabel(LineKind kind, double price, string text, Brush brush)
        {
            double offsetTicks = labelOffsetTicksProvider != null ? labelOffsetTicksProvider() : 0;
            offsetTicks = Math.Max(0, offsetTicks);
            double tick = tickSizeProvider != null ? tickSizeProvider() : 0;
            MarketPosition direction = workingDirectionProvider != null ? workingDirectionProvider() : MarketPosition.Flat;
            bool isStop = kind == LineKind.Stop;

            double offsetPrice = price;
            if (direction == MarketPosition.Long)
                offsetPrice = isStop ? price - offsetTicks * tick : price + offsetTicks * tick;
            else if (direction == MarketPosition.Short)
                offsetPrice = isStop ? price + offsetTicks * tick : price - offsetTicks * tick;

            offsetPrice = roundToTickProvider != null ? roundToTickProvider(offsetPrice) : offsetPrice;
            TryRemoveDrawObject(LabelTag(kind));
            int barsAgo = labelBarsAgoProvider != null ? labelBarsAgoProvider() : 0;
            DrawingTool label = Draw.Text(owner, LabelTag(kind), text, barsAgo, offsetPrice, brush);
            if (label != null)
            {
                label.IsLocked = true;
                label.IsAutoScale = false;
            }
            TrackDrawObject(label);
        }

        private HorizontalLine GetLineRef(LineKind kind)
        {
            switch (kind)
            {
                case LineKind.Entry:
                    return entryLine;
                case LineKind.Stop:
                    return stopLine;
                default:
                    return targetLine;
            }
        }

        private void SetLineRef(LineKind kind, HorizontalLine line)
        {
            switch (kind)
            {
                case LineKind.Entry:
                    entryLine = line;
                    break;
                case LineKind.Stop:
                    stopLine = line;
                    break;
                default:
                    targetLine = line;
                    break;
            }
        }

        private string LineTag(LineKind kind)
        {
            switch (kind)
            {
                case LineKind.Entry:
                    return tags.EntryLineTag;
                case LineKind.Stop:
                    return tags.StopLineTag;
                default:
                    return tags.TargetLineTag;
            }
        }

        private string LabelTag(LineKind kind)
        {
            switch (kind)
            {
                case LineKind.Entry:
                    return tags.EntryLabelTag;
                case LineKind.Stop:
                    return tags.StopLabelTag;
                default:
                    return tags.TargetLabelTag;
            }
        }

        private Brush LineBrush(LineKind kind)
        {
            switch (kind)
            {
                case LineKind.Entry:
                    return Brushes.Black;
                case LineKind.Stop:
                    return Brushes.Red;
                default:
                    return Brushes.ForestGreen;
            }
        }

        private void SetLineAnchors(HorizontalLine line, double price)
        {
            if (line == null)
                return;
            if (line.StartAnchor != null)
                line.StartAnchor.Price = price;
            if (line.EndAnchor != null)
                line.EndAnchor.Price = price;
        }

        private void TrackDrawObject(DrawingTool obj)
        {
            if (obj == null)
                return;

            trackedDrawObjects.RemoveAll(o => o != null && Equals(o.Tag, obj.Tag));
            trackedDrawObjects.Add(obj);
        }

        private void TryRemoveDrawObject(string tag)
        {
            if (string.IsNullOrEmpty(tag))
                return;

            try
            {
                owner.RemoveDrawObject(tag);
            }
            catch (Exception ex)
            {
                cleanupLogAction?.Invoke(ex.Message);
            }
        }
    }
}
