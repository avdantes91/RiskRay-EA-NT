// RiskRay strategy
// README / Assumptions (max 8 bullets)
// - Semi-manual trade planner/executor with BUY/SELL arming flow and bracketed exits.
// - Chart WPF panel (BUY/SELL/CLOSE/BE) with blinking armed state; unmanaged OCO orders.
// - Entry line follows bid/ask while armed; SL/TP draggable with ChangeOrder updates.
// - Market-relative validity clamps lines to safe ticks to avoid instant triggers.
// - Live sizing uses instrument tick/point metadata, half-up rounding, and MaxContracts cap.
// - Break-even button moves stop to avg fill + offset ticks with market-relative clamp.
// - Full cleanup on termination: timers, UI, handlers, draw objects.
// - Single concurrent position per instrument; new arming blocked while non-flat/working.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Threading;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;

namespace NinjaTrader.NinjaScript.Strategies
{
    public class RiskRay : Strategy
    {
        private enum ArmDirection
        {
            None,
            Long,
            Short
        }

        public enum CommissionModeOption
        {
            Off,
            On
        }

        public enum LogLevelOption
        {
            Off,
            Info,
            Debug
        }

        private const string EntryTag = "RiskRayEntry";
        private const string StopTag = "RiskRayStop";
        private const string TargetTag = "RiskRayTarget";
        private const string EntryLabelTag = "RiskRayEntryLbl";
        private const string StopLabelTag = "RiskRayStopLbl";
        private const string TargetLabelTag = "RiskRayTargetLbl";
        private const string EntrySignal = "RR_ENTRY";
        private const string StopSignal = "RR_STOP";
        private const string TargetSignal = "RR_TARGET";

        private Grid uiRoot;
        private Button buyButton;
        private Button sellButton;
        private Button closeButton;
        private Button beButton;
        private Button trailButton;
        private DispatcherTimer blinkTimer;
        private bool blinkOn;

        private ArmDirection armedDirection = ArmDirection.None;
        private bool isArmed;
        private bool hasPendingEntry;

        private HorizontalLine entryLine;
        private HorizontalLine stopLine;
        private HorizontalLine targetLine;

        private double entryPrice;
        private double stopPrice;
        private double targetPrice;
        private double avgEntryPrice;

        private Order entryOrder;
        private Order stopOrder;
        private Order targetOrder;
        private string currentOco;

        private DateTime lastClampLogTime = DateTime.MinValue;
        private DateTime lastDebugLogTime = DateTime.MinValue;
        private DateTime lastQtyBlockLogTime = DateTime.MinValue;

        private bool suppressLineEvents;
        private bool uiLoaded;
        private Grid chartGrid;

        private readonly List<DrawingTool> trackedDrawObjects = new List<DrawingTool>();
        private bool entryLineDirty;
        private bool stopLineDirty;
        private bool targetLineDirty;
        private string lastEntryLabel;
        private string lastStopLabel;
        private string lastTargetLabel;
        private bool fatalError;
        private string fatalErrorMessage;
        private bool isDraggingStop;
        private bool isDraggingTarget;
        private DateTime lastDragMoveLogStop = DateTime.MinValue;
        private DateTime lastDragMoveLogTarget = DateTime.MinValue;
        private bool chartEventsAttached;
        private string cachedEntryLabelText;
        private string cachedStopLabelText;
        private string cachedTargetLabelText;
        private string cachedQtyLabelText;
        private string cachedRrLabelText;
        private DateTime lastLabelSkipLogTime = DateTime.MinValue;
        private bool isBeDialogOpen;
        private DateTime lastBeDialogTime = DateTime.MinValue;

        #region Properties

        [Range(1, double.MaxValue), NinjaScriptProperty]
        [Display(Name = "FixedRiskUSD", Order = 1, GroupName = "Parameters")]
        public double FixedRiskUSD { get; set; }

        [Range(1, int.MaxValue), NinjaScriptProperty]
        [Display(Name = "DefaultStopTicks", Order = 2, GroupName = "Parameters")]
        public int DefaultStopTicks { get; set; }

        [Range(1, int.MaxValue), NinjaScriptProperty]
        [Display(Name = "DefaultTargetTicks", Order = 3, GroupName = "Parameters")]
        public int DefaultTargetTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "UseBidAskForEntry", Order = 4, GroupName = "Parameters")]
        public bool UseBidAskForEntry { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "BreakEvenOffsetTicks", Order = 5, GroupName = "Parameters")]
        public int BreakEvenOffsetTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "CommissionMode", Order = 6, GroupName = "Parameters")]
        public CommissionModeOption CommissionMode { get; set; }

        [Range(0, double.MaxValue), NinjaScriptProperty]
        [Display(Name = "CommissionPerContractRoundTurn", Order = 7, GroupName = "Parameters")]
        public double CommissionPerContractRoundTurn { get; set; }

        [Range(1, int.MaxValue), NinjaScriptProperty]
        [Display(Name = "MaxContracts", Order = 8, GroupName = "Parameters")]
        public int MaxContracts { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "LogLevel", Order = 9, GroupName = "Parameters")]
        public LogLevelOption LogLevelSetting { get; set; }

        [Range(0, double.MaxValue), NinjaScriptProperty]
        [Display(Name = "MaxRiskWarningUSD", Order = 10, GroupName = "Parameters")]
        public double MaxRiskWarningUSD { get; set; }

        [Range(0, int.MaxValue), NinjaScriptProperty]
        [Display(Name = "LabelOffsetTicks", Order = 11, GroupName = "Parameters")]
        public int LabelOffsetTicks { get; set; }

        [Range(0, 100), NinjaScriptProperty]
        [Display(Name = "LabelOffsetPixels", Order = 12, GroupName = "Labels")]
        public int LabelOffsetPixels { get; set; }

        [Range(0, 200), NinjaScriptProperty]
        [Display(Name = "LabelBarsRightOffset", Order = 13, GroupName = "Labels")]
        public int LabelBarsRightOffset { get; set; }

        [Range(-50, 50), NinjaScriptProperty]
        [Display(Name = "LabelHorizontalShift", Order = 14, GroupName = "Labels")]
        public int LabelHorizontalShift { get; set; }

        [Range(0, int.MaxValue), NinjaScriptProperty]
        [Display(Name = "BE Plus Ticks", Order = 15, GroupName = "RiskRay")]
        public int BreakEvenPlusTicks { get; set; }

        [Range(1, int.MaxValue), NinjaScriptProperty]
        [Display(Name = "TrailOffsetTicks", Order = 16, GroupName = "Trade Management")]
        public int TrailOffsetTicks { get; set; }

        #endregion

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "RiskRay";
                Description = "Semi-manual position sizer with arming UI, bracket management, and draggable orders.";
                Calculate = Calculate.OnEachTick;
                EntriesPerDirection = 1;
                EntryHandling = EntryHandling.AllEntries;
                IsUnmanaged = true;
                IsInstantiatedOnEachOptimizationIteration = false;
                DefaultStopTicks = 20;
                DefaultTargetTicks = 40;
                FixedRiskUSD = 200;
                UseBidAskForEntry = true;
                BreakEvenOffsetTicks = 0;
                CommissionMode = CommissionModeOption.Off;
                CommissionPerContractRoundTurn = 0;
                MaxContracts = 10;
                LogLevelSetting = LogLevelOption.Info;
                BarsRequiredToTrade = 0;
                IncludeCommission = false;
                fatalError = false;
                fatalErrorMessage = null;
                MaxRiskWarningUSD = 200;
                LabelOffsetTicks = 2;
                LabelOffsetPixels = 14;
                LabelBarsRightOffset = 25;
                LabelHorizontalShift = 0;
                BreakEvenPlusTicks = 0;
                TrailOffsetTicks = 20;
            }
            else if (State == State.Configure)
            {
            }
            else if (State == State.Historical)
            {
                SafeExecute("BuildUi", BuildUi);
                SafeExecute("AttachChartEvents", AttachChartEvents);
            }
            else if (State == State.Realtime)
            {
                SafeExecute("StartBlinkTimer", StartBlinkTimer);
                SafeExecute("AttachChartEvents", AttachChartEvents);
            }
            else if (State == State.Terminated)
            {
                DetachChartEvents();
                DisposeUi();
                StopBlinkTimer();
                RemoveAllDrawObjects();
                Print($"[RiskRay] [State.Terminated] state={DescribeState()} fatal={fatalError} detail={fatalErrorMessage}");
            }
        }

        protected override void OnBarUpdate()
        {
            try
            {
                if (State != State.Realtime)
                    return;

                // Only entry line follows price while armed; stops/targets stay where user placed them.
                UpdateEntryLineFromMarket();
                ApplyLineUpdates();
                UpdateLabelsOnly();
            }
            catch (Exception ex)
            {
                LogFatal("OnBarUpdate", ex);
            }
        }

        protected override void OnMarketData(MarketDataEventArgs marketDataUpdate)
        {
            try
            {
                if (State != State.Realtime)
                    return;

                UpdateEntryLineFromMarket();
                ProcessLineDrag(stopLine, ref stopPrice, true);
                ProcessLineDrag(targetLine, ref targetPrice, false);
                ApplyLineUpdates();
                UpdateLabelsOnly();
            }
            catch (Exception ex)
            {
                LogFatal("OnMarketData", ex);
            }
        }

        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPriceParam, int quantity, int filled, double averageFillPrice, OrderState orderState, DateTime time, ErrorCode error, string nativeError)
        {
            try
            {
                if (order == null)
                    return;

                if (order.Name == EntrySignal)
                    entryOrder = order;
                else if (order.Name == StopSignal)
                    stopOrder = order;
                else if (order.Name == TargetSignal)
                    targetOrder = order;

                if (order.OrderState == OrderState.Filled && order.Name == EntrySignal)
                {
                    avgEntryPrice = order.AverageFillPrice;
                    hasPendingEntry = false;
                    armedDirection = ArmDirection.None;
                    isArmed = false;
                    blinkOn = false;
                    UpdateUiState();
                    UpdateEntryLine(avgEntryPrice, "Entry fill");
                    LogInfo($"Entry filled @ {avgEntryPrice:F2} ({order.Quantity} contracts)");
                }

                if (order.OrderState == OrderState.Filled && (order.Name == StopSignal || order.Name == TargetSignal))
                {
                    HandlePositionClosed(order.Name);
                }

                if (order.OrderState == OrderState.Cancelled)
                {
                    if (order == entryOrder)
                        entryOrder = null;
                    if (order == stopOrder)
                        stopOrder = null;
                    if (order == targetOrder)
                        targetOrder = null;
                }

                if (order.OrderState == OrderState.Rejected)
                {
                    LogInfo($"Order rejected ({order.Name}): {nativeError ?? error.ToString()}");
                    CleanupAfterError();
                }
            }
            catch (Exception ex)
            {
                LogFatal("OnOrderUpdate", ex);
            }
        }

        protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity, MarketPosition marketPosition, string orderId, DateTime time)
        {
            try
            {
                if (execution == null || execution.Order == null)
                    return;

                if (execution.Order.Name == EntrySignal)
                    avgEntryPrice = execution.Order.AverageFillPrice;

                if (execution.Order.Name == StopSignal || execution.Order.Name == TargetSignal)
                    HandlePositionClosed(execution.Order.Name);
            }
            catch (Exception ex)
            {
                LogFatal("OnExecutionUpdate", ex);
            }
        }

        protected override void OnPositionUpdate(Position position, double averagePrice, int quantity, MarketPosition marketPosition)
        {
            try
            {
                if (position == null)
                    return;

                if (marketPosition == MarketPosition.Flat)
                {
                    avgEntryPrice = 0;
                    entryOrder = null;
                    stopOrder = null;
                    targetOrder = null;
                    currentOco = null;
                    hasPendingEntry = false;
                    armedDirection = ArmDirection.None;
                    isArmed = false;
                    RemoveAllDrawObjects();
                    UpdateUiState();
                }
            }
            catch (Exception ex)
            {
                LogFatal("OnPositionUpdate", ex);
            }
        }

        #region UI

        private void BuildUi()
        {
            if (ChartControl == null || uiLoaded)
                return;

            ChartControl.Dispatcher.InvokeAsync(() =>
            {
                SafeExecute("BuildUi.Dispatcher", () =>
                {
                    if (uiLoaded)
                        return;

                    chartGrid = ChartControl.Parent as Grid;
                    if (chartGrid == null)
                        return;

                    uiRoot = new Grid
                    {
                        HorizontalAlignment = HorizontalAlignment.Left,
                        VerticalAlignment = VerticalAlignment.Top,
                        Margin = new Thickness(8),
                        Background = Brushes.Transparent
                    };

                    uiRoot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    uiRoot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    uiRoot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    uiRoot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    uiRoot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    buyButton = CreateButton("BUY", Brushes.DarkGreen, OnBuyClicked);
                    sellButton = CreateButton("SELL", Brushes.DarkRed, OnSellClicked);
                    closeButton = CreateButton("CLOSE", Brushes.DimGray, OnCloseClicked);
                    beButton = CreateButton("BE", Brushes.DarkSlateBlue, OnBeClicked);
                    trailButton = CreateButton("TRAIL", Brushes.DarkOrange, OnTrailClicked);

                    uiRoot.Children.Add(buyButton);
                    uiRoot.Children.Add(sellButton);
                    uiRoot.Children.Add(closeButton);
                    uiRoot.Children.Add(beButton);
                    uiRoot.Children.Add(trailButton);
                    Grid.SetColumn(buyButton, 0);
                    Grid.SetColumn(sellButton, 1);
                    Grid.SetColumn(closeButton, 2);
                    Grid.SetColumn(beButton, 3);
                    Grid.SetColumn(trailButton, 4);

                    chartGrid.Children.Add(uiRoot);
                    uiLoaded = true;
                    UpdateUiState();
                    AttachChartEvents();
                });
            });
        }

        private Button CreateButton(string content, Brush baseBrush, RoutedEventHandler handler)
        {
            var button = new Button
            {
                Content = content,
                Margin = new Thickness(2),
                Padding = new Thickness(10, 6, 10, 6),
                Background = baseBrush,
                Foreground = Brushes.White,
                BorderBrush = Brushes.Black,
                FontWeight = FontWeights.SemiBold,
                Cursor = System.Windows.Input.Cursors.Hand
            };
            button.Click += handler;
            return button;
        }

        private void DisposeUi()
        {
            if (!uiLoaded || ChartControl == null)
                return;

            ChartControl.Dispatcher.InvokeAsync(() =>
            {
                DetachChartEvents();
                if (uiRoot != null && chartGrid != null && chartGrid.Children.Contains(uiRoot))
                    chartGrid.Children.Remove(uiRoot);

                if (buyButton != null)
                    buyButton.Click -= OnBuyClicked;
                if (sellButton != null)
                    sellButton.Click -= OnSellClicked;
                if (closeButton != null)
                    closeButton.Click -= OnCloseClicked;
                if (beButton != null)
                    beButton.Click -= OnBeClicked;
                if (trailButton != null)
                    trailButton.Click -= OnTrailClicked;

                uiRoot = null;
                buyButton = null;
                sellButton = null;
                closeButton = null;
                beButton = null;
                trailButton = null;
                uiLoaded = false;
            });
        }

        private void UpdateUiState()
        {
            if (ChartControl == null || !uiLoaded)
                return;

            ChartControl.Dispatcher.InvokeAsync(() =>
            {
                if (buyButton != null)
                    buyButton.Opacity = (armedDirection == ArmDirection.Long && isArmed) ? (blinkOn ? 1 : 0.55) : 1;
                if (sellButton != null)
                    sellButton.Opacity = (armedDirection == ArmDirection.Short && isArmed) ? (blinkOn ? 1 : 0.55) : 1;
                if (closeButton != null)
                {
                    closeButton.IsEnabled = isArmed || Position.MarketPosition != MarketPosition.Flat || entryOrder != null;
                    closeButton.IsHitTestVisible = true;
                }
                if (trailButton != null)
                {
                    trailButton.IsEnabled = Position.MarketPosition != MarketPosition.Flat && stopOrder != null;
                    trailButton.IsHitTestVisible = true;
                }
                if (beButton != null)
                    beButton.IsEnabled = Position.MarketPosition != MarketPosition.Flat && stopOrder != null;

                UpdateArmButtonsUI();
            });
        }

        private void UpdateArmButtonsUI()
        {
            if (ChartControl == null || !uiLoaded)
                return;

            ChartControl.Dispatcher.InvokeAsync(() =>
            {
                if (buyButton != null)
                    buyButton.Content = (isArmed && armedDirection == ArmDirection.Long) ? "BUY ARMED" : "BUY";
                if (sellButton != null)
                    sellButton.Content = (isArmed && armedDirection == ArmDirection.Short) ? "SELL ARMED" : "SELL";
            });
        }

        private void StartBlinkTimer()
        {
            if (blinkTimer != null)
                return;

            blinkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            blinkTimer.Tick += (s, e) =>
            {
                SafeExecute("BlinkTimer", () =>
                {
                    if (!isArmed)
                        return;

                    blinkOn = !blinkOn;
                    UpdateUiState();
                });
            };
            blinkTimer.Start();
        }

        private void StopBlinkTimer()
        {
            if (blinkTimer == null)
                return;

            blinkTimer.Stop();
            blinkTimer = null;
        }

        #endregion

        #region Buttons

        private void OnBuyClicked(object sender, RoutedEventArgs e)
        {
            SafeExecute("OnBuyClicked", () =>
            {
                if (Position.MarketPosition != MarketPosition.Flat || entryOrder != null)
                {
                    LogInfo("Arming blocked while position active");
                    return;
                }

                if (isArmed && armedDirection == ArmDirection.Long)
                {
                    ConfirmEntry();
                }
                else
                {
                    Arm(ArmDirection.Long);
                }
            });
        }

        private void OnSellClicked(object sender, RoutedEventArgs e)
        {
            SafeExecute("OnSellClicked", () =>
            {
                if (Position.MarketPosition != MarketPosition.Flat || entryOrder != null)
                {
                    LogInfo("Arming blocked while position active");
                    return;
                }

                if (isArmed && armedDirection == ArmDirection.Short)
                {
                    ConfirmEntry();
                }
                else
                {
                    Arm(ArmDirection.Short);
                }
            });
        }

        private void OnCloseClicked(object sender, RoutedEventArgs e)
        {
            try
            {
                Print("[RiskRay] CLOSE click received");
                ResetAndFlatten("UserClose");
            }
            catch (Exception ex)
            {
                Print("[RiskRay] CLOSE exception: " + ex);
            }
        }

        private void OnBeClicked(object sender, RoutedEventArgs e)
        {
            SafeExecute("OnBeClicked", MoveStopToBreakEven);
        }

        private void OnTrailClicked(object sender, RoutedEventArgs e)
        {
            SafeExecute("OnTrailClicked", ExecuteTrailStop);
        }

        #endregion

        #region Arming and lines

        private void Arm(ArmDirection direction)
        {
            armedDirection = direction;
            isArmed = true;
            hasPendingEntry = false;
            blinkOn = true;
            InitializeLinesForDirection(direction);
            UpdateUiState();
            LogInfo($"{direction} ARMED");
        }

        private void Disarm(bool removeLines = true)
        {
            isArmed = false;
            armedDirection = ArmDirection.None;
            hasPendingEntry = false;
            blinkOn = false;
            isDraggingStop = false;
            isDraggingTarget = false;
            if (removeLines)
                RemoveAllDrawObjects();
            UpdateUiState();
        }

        private void DisarmAndClearLines()
        {
            isArmed = false;
            armedDirection = ArmDirection.None;
            hasPendingEntry = false;
            blinkOn = false;
            isDraggingStop = false;
            isDraggingTarget = false;
            entryPrice = 0;
            stopPrice = 0;
            targetPrice = 0;
            RemoveAllDrawObjects();
            UpdateUiState();
        }

        private void InitializeLinesForDirection(ArmDirection direction)
        {
            double refPrice = GetEntryReference(direction);
            double tick = TickSize();
            entryPrice = RoundToTick(refPrice);
            stopPrice = RoundToTick(direction == ArmDirection.Long ? entryPrice - DefaultStopTicks * tick : entryPrice + DefaultStopTicks * tick);
            targetPrice = RoundToTick(direction == ArmDirection.Long ? entryPrice + DefaultTargetTicks * tick : entryPrice - DefaultTargetTicks * tick);

            bool clamped;
            EnforceValidity(GetWorkingDirection(), ref stopPrice, ref targetPrice, out clamped);

            entryLineDirty = true;
            stopLineDirty = true;
            targetLineDirty = true;
            ApplyLineUpdates();
            UpdateLabelsOnly();

            if (clamped)
                LogClampOnce("Default lines clamped to stay off-market");
        }

        private void UpdateEntryLineFromMarket()
        {
            if (!isArmed || armedDirection == ArmDirection.None)
                return;

            double refPrice = GetEntryReference(armedDirection);
            double newEntry = RoundToTick(refPrice);
            if (Math.Abs(newEntry - entryPrice) > TickSize() / 4)
            {
                entryPrice = newEntry;
                entryLineDirty = true;
            }
        }

        private void ProcessLineDrag(HorizontalLine line, ref double trackedPrice, bool isStop)
        {
            if (line == null || suppressLineEvents)
                return;

            double? candidate = GetLinePrice(line);
            if (candidate == null)
                return;

            double snapped = RoundToTick(candidate.Value);
            if (Math.Abs(snapped - trackedPrice) < TickSize() / 4)
                return;

            // User-driven drag detected
            if (isStop && !isDraggingStop)
            {
                isDraggingStop = true;
                LogDebugDrag("DragStart SL at", snapped);
            }
            else if (!isStop && !isDraggingTarget)
            {
                isDraggingTarget = true;
                LogDebugDrag("DragStart TP at", snapped);
            }

            trackedPrice = snapped;

            MarketPosition direction = GetWorkingDirection();
            if (direction == MarketPosition.Flat && !isArmed)
                return;

            bool clamped;
            double stop = stopPrice;
            double target = targetPrice;
            EnforceValidity(direction, ref stop, ref target, out clamped);

            if (isStop)
            {
                stopPrice = stop;
                SetLinePrice(stopLine, stopPrice);
                LogDebugDrag("DragMove SL ->", stopPrice);
                if (IsOrderActive(stopOrder) && Position.MarketPosition != MarketPosition.Flat)
                {
                    double currentStop = stopOrder.StopPrice;
                    if (Math.Abs(currentStop - stopPrice) >= TickSize() / 8)
                    {
                        SafeExecute("ChangeOrder-StopDrag", () => ChangeOrder(stopOrder, stopOrder.Quantity, stopOrder.LimitPrice, stopPrice));
                        LogInfo($"SL modified -> {stopPrice:F2}");
                    }
                }
            }
            else
            {
                targetPrice = target;
                SetLinePrice(targetLine, targetPrice);
                LogDebugDrag("DragMove TP ->", targetPrice);
                if (targetOrder != null && targetOrder.OrderState == OrderState.Working)
                    SafeExecute("ChangeOrder-TargetDrag", () => ChangeOrder(targetOrder, targetOrder.Quantity, targetPrice, targetOrder.StopPrice));
            }

            if (clamped)
                LogClampOnce("Line clamped to stay off-market");

            UpdateLabelsOnly();
        }

        private void UpdateEntryLine(double price, string reason)
        {
            entryPrice = RoundToTick(price);
            CreateOrUpdateEntryLine(entryPrice, $"{GetQtyLabel()} ({reason})");
        }

        private void CreateOrUpdateEntryLine(double price, string label)
        {
            suppressLineEvents = true;
            if (entryLine == null)
            {
                entryLine = Draw.HorizontalLine(this, EntryTag, price, Brushes.Black);
                if (entryLine != null)
                {
                    entryLine.Stroke = new Stroke(Brushes.Black, DashStyleHelper.Solid, 2);
                    entryLine.IsLocked = true;
                    TrackDrawObject(entryLine);
                }
            }
            else
            {
                SetLinePrice(entryLine, price);
            }
            CreateOrUpdateLabel(EntryLabelTag, price, label, Brushes.Black);
            suppressLineEvents = false;
        }

        private void CreateOrUpdateStopLine(double price, string label)
        {
            suppressLineEvents = true;
            if (stopLine == null)
            {
                stopLine = Draw.HorizontalLine(this, StopTag, price, Brushes.Red);
                if (stopLine != null)
                {
                    stopLine.Stroke = new Stroke(Brushes.Red, DashStyleHelper.Solid, 2);
                    stopLine.IsLocked = false;
                    TrackDrawObject(stopLine);
                }
            }
            else
            {
                SetLinePrice(stopLine, price);
            }
            CreateOrUpdateLabel(StopLabelTag, price, label, Brushes.Red);
            suppressLineEvents = false;
        }

        private void CreateOrUpdateTargetLine(double price, string label)
        {
            suppressLineEvents = true;
            if (targetLine == null)
            {
                targetLine = Draw.HorizontalLine(this, TargetTag, price, Brushes.ForestGreen);
                if (targetLine != null)
                {
                    targetLine.Stroke = new Stroke(Brushes.ForestGreen, DashStyleHelper.Solid, 2);
                    targetLine.IsLocked = false;
                    TrackDrawObject(targetLine);
                }
            }
            else
            {
                SetLinePrice(targetLine, price);
            }
            CreateOrUpdateLabel(TargetLabelTag, price, label, Brushes.ForestGreen);
            suppressLineEvents = false;
        }

        #endregion

        #region Orders

        private void ConfirmEntry()
        {
            if (!isArmed || armedDirection == ArmDirection.None)
                return;

            bool clamped;
            EnforceValidity(GetWorkingDirection(), ref stopPrice, ref targetPrice, out clamped);
            if (clamped)
            {
                stopLineDirty = true;
                targetLineDirty = true;
                LogClampOnce("Lines clamped before entry to avoid immediate triggers");
            }

            int qty = CalculateQuantity();
            if (qty < 1)
            {
                LogQtyBlocked();
                return;
            }

            OrderAction entryAction = armedDirection == ArmDirection.Long ? OrderAction.Buy : OrderAction.SellShort;
            OrderAction stopAction = armedDirection == ArmDirection.Long ? OrderAction.Sell : OrderAction.BuyToCover;
            OrderAction targetAction = stopAction;

            SafeExecute("SubmitOrders", () =>
            {
                entryOrder = SubmitOrderUnmanaged(0, entryAction, OrderType.Market, qty, 0, 0, null, EntrySignal);
                currentOco = Guid.NewGuid().ToString("N");
                stopOrder = SubmitOrderUnmanaged(0, stopAction, OrderType.StopMarket, qty, 0, stopPrice, currentOco, StopSignal);
                targetOrder = SubmitOrderUnmanaged(0, targetAction, OrderType.Limit, qty, targetPrice, 0, currentOco, TargetSignal);
            });

            isArmed = false;
            hasPendingEntry = true;
            UpdateUiState();
            string side = entryAction == OrderAction.Buy ? "BUY" : "SELL SHORT";
            LogInfo($"{side} entry submitted: qty {qty}, SL {stopPrice:F2}, TP {targetPrice:F2}");
            ApplyLineUpdates();
            UpdateLabelsOnly();
        }

        private void ResetAndFlatten(string reason)
        {
            LogInfo("CLOSE pressed -> cancel orders + flatten + UI reset");

            CancelActiveOrder(entryOrder, "CancelEntry");
            CancelActiveOrder(stopOrder, "CancelStop");
            CancelActiveOrder(targetOrder, "CancelTarget");

            if (Position.MarketPosition != MarketPosition.Flat)
            {
                int qty = Math.Abs(Position.Quantity);
                if (qty > 0)
                {
                    OrderAction action = Position.MarketPosition == MarketPosition.Long ? OrderAction.Sell : OrderAction.BuyToCover;
                    SafeExecute("ClosePosition", () => SubmitOrderUnmanaged(0, action, OrderType.Market, qty, 0, 0, null, "RR_CLOSE"));
                }
            }

            Disarm(true);
            RemoveAllDrawObjects();

            entryOrder = null;
            stopOrder = null;
            targetOrder = null;
            currentOco = null;
            hasPendingEntry = false;
            avgEntryPrice = 0;
        }

        private void MoveStopToBreakEven()
        {
            if (Position.MarketPosition == MarketPosition.Flat || stopOrder == null)
            {
                LogInfo("BE failed: no working stop order");
                return;
            }

            double marketRef = Position.MarketPosition == MarketPosition.Long ? GetCurrentBid() : GetCurrentAsk();
            if (marketRef <= 0)
                marketRef = Close[0];

            if ((Position.MarketPosition == MarketPosition.Long && marketRef <= avgEntryPrice)
                || (Position.MarketPosition == MarketPosition.Short && marketRef >= avgEntryPrice))
            {
                LogInfo("BE blocked: position not in profit");
                ShowBeBlockedDialog();
                return;
            }

            double newStop = Position.MarketPosition == MarketPosition.Long
                ? avgEntryPrice + (BreakEvenPlusTicks * TickSize())
                : avgEntryPrice - (BreakEvenPlusTicks * TickSize());

            stopPrice = RoundToTick(newStop);
            bool clamped;
            double target = targetPrice;
            EnforceValidity(Position.MarketPosition, ref stopPrice, ref target, out clamped);
            if (Math.Abs(target - targetPrice) > TickSize() / 8)
            {
                targetPrice = target;
                targetLineDirty = true;
            }
            stopLineDirty = true;

            if (IsOrderActive(stopOrder))
            {
                SafeExecute("ChangeOrder-BE", () => ChangeOrder(stopOrder, stopOrder.Quantity, stopOrder.LimitPrice, stopPrice));
                LogInfo($"BE pressed: stop moved to {stopPrice:F2}" + (clamped ? " (clamped)" : string.Empty));
            }
            else
            {
                LogInfo("BE failed: no working stop order");
            }

            ApplyLineUpdates();
            UpdateLabelsOnly();
        }

        private void ExecuteTrailStop()
        {
            if (Position.MarketPosition == MarketPosition.Flat)
            {
                ShowTrailMessage("TRAIL: No open position.");
                return;
            }

            if (!IsOrderActive(stopOrder))
            {
                ShowTrailMessage("TRAIL: Stop-loss order not found.");
                return;
            }

            double refPrice = Position.MarketPosition == MarketPosition.Long ? GetCurrentBid() : GetCurrentAsk();
            if (refPrice <= 0)
                refPrice = Close[0];

            double newStop = Position.MarketPosition == MarketPosition.Long
                ? refPrice - TrailOffsetTicks * TickSize()
                : refPrice + TrailOffsetTicks * TickSize();

            newStop = RoundToTick(newStop);
            stopPrice = newStop;
            SetLinePrice(stopLine, stopPrice);
            stopLineDirty = false;

            if (IsOrderActive(stopOrder))
            {
                SafeExecute("ChangeOrder-TRAIL", () => ChangeOrder(stopOrder, stopOrder.Quantity, stopOrder.LimitPrice, stopPrice));
                LogInfo($"TRAIL pressed: move SL to {stopPrice:F2} (offset {TrailOffsetTicks} ticks from {refPrice:F2})");
            }
            else
            {
                ShowTrailMessage("TRAIL: Stop-loss order not found.");
                return;
            }

            ApplyLineUpdates();
            UpdateLabelsOnly();
        }

        private void HandlePositionClosed(string reason)
        {
            LogInfo($"Exit filled via {reason}");
            stopOrder = null;
            targetOrder = null;
            entryOrder = null;
            avgEntryPrice = 0;
            hasPendingEntry = false;
            Disarm();
        }

        private void CleanupAfterError()
        {
            if (IsOrderActive(entryOrder))
                CancelOrder(entryOrder);
            if (IsOrderActive(stopOrder))
                CancelOrder(stopOrder);
            if (IsOrderActive(targetOrder))
                CancelOrder(targetOrder);
            Disarm();
            RemoveAllDrawObjects();
        }

        #endregion

        #region Helpers

        private MarketPosition GetWorkingDirection()
        {
            if (Position.MarketPosition != MarketPosition.Flat)
                return Position.MarketPosition;

            if (armedDirection == ArmDirection.Long)
                return MarketPosition.Long;
            if (armedDirection == ArmDirection.Short)
                return MarketPosition.Short;
            return MarketPosition.Flat;
        }

        private double? GetLinePrice(HorizontalLine line)
        {
            if (line == null || line.StartAnchor == null)
                return null;
            return line.StartAnchor.Price;
        }

        private double GetEntryReference(ArmDirection direction)
        {
            double last = Close[0];
            double bid = GetCurrentBid();
            double ask = GetCurrentAsk();

            if (!UseBidAskForEntry)
                return last;

            if (direction == ArmDirection.Long)
                return ask > 0 ? ask : last;
            return bid > 0 ? bid : last;
        }

        private double TickSize()
        {
            return Instrument != null && Instrument.MasterInstrument != null
                ? Instrument.MasterInstrument.TickSize
                : 0.01;
        }

        private double TickValue()
        {
            return TickSize() * (Instrument?.MasterInstrument.PointValue ?? 1);
        }

        private double RoundToTick(double price)
        {
            return Instrument.MasterInstrument.RoundToTickSize(price);
        }

        private double GetEntryReferenceForRisk()
        {
            if (Position != null && Position.MarketPosition != MarketPosition.Flat && Position.Quantity != 0)
                return Position.AveragePrice;

            if (isArmed)
                return GetEntryReference(armedDirection);

            return entryPrice;
        }

        private int CalculateQuantity()
        {
            double tick = TickSize();
            double distanceTicks = Math.Abs(entryPrice - stopPrice) / tick;
            if (distanceTicks <= 0)
                return 0;

            double perContractRisk = distanceTicks * TickValue();
            if (CommissionMode == CommissionModeOption.On)
                perContractRisk += CommissionPerContractRoundTurn;

            double rawQty = perContractRisk > 0 ? FixedRiskUSD / perContractRisk : 0;
            int qty = (int)Math.Floor(rawQty + 0.5); // half-up
            qty = Math.Min(qty, MaxContracts);
            return qty;
        }

        private void ApplyLineUpdates()
        {
            // Only move lines when their tracked price changed; avoids ticking redraws that used to override user drags.
            if (entryLineDirty)
            {
                CreateOrUpdateEntryLine(entryPrice, GetQtyLabel());
                entryLineDirty = false;
            }

            if (stopLineDirty && !isDraggingStop)
            {
                CreateOrUpdateStopLine(stopPrice, GetStopLabel());
                stopLineDirty = false;
            }

            if (targetLineDirty && !isDraggingTarget)
            {
                CreateOrUpdateTargetLine(targetPrice, GetTargetLabel());
                targetLineDirty = false;
            }
        }

        private void UpdateLabelsOnly()
        {
            if (!HasActiveLines())
                return;

            string entryLabel = GetEntryLabelSafe();
            string stopLbl = GetStopLabel();
            string targetLbl = GetTargetLabel();

            if (entryLabel != lastEntryLabel)
            {
                CreateOrUpdateLabel(EntryLabelTag, entryPrice, entryLabel, Brushes.Black);
                lastEntryLabel = entryLabel;
            }

            if (stopLbl != lastStopLabel)
            {
                CreateOrUpdateLabel(StopLabelTag, stopPrice, stopLbl, Brushes.Red);
                lastStopLabel = stopLbl;
            }

            if (targetLbl != lastTargetLabel)
            {
                CreateOrUpdateLabel(TargetLabelTag, targetPrice, targetLbl, Brushes.ForestGreen);
                lastTargetLabel = targetLbl;
            }

            if (LogLevelSetting == LogLevelOption.Debug && ShouldLogDebug())
            {
                int qty = CalculateQuantity();
                double stopTicks = Math.Abs(entryPrice - stopPrice) / TickSize();
                double targetTicks = Math.Abs(targetPrice - entryPrice) / TickSize();
                LogDebug($"Sizing: qty {qty}, stopTicks {stopTicks:F1}, targetTicks {targetTicks:F1}");
            }
        }

        private string GetEntryLabelSafe()
        {
            string qtyLabel = GetQtyLabel();
            string rrText = GetRiskRewardText();
            string combined;
            if (string.IsNullOrEmpty(qtyLabel) || qtyLabel == "0 contracts")
                qtyLabel = cachedQtyLabelText ?? "CALC…";

            if (string.IsNullOrEmpty(rrText) || rrText == "R0.00" || rrText == "R—")
                rrText = cachedRrLabelText ?? "R?";

            combined = $"{qtyLabel} | {rrText}";
            cachedEntryLabelText = combined;
            return combined;
        }

        private string GetQtyLabel()
        {
            if (!TryComputeSizing(out double tick, out double tickValue, out double entryRef, out string reason))
            {
                return UseCachedOrPlaceholder(ref cachedQtyLabelText, reason);
            }

            double stopTicks = Math.Abs(entryPrice - stopPrice) / tick;
            double perContractRisk = (stopTicks * tickValue) + (CommissionMode == CommissionModeOption.On ? CommissionPerContractRoundTurn : 0);
            double rawQty = perContractRisk > 0 ? FixedRiskUSD / perContractRisk : 0;
            int roundedQty = (int)Math.Floor(rawQty + 0.5);
            int cappedQty = Math.Min(roundedQty, MaxContracts);

            string label;
            if (cappedQty < 1)
                label = $"{rawQty:F2} (min 1)";
            else
                label = $"{cappedQty} contracts";

            cachedQtyLabelText = label;
            return label;
        }

        private string GetStopLabel()
        {
            if (!TryComputeSizing(out double tick, out double tickValue, out double entryRef, out string reason))
                return UseCachedOrPlaceholder(ref cachedStopLabelText, reason);

            double stopDistanceTicks = Math.Abs(entryRef - stopPrice) / tick;
            if (double.IsNaN(stopDistanceTicks) || double.IsInfinity(stopDistanceTicks))
                return UseCachedOrPlaceholder(ref cachedStopLabelText, "stop distance invalid");

            if (stopDistanceTicks <= double.Epsilon)
            {
                cachedStopLabelText = "SL: BE";
                return cachedStopLabelText;
            }

            double perContractRisk = (stopDistanceTicks * tickValue) + (CommissionMode == CommissionModeOption.On ? CommissionPerContractRoundTurn : 0);
            // Use at least 1 contract for display risk to avoid showing $0 when qty is blocked.
            double riskQty = Math.Max(1, GetDisplayQuantity());
            double totalRisk = perContractRisk * riskQty;
            string distanceText = FormatPointsAndTicks(stopDistanceTicks);
            string label = $"SL: -{CurrencySymbol()}{totalRisk:F2} ({distanceText})";
            if (totalRisk > MaxRiskWarningUSD)
                label = $"!! {label} !!";
            cachedStopLabelText = label;
            return label;
        }

        private string GetTargetLabel()
        {
            if (!TryComputeSizing(out double tick, out double tickValue, out double entryRef, out string reason))
                return UseCachedOrPlaceholder(ref cachedTargetLabelText, reason);

            double rewardTicks = Math.Abs(targetPrice - entryRef) / tick;
            if (double.IsNaN(rewardTicks) || double.IsInfinity(rewardTicks))
                return UseCachedOrPlaceholder(ref cachedTargetLabelText, "reward distance invalid");

            double rewardQty = Math.Max(1, GetDisplayQuantity());
            double reward = rewardTicks * tickValue * rewardQty;
            string label = $"TP: +{CurrencySymbol()}{reward:F2}";
            cachedTargetLabelText = label;
            return label;
        }

        private string GetRiskRewardText()
        {
            if (!TryComputeSizing(out double tick, out _, out double entryRef, out string reason))
                return UseCachedOrPlaceholder(ref cachedRrLabelText, reason);

            double stopTicks = Math.Abs(entryRef - stopPrice) / tick;
            double rewardTicks = Math.Abs(targetPrice - entryRef) / tick;
            if (stopTicks <= double.Epsilon || double.IsNaN(stopTicks) || double.IsInfinity(stopTicks))
                return UseCachedOrPlaceholder(ref cachedRrLabelText, "stop ticks invalid");

            double rr = rewardTicks / stopTicks;
            string rrText;
            if (Math.Abs(rr - 1.0) < 0.005)
                rrText = "R1";
            else
                rrText = $"R{rr:F2}";

            cachedRrLabelText = rrText;
            return rrText;
        }

        private double GetStopDistanceTicks(double entryRef)
        {
            double tick = TickSize();
            if (tick <= 0 || double.IsNaN(entryRef) || double.IsInfinity(entryRef))
                return double.NaN;
            return Math.Abs(entryRef - stopPrice) / tick;
        }

        private bool TryComputeSizing(out double tick, out double tickValue, out double entryRef, out string reason)
        {
            tick = TickSize();
            tickValue = TickValue();
            entryRef = double.NaN;

            if (Instrument == null || Instrument.MasterInstrument == null)
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

            entryRef = GetEntryReferenceForRisk();
            if (double.IsNaN(entryRef) || double.IsInfinity(entryRef) || entryRef <= 0)
            {
                reason = "entryRef invalid";
                return false;
            }

            reason = null;
            return true;
        }

        private string UseCachedOrPlaceholder(ref string cache, string reason)
        {
            if (ShouldLogLabelSkip())
                LogDebug($"Label update skipped: {reason}");
            if (!string.IsNullOrEmpty(cache))
                return cache;
            return "CALC…";
        }

        private string FormatPointsAndTicks(double stopDistanceTicks)
        {
            // Append stop distance as points.ticks (e.g., 20.3 where .3 = ticks past whole point)
            double tick = TickSize();
            if (tick <= 0 || double.IsNaN(stopDistanceTicks) || double.IsInfinity(stopDistanceTicks))
                return "CALC…";

            double stopPoints = stopDistanceTicks * tick;
            double wholePoints = Math.Floor(stopPoints + 1e-9);
            int ticksPerPoint = Math.Max(1, (int)Math.Round(1.0 / tick));
            int remainingTicks = (int)Math.Round((stopPoints - wholePoints) / tick);
            remainingTicks = Math.Max(0, Math.Min(remainingTicks, ticksPerPoint - 1));
            if (remainingTicks >= ticksPerPoint)
            {
                wholePoints += 1;
                remainingTicks = 0;
            }
            return $"{wholePoints}.{remainingTicks}";
        }

        private int GetLabelBarsAgo()
        {
            int baseBarsRight = Math.Max(0, LabelBarsRightOffset);
            int barsAgo = -baseBarsRight - LabelHorizontalShift;
            // Clamp to avoid extreme placements while keeping consistent alignment
            barsAgo = Math.Max(-200, Math.Min(200, barsAgo));
            return barsAgo;
        }

        private void CreateOrUpdateLabel(string tag, double price, string text, Brush brush)
        {
            // Offset in ticks, scaled up using pixel preference (fallback tick-based approach for NT8 compatibility).
            double tickOffset = Math.Max(LabelOffsetTicks, LabelOffsetPixels);
            double offsetTicks = tickOffset;
            MarketPosition dir = GetWorkingDirection();
            bool isStop = tag == StopLabelTag;

            double offsetPrice = price;
            if (dir == MarketPosition.Long)
                offsetPrice = isStop ? price - offsetTicks * TickSize() : price + offsetTicks * TickSize();
            else if (dir == MarketPosition.Short)
                offsetPrice = isStop ? price + offsetTicks * TickSize() : price - offsetTicks * TickSize();

            offsetPrice = RoundToTick(offsetPrice);

            int barsAgo = GetLabelBarsAgo();
            var label = Draw.Text(this, tag, text, barsAgo, offsetPrice, brush);
            if (label != null)
                TrackDrawObject(label);
        }

        private void SetLinePrice(HorizontalLine line, double price)
        {
            if (line == null)
                return;
            suppressLineEvents = true;
            if (line.StartAnchor != null)
                line.StartAnchor.Price = price;
            if (line.EndAnchor != null)
                line.EndAnchor.Price = price;
            suppressLineEvents = false;
        }

        private void EnforceValidity(MarketPosition direction, ref double stop, ref double target, out bool clamped)
        {
            clamped = false;
            double tick = TickSize();
            double bid = GetCurrentBid();
            double ask = GetCurrentAsk();
            if (bid <= 0)
                bid = Close[0];
            if (ask <= 0)
                ask = Close[0];

            if (direction == MarketPosition.Long)
            {
                double stopMax = bid - tick;
                double targetMin = ask + tick;
                if (stop >= stopMax)
                {
                    stop = RoundToTick(stopMax);
                    clamped = true;
                }
                if (target <= targetMin)
                {
                    target = RoundToTick(targetMin);
                    clamped = true;
                }
            }
            else if (direction == MarketPosition.Short)
            {
                double stopMin = ask + tick;
                double targetMax = bid - tick;
                if (stop <= stopMin)
                {
                    stop = RoundToTick(stopMin);
                    clamped = true;
                }
                if (target >= targetMax)
                {
                    target = RoundToTick(targetMax);
                    clamped = true;
                }
            }
        }

        private void RemoveAllDrawObjects()
        {
            suppressLineEvents = true;
            RemoveDrawObject(EntryTag);
            RemoveDrawObject(StopTag);
            RemoveDrawObject(TargetTag);
            RemoveDrawObject(EntryLabelTag);
            RemoveDrawObject(StopLabelTag);
            RemoveDrawObject(TargetLabelTag);
            foreach (var obj in trackedDrawObjects)
            {
                if (obj != null && obj.Tag != null)
                    RemoveDrawObject(obj.Tag);
            }
            trackedDrawObjects.Clear();
            entryLine = null;
            stopLine = null;
            targetLine = null;
            entryLineDirty = false;
            stopLineDirty = false;
            targetLineDirty = false;
            lastEntryLabel = null;
            lastStopLabel = null;
            lastTargetLabel = null;
            cachedEntryLabelText = null;
            cachedStopLabelText = null;
            cachedTargetLabelText = null;
            cachedQtyLabelText = null;
            cachedRrLabelText = null;
            suppressLineEvents = false;
        }

        private void TrackDrawObject(DrawingTool obj)
        {
            if (obj == null)
                return;

            trackedDrawObjects.RemoveAll(o => o != null && o.Tag == obj.Tag);
            trackedDrawObjects.Add(obj);
        }

        private void LogInfo(string message)
        {
            if (LogLevelSetting == LogLevelOption.Off)
                return;
            Print($"[RiskRay] {message}");
        }

        private void LogClampOnce(string message)
        {
            if (LogLevelSetting == LogLevelOption.Off)
                return;
            if ((DateTime.Now - lastClampLogTime).TotalSeconds < 1)
                return;
            lastClampLogTime = DateTime.Now;
            Print($"[RiskRay] {message}");
        }

        private void LogQtyBlocked()
        {
            if (LogLevelSetting == LogLevelOption.Off)
                return;
            if ((DateTime.Now - lastQtyBlockLogTime).TotalSeconds < 1)
                return;
            lastQtyBlockLogTime = DateTime.Now;
            Print("[RiskRay] Qty < 1 => block confirmation");
        }

        private void SafeExecute(string context, Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                LogFatal(context, ex);
            }
        }

        private void AttachChartEvents()
        {
            if (chartEventsAttached || ChartControl == null)
                return;

            ChartControl.MouseLeftButtonUp += ChartControl_MouseLeftButtonUp;
            chartEventsAttached = true;
        }

        private void DetachChartEvents()
        {
            if (!chartEventsAttached || ChartControl == null)
                return;

            ChartControl.MouseLeftButtonUp -= ChartControl_MouseLeftButtonUp;
            chartEventsAttached = false;
        }

        private void ChartControl_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            SafeExecute("MouseUp", FinalizeDrag);
        }

        private void FinalizeDrag()
        {
            bool didLog = false;
            if (isDraggingStop)
            {
                LogDebugDrag("DragEnd SL at", stopPrice);
                didLog = true;
            }
            if (isDraggingTarget)
            {
                LogDebugDrag("DragEnd TP at", targetPrice);
                didLog = true;
            }

            if (isDraggingStop || isDraggingTarget)
            {
                bool clamped;
                double stop = stopPrice;
                double target = targetPrice;
                EnforceValidity(GetWorkingDirection(), ref stop, ref target, out clamped);
                stopPrice = stop;
                targetPrice = target;
                SetLinePrice(stopLine, stopPrice);
                SetLinePrice(targetLine, targetPrice);
                stopLineDirty = false;
                targetLineDirty = false;
                if (clamped)
                    LogClampOnce("Line clamped at drag end");
                if (IsOrderActive(stopOrder) && Position.MarketPosition != MarketPosition.Flat)
                {
                    SafeExecute("ChangeOrder-StopDragEnd", () => ChangeOrder(stopOrder, stopOrder.Quantity, stopOrder.LimitPrice, stopPrice));
                    LogInfo($"SL modified -> {stopPrice:F2}");
                }
                if (targetOrder != null && targetOrder.OrderState == OrderState.Working)
                    SafeExecute("ChangeOrder-TargetDragEnd", () => ChangeOrder(targetOrder, targetOrder.Quantity, targetPrice, targetOrder.StopPrice));
                UpdateLabelsOnly();
            }

            isDraggingStop = false;
            isDraggingTarget = false;
        }

        private void LogFatal(string context, Exception ex)
        {
            fatalError = true;
            fatalErrorMessage = $"{context}: {ex.Message} | {ex.StackTrace}";
            Print($"[RiskRay][FATAL] {fatalErrorMessage}");
        }

        private void ShowTrailMessage(string message)
        {
            if (ChartControl == null)
            {
                Print($"[RiskRay] {message}");
                return;
            }

            ChartControl.Dispatcher.InvokeAsync(() =>
            {
                MessageBox.Show(message, "RiskRay - Trail", MessageBoxButton.OK, MessageBoxImage.Information);
            });
        }

        private void ShowBeBlockedDialog()
        {
            // Throttle dialog to avoid spamming
            if (isBeDialogOpen)
                return;
            if ((DateTime.Now - lastBeDialogTime).TotalSeconds < 2)
                return;

            if (ChartControl == null)
                return;

            isBeDialogOpen = true;
            ChartControl.Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    MessageBox.Show("BE is not allowed because the position is not in profit yet. Needs at least +1 tick.", "RiskRay - Break Even", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                finally
                {
                    lastBeDialogTime = DateTime.Now;
                    isBeDialogOpen = false;
                }
            });
        }

        private void LogDebugDrag(string prefix, double price)
        {
            if (LogLevelSetting != LogLevelOption.Debug)
                return;

            DateTime now = DateTime.Now;
            if (prefix.Contains("SL"))
            {
                if ((now - lastDragMoveLogStop).TotalMilliseconds < 200)
                    return;
                lastDragMoveLogStop = now;
            }
            else
            {
                if ((now - lastDragMoveLogTarget).TotalMilliseconds < 200)
                    return;
                lastDragMoveLogTarget = now;
            }

            Print($"[RiskRay][DEBUG] {prefix} {price:F2}");
        }

        private void LogDebug(string message)
        {
            if (LogLevelSetting != LogLevelOption.Debug)
                return;
            Print($"[RiskRay][DEBUG] {message}");
        }

        private bool ShouldLogDebug()
        {
            if ((DateTime.Now - lastDebugLogTime).TotalSeconds < 1)
                return false;
            lastDebugLogTime = DateTime.Now;
            return true;
        }

        private bool ShouldLogLabelSkip()
        {
            if ((DateTime.Now - lastLabelSkipLogTime).TotalSeconds < 1)
                return false;
            lastLabelSkipLogTime = DateTime.Now;
            return true;
        }

        private bool HasActiveLines()
        {
            return entryLine != null || stopLine != null || targetLine != null;
        }

        private string DescribeState()
        {
            if (fatalError)
                return "FatalError";

            if (Position != null && Position.MarketPosition != MarketPosition.Flat)
                return $"InPosition:{Position.MarketPosition}";

            if (isArmed && armedDirection == ArmDirection.Long)
                return "BuyArmed";
            if (isArmed && armedDirection == ArmDirection.Short)
                return "SellArmed";
            return "Idle";
        }

        private bool IsOrderActive(Order order)
        {
            if (order == null)
                return false;

            return order.OrderState != OrderState.Filled
                && order.OrderState != OrderState.Cancelled
                && order.OrderState != OrderState.Rejected;
        }

        private void CancelActiveOrder(Order order, string context)
        {
            if (!IsOrderActive(order))
                return;

            SafeExecute(context, () => CancelOrder(order));
        }

        private int GetDisplayQuantity()
        {
            if (Position.MarketPosition != MarketPosition.Flat && Position.Quantity != 0)
                return Math.Abs(Position.Quantity);

            if (hasPendingEntry && entryOrder != null && entryOrder.Quantity > 0)
                return entryOrder.Quantity;

            return CalculateQuantity();
        }

        private string CurrencySymbol()
        {
            Currency currency = Instrument?.MasterInstrument?.Currency ?? Currency.UsDollar;
            switch (currency)
            {
                case Currency.Euro:
                    return "€";
                case Currency.BritishPound:
                    return "£";
                case Currency.JapaneseYen:
                    return "¥";
                default:
                    return "$";
            }
        }

        #endregion
    }
}
