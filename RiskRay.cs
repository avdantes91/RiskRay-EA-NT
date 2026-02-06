// RiskRay strategy
// Manual in-chart button panel drives unmanaged bracket orders through ARMED/CONFIRM actions (BUY/SELL/BE/TRAIL/CLOSE).
// Constraints: strictly user-driven (no automation), single position per instrument, market replay friendly, unmanaged order model only.
// Components: WPF panel with blink feedback, risk-based sizing, draggable entry/SL/TP lines with clamps, unmanaged submission/tracking, and BE/TRAIL/CLOSE flows.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using System.Threading;
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
        #region Fields / State

        // Arming state used for the two-step confirmation flow.
        private enum ArmDirection
        {
            None,
            Long,
            Short
        }

        // Commission/logging options exposed via user parameters.
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

        public enum LabelOffsetModeOption
        {
            Legacy_TicksMax,
            TicksOnly,
            ApproxPixels
        }

        public enum NotificationModeOption
        {
            MessageBox,
            HUD
        }

        // WPF panel controls for manual interaction and blink feedback.
        private Grid uiRoot;
        private Button buyButton;
        private Button sellButton;
        private Button closeButton;
        private Button beButton;
        private Button trailButton;
        private DispatcherTimer blinkTimer;
        private EventHandler blinkTickHandler;
        private bool blinkOn;

        // ARMED state flags that gate line movement and order submission.
        private ArmDirection armedDirection = ArmDirection.None;
        private bool isArmed;
        private bool hasPendingEntry;

        // Tracked prices for sizing and HUD labels.
        private double entryPrice;
        private double stopPrice;
        private double targetPrice;
        private double avgEntryPrice;

        // Unmanaged order handles and OCO group identifier (OrderTagPrefix invariant).
        private Order entryOrder;
        private Order stopOrder;
        private Order targetOrder;
        private string currentOco;

        // Per-event throttles to avoid noisy logs while dragging/clamping/sizing.
        private DateTime lastClampLogTime = DateTime.MinValue;
        private DateTime lastDebugLogTime = DateTime.MinValue;
        private DateTime lastQtyBlockLogTime = DateTime.MinValue;

        // Chart attachment state and suppression flags used while programmatically moving lines.
        private bool suppressLineEvents;
        private bool uiLoaded;
        private Grid chartGrid;

        // Cached draw state/labels to prevent leakage and redundant re-renders.
        private readonly HashSet<string> processedExitIds = new HashSet<string>();
        private bool entryLineDirty;
        private bool stopLineDirty;
        private bool targetLineDirty;
        private string lastEntryLabel;
        private string lastStopLabel;
        private string lastTargetLabel;
        private double lastEntryLabelPrice = double.NaN;
        private double lastStopLabelPrice = double.NaN;
        private double lastTargetLabelPrice = double.NaN;
        private bool entryLabelDirty = true;
        private bool stopLabelDirty = true;
        private bool targetLabelDirty = true;
        private bool fatalError;
        private string fatalErrorMessage;
        private bool isDraggingStop;
        private bool isDraggingTarget;
        private bool dragFinalizePending;
        private int finalizeDragInProgress;
        private DateTime lastFinalizeDragTimeUtc = DateTime.MinValue;
        private const int FinalizeDragDuplicateSuppressMs = 100;
        private DateTime lastDragMoveLogStop = DateTime.MinValue;
        private DateTime lastDragMoveLogTarget = DateTime.MinValue;
        private DateTime lastMouseDragPulseUtc = DateTime.MinValue;
        private DateTime lastUserInteractionUtc = DateTime.MinValue;
        private const int MouseDragPulseThrottleMs = 20;
        private const int UserInteractionGraceMs = 200;
        private DateTime lastLabelRefreshLogTime = DateTime.MinValue;
        private bool chartEventsAttached;
        // Dialog/blink/self-check guards that throttle popups and enforce safety invariants.
        private bool isBeDialogOpen;
        private DateTime lastBeDialogTime = DateTime.MinValue;
        private bool blinkBuy;
        private bool blinkSell;
        private bool selfCheckDone;
        private bool selfCheckFailed;
        private string selfCheckReason;
        private bool selfCheckDialogShown;
        private int blinkTickCounter;
        private DateTime lastUiErrorLogTime = DateTime.MinValue;
        private DateTime lastCleanupLogTime = DateTime.MinValue;
        private bool receivedMarketDataThisSession;
        private double cachedTickSize;
        private string lastMilestone;
        private DateTime lastMilestoneTime = DateTime.MinValue;
        private int fatalCount;
        private DateTime lastHudMessageTime = DateTime.MinValue;
        private DateTime lastUiUnavailableLogTime = DateTime.MinValue;
        private readonly string instanceId = Guid.NewGuid().ToString("N").Substring(0, 6);
        private int stateChangeSeq = 0;
        private bool isRunningInstance;
        private bool userAdjustedStopWhileArmed;
        private bool userAdjustedTargetWhileArmed;
        private double armedStopOffsetTicks;
        private double armedTargetOffsetTicks;
        private RiskRayTagNames tags;
        private RiskRaySizing sizing;
        private RiskRayChartLines chartLines;
        private RiskRayHud hud;
        private bool helpersInitLogged;

        #endregion

        #region Inputs / Parameters

        #region Properties
        // User-configurable inputs for risk sizing, default offsets, logging, and ID tagging (assumed static during run).

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

        [NinjaScriptProperty]
        [Display(Name = "DebugBlink", Order = 17, GroupName = "Debug")]
        public bool DebugBlink { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "OrderTagPrefix", Order = 10, GroupName = "RiskRay - IDs")]
        public string OrderTagPrefix { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "LabelOffsetMode", Description = "Legacy_TicksMax treats LabelOffsetPixels as tick-equivalent (current behavior).", Order = 11, GroupName = "Labels")]
        public LabelOffsetModeOption LabelOffsetMode { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "NotificationMode", Description = "MessageBox (default) or HUD (non-blocking) for alerts.", Order = 18, GroupName = "Debug")]
        public NotificationModeOption NotificationMode { get; set; }

        #endregion
        #endregion

        #region Lifecycle

        // Lifecycle gate: builds UI in historical for chart availability, runs self-check once in realtime, and cleans up unmanaged artifacts on termination.
        protected override void OnStateChange()
        {
            stateChangeSeq++;
            Print($"{Prefix("TRACE")} OnStateChange seq={stateChangeSeq} state={State}");
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
                MaxRiskWarningUSD = 0;
                LabelOffsetTicks = 2;
                LabelOffsetPixels = 14;
                LabelBarsRightOffset = 25;
                LabelHorizontalShift = 0;
                BreakEvenPlusTicks = 0;
                TrailOffsetTicks = 20;
                selfCheckDone = false;
                selfCheckFailed = false;
                selfCheckReason = null;
                selfCheckDialogShown = false;
                DebugBlink = false;
                OrderTagPrefix = "RR_";
                LabelOffsetMode = LabelOffsetModeOption.Legacy_TicksMax;
                NotificationMode = NotificationModeOption.MessageBox;
                receivedMarketDataThisSession = false;
                cachedTickSize = 0;
                lastMilestone = null;
                lastMilestoneTime = DateTime.MinValue;
                fatalCount = 0;
                userAdjustedStopWhileArmed = false;
                userAdjustedTargetWhileArmed = false;
                armedStopOffsetTicks = 0;
                armedTargetOffsetTicks = 0;
                dragFinalizePending = false;
                lastUserInteractionUtc = DateTime.MinValue;
            }
            else if (State == State.Configure)
            {
                tags = null;
                sizing = null;
                chartLines = null;
                hud = null;
            }
            else if (State == State.Historical)
            {
                Print($"{Prefix("TRACE")} State.Historical begin");
                TryMarkRunningInstance();
                SetMilestone("Historical");
                SafeExecute("BuildUi", BuildUi);
                SafeExecute("AttachChartEvents", AttachChartEvents);
                Print($"{Prefix("TRACE")} State.Historical end");
            }
            else if (State == State.Realtime)
            {
                Print($"{Prefix("TRACE")} State.Realtime begin");
                TryMarkRunningInstance();
                SetMilestone("Realtime");
                RunSelfCheckOnce();
                SafeExecute("StartBlinkTimer", StartBlinkTimer);
                SafeExecute("AttachChartEvents", AttachChartEvents);
                Print($"{Prefix("TRACE")} State.Realtime end");
            }
            else if (State == State.Terminated)
            {
                Print($"{Prefix("TRACE")} State.Terminated begin");
                bool chartNull = ChartControl == null;
                bool dispOk = ChartControl?.Dispatcher != null && !(ChartControl?.Dispatcher.HasShutdownStarted ?? true);
                Print($"{Prefix("TRACE")} Terminated: dispatcherOk={dispOk} chartNull={chartNull}");
                SetMilestone("Terminated-Start");
                try
                {
                    if (chartNull)
                    {
                        Print($"{Prefix("TRACE")} Terminated helper instance (chartNull=True) -> skipping UI cleanup");
                        RemoveAllDrawObjects();
                    }
                    else if (!dispOk)
                    {
                        DetachChartEvents();
                        StopBlinkTimer();
                        DisposeUi(true);
                        RemoveAllDrawObjects();
                    }
                    else
                    {
                        DetachChartEvents();
                        StopBlinkTimer();
                        DisposeUi(true);
                        RemoveAllDrawObjects();
                    }
                    Print($"{Prefix()} [State.Terminated] state={DescribeState()} fatal={fatalError} detail={fatalErrorMessage} milestone={lastMilestone} at={lastMilestoneTime:o} fatalCount={fatalCount}");
                }
                catch (Exception ex)
                {
                    LogFatal("OnStateChange.Terminated", ex);
                }
                Print($"{Prefix("TRACE")} State.Terminated end");
            }
        }

        private void TryMarkRunningInstance()
        {
            if (isRunningInstance)
                return;
            Dispatcher dispatcher = ChartControl?.Dispatcher;
            if (ChartControl != null && dispatcher != null && !dispatcher.HasShutdownStarted)
            {
                isRunningInstance = true;
                Print($"{Prefix("TRACE")} RUNNING_INSTANCE chartAttached=True instrument={Instrument?.FullName}");
            }
        }

        // Main tick handler in realtime: keeps entry line following bid/ask while armed and refreshes HUD without changing user-placed stops/targets.
        protected override void OnBarUpdate()
        {
            try
            {
                EnsureHelpers();
                if (State != State.Realtime)
                    return;

                // Fallback pulse when MarketData is unavailable; skips if realtime market data already observed.
                if (receivedMarketDataThisSession)
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

        // Responds to market data for smoother drag/line updates (especially in playback), keeping stops/targets in sync while respecting clamps.
        protected override void OnMarketData(MarketDataEventArgs marketDataUpdate)
        {
            try
            {
                EnsureHelpers();
                if (State != State.Realtime)
                    return;

                receivedMarketDataThisSession = true;

                ProcessLineDrag(RiskRayChartLines.LineKind.Stop, ref stopPrice, true);
                ProcessLineDrag(RiskRayChartLines.LineKind.Target, ref targetPrice, false);
                UpdateEntryLineFromMarket();
                ApplyLineUpdates();
                UpdateLabelsOnly();
            }
            catch (Exception ex)
            {
                LogFatal("OnMarketData", ex);
            }
        }

        // Tracks unmanaged orders, fills, and rejects; updates local handles and resets ARMED state on entry fills.
        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPriceParam, int quantity, int filled, double averageFillPrice, OrderState orderState, DateTime time, ErrorCode error, string nativeError)
        {
            try
            {
                EnsureHelpers();
                if (order == null)
                    return;

                if (order.Name == tags.EntrySignalLong || order.Name == tags.EntrySignalShort)
                    entryOrder = order;
                else if (order.Name == tags.StopSignal)
                    stopOrder = order;
                else if (order.Name == tags.TargetSignal)
                    targetOrder = order;

                if (order.OrderState == OrderState.Filled && (order.Name == tags.EntrySignalLong || order.Name == tags.EntrySignalShort))
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

                if (order.OrderState == OrderState.Filled && (order.Name == tags.StopSignal || order.Name == tags.TargetSignal))
                {
                    ProcessExitFill(order, null, "OnOrderUpdate");
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

        // Mirrors execution-level fills into avgEntryPrice and exit handling for unmanaged flow.
        protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity, MarketPosition marketPosition, string orderId, DateTime time)
        {
            try
            {
                EnsureHelpers();
                if (execution == null || execution.Order == null)
                    return;

                if (execution.Order.Name == tags.EntrySignalLong || execution.Order.Name == tags.EntrySignalShort)
                    avgEntryPrice = execution.Order.AverageFillPrice;

                if (execution.Order.Name == tags.StopSignal || execution.Order.Name == tags.TargetSignal)
                    ProcessExitFill(execution.Order, execution, "OnExecutionUpdate");
            }
            catch (Exception ex)
            {
                LogFatal("OnExecutionUpdate", ex);
            }
        }

        // When flat, fully reset local state and draw objects to match broker position.
        protected override void OnPositionUpdate(Position position, double averagePrice, int quantity, MarketPosition marketPosition)
        {
            try
            {
                EnsureHelpers();
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
                    processedExitIds.Clear();
                    RemoveAllDrawObjects();
                    UpdateUiState();
                }
            }
            catch (Exception ex)
            {
                LogFatal("OnPositionUpdate", ex);
            }
        }

        #endregion

        #region UI Panel

        #region UI

        // Create the WPF chart-side button panel once ChartControl is available; must marshal to dispatcher to satisfy NinjaTrader UI thread rules.
        private void BuildUi()
        {
            if (ChartControl == null || uiLoaded)
                return;

            SetMilestone("BuildUi-Start");
            UiBeginInvoke(() =>
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
                    SetMilestone("BuildUi-End");
                });
            });
        }

        // Factory for consistent button styling and click hookup for the manual control panel.
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

        // Tear down the WPF panel and detach events on disposal or termination to avoid stale references.
        private void DisposeUi(bool forceSync = false)
        {
            if (!uiLoaded)
                return;

            SetMilestone("DisposeUi-Start");
            if (ChartControl == null || ChartControl.Dispatcher == null || ChartControl.Dispatcher.HasShutdownStarted)
            {
                // Fallback cleanup when dispatcher is gone; avoid touching UI elements.
                buyButton = null;
                sellButton = null;
                closeButton = null;
                beButton = null;
                trailButton = null;
                uiRoot = null;
                chartGrid = null;
                chartEventsAttached = false;
                uiLoaded = false;
                SetMilestone("DisposeUi-End");
                return;
            }

            Action disposer = () =>
            {
                DetachChartEvents();
                if (uiRoot != null && chartGrid != null)
                {
                    try
                    {
                        if (chartGrid.Children.Contains(uiRoot))
                            chartGrid.Children.Remove(uiRoot);
                    }
                    catch (Exception ex)
                    {
                        LogUiError("DisposeUi.Remove", ex);
                    }
                }

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
                LogInfo("UI disposed");
                SetMilestone("DisposeUi-End");
            };

            if (forceSync)
                UiInvoke(disposer);
            else
                UiBeginInvoke(disposer);
        }

        // Dispatcher-safe UI refresh for opacity/enabled states based on ARMED and position status.
        private void UpdateUiState()
        {
            if (ChartControl == null || !uiLoaded)
                return;

            UiBeginInvoke(() =>
            {
                if (buyButton != null)
                    buyButton.Opacity = blinkBuy ? (blinkOn ? 1 : 0.55) : 1;
                if (sellButton != null)
                    sellButton.Opacity = blinkSell ? (blinkOn ? 1 : 0.55) : 1;
                if (DebugBlink)
                    Print($"{Prefix("DEBUG")} UpdateUiState: blinkBuy={blinkBuy} blinkSell={blinkSell} phase={(blinkOn ? "on" : "off")}");
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

        // Keeps BUY/SELL button text and blink state aligned with ARMED direction.
        private void UpdateArmButtonsUI()
        {
            if (ChartControl == null || !uiLoaded)
                return;

            UiBeginInvoke(() =>
            {
                blinkBuy = isArmed && armedDirection == ArmDirection.Long;
                blinkSell = isArmed && armedDirection == ArmDirection.Short;
                if (DebugBlink)
                    Print($"{Prefix("DEBUG")} UpdateArmButtonsUI: blinkBuy={blinkBuy} blinkSell={blinkSell} phase={(blinkOn ? "on" : "off")} btnNull buy:{buyButton == null} sell:{sellButton == null}");
                if (buyButton != null)
                    buyButton.Content = (isArmed && armedDirection == ArmDirection.Long) ? "BUY ARMED" : "BUY";
                if (sellButton != null)
                    sellButton.Content = (isArmed && armedDirection == ArmDirection.Short) ? "SELL ARMED" : "SELL";
                if (buyButton != null)
                    buyButton.Opacity = blinkBuy ? (blinkOn ? 1 : 0.55) : 1;
                if (sellButton != null)
                    sellButton.Opacity = blinkSell ? (blinkOn ? 1 : 0.55) : 1;
            });
        }

        // Blink timer (500ms) drives visual feedback for ARMED state; must run on chart dispatcher to avoid cross-thread WPF access.
        private void StartBlinkTimer()
        {
            UiInvoke(() =>
            {
                if (blinkTimer != null || ChartControl == null || ChartControl.Dispatcher == null)
                    return;

                SetMilestone("StartBlinkTimer");
                blinkTimer = new DispatcherTimer(DispatcherPriority.Normal, ChartControl.Dispatcher)
                {
                    Interval = TimeSpan.FromMilliseconds(500)
                };
                blinkTickHandler = (s, e) =>
                {
                    blinkTickCounter++;
                    SafeExecute("BlinkTimer", () =>
                    {
                        if (!isArmed)
                            return;

                        blinkOn = !blinkOn;
                        if (DebugBlink && blinkTickCounter % 10 == 0)
                        {
                            Print($"{Prefix("DEBUG")} Blink tick #{blinkTickCounter} flags: buy={blinkBuy} sell={blinkSell} phase={(blinkOn ? "on" : "off")} btns null? buy:{buyButton == null} sell:{sellButton == null}");
                        }
                        UpdateUiState();
                    });
                };
                blinkTimer.Tick += blinkTickHandler;
                blinkTimer.Start();
                LogInfo("Blink timer started");
            });
        }

        // Stop and release blink timer when no longer needed.
        private void StopBlinkTimer()
        {
            if (blinkTimer != null && (ChartControl == null || ChartControl.Dispatcher == null || ChartControl.Dispatcher.HasShutdownStarted))
            {
                try
                {
                    if (blinkTickHandler != null)
                        blinkTimer.Tick -= blinkTickHandler;
                    blinkTimer.Stop();
                }
                catch (Exception ex)
                {
                    LogUiError("StopBlinkTimer", ex);
                }
                blinkTimer = null;
                blinkTickHandler = null;
                return;
            }

            UiInvoke(() =>
            {
                if (blinkTimer == null)
                    return;

                if (blinkTickHandler != null)
                    blinkTimer.Tick -= blinkTickHandler;
                blinkTimer.Stop();
                blinkTimer = null;
                blinkTickHandler = null;
                SetMilestone("StopBlinkTimer");
                LogInfo("Blink timer stopped");
            });
        }

        #endregion

        #region Buttons

        // BUY uses ARMED -> CONFIRM: first click arms, second submits entry + bracket if flat and no pending entry.
        private void OnBuyClicked(object sender, RoutedEventArgs e)
        {
            LogInfo("UserClick: BUYARM");
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

        // SELL mirrors BUY flow for shorts and blocks arming while any position or pending entry exists.
        private void OnSellClicked(object sender, RoutedEventArgs e)
        {
            LogInfo("UserClick: SELLARM");
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

        // CLOSE cancels all working orders and flattens exposure regardless of ARMED state.
        private void OnCloseClicked(object sender, RoutedEventArgs e)
        {
            try
            {
                LogInfo("UserClick: CLOSE");
                Print($"{Prefix()} CLOSE click received");
                ResetAndFlatten("UserClose");
            }
            catch (Exception ex)
            {
                Print($"{Prefix()} CLOSE exception: " + ex);
            }
        }

        // BE shifts stop to break-even (+offset) only when trade is in profit; otherwise shows throttled dialog.
        private void OnBeClicked(object sender, RoutedEventArgs e)
        {
            LogInfo("UserClick: BE");
            SafeExecute("OnBeClicked", MoveStopToBreakEven);
        }

        // TRAIL repositions the stop relative to current market using configured offset when a working stop exists.
        private void OnTrailClicked(object sender, RoutedEventArgs e)
        {
            LogInfo("UserClick: TRAIL");
            SafeExecute("OnTrailClicked", ExecuteTrailStop);
        }

        #endregion
        #endregion

        #region Lines/Draw Objects

        #region Arming and lines

        // Enter ARMED state and initialize lines around bid/ask; blink toggles to indicate pending confirm step.
        private void Arm(ArmDirection direction)
        {
            armedDirection = direction;
            isArmed = true;
            hasPendingEntry = false;
            blinkOn = true;
            blinkBuy = direction == ArmDirection.Long;
            blinkSell = direction == ArmDirection.Short;
            blinkTickCounter = 0;
            if (blinkTimer == null)
            {
                StartBlinkTimer();
                LogDebug("Blink: start timer");
            }
            LogDebug(direction == ArmDirection.Long ? "Blink: Long ARMED -> blinking BUY" : "Blink: Short ARMED -> blinking SELL");
            InitializeLinesForDirection(direction);
            UpdateUiState();
            LogInfo($"{direction} ARMED");
        }

        // Reset ARMED flags and optionally clear draw objects; used after fills/errors to stop auto-updating entry line.
        private void Disarm(bool removeLines = true)
        {
            isArmed = false;
            armedDirection = ArmDirection.None;
            hasPendingEntry = false;
            blinkOn = false;
            blinkBuy = false;
            blinkSell = false;
            blinkTickCounter = 0;
            LogDebug("Blink: disarmed -> stop blinking");
            isDraggingStop = false;
            isDraggingTarget = false;
            dragFinalizePending = false;
            lastUserInteractionUtc = DateTime.MinValue;
            userAdjustedStopWhileArmed = false;
            userAdjustedTargetWhileArmed = false;
            armedStopOffsetTicks = 0;
            armedTargetOffsetTicks = 0;
            ResetLabelTrackingCaches();
            if (removeLines)
            {
                entryPrice = 0;
                stopPrice = 0;
                targetPrice = 0;
                RemoveAllDrawObjects();
            }
            UpdateUiState();
        }

        // Hard reset of arming and tracked prices; used on CLOSE flow to clear HUD artifacts.
        private void DisarmAndClearLines()
        {
            Disarm(true);
        }

        // Seeds entry/SL/TP lines from current bid/ask with default offsets; applies clamps to stay off-market before confirmation.
        private void InitializeLinesForDirection(ArmDirection direction)
        {
            EnsureHelpers();
            double refPrice = GetEntryReference(direction);
            double tick = sizing.TickSize();
            entryPrice = sizing.RoundToTick(refPrice);
            stopPrice = sizing.RoundToTick(direction == ArmDirection.Long ? entryPrice - DefaultStopTicks * tick : entryPrice + DefaultStopTicks * tick);
            targetPrice = sizing.RoundToTick(direction == ArmDirection.Long ? entryPrice + DefaultTargetTicks * tick : entryPrice - DefaultTargetTicks * tick);

            bool clamped;
            EnforceValidity(GetWorkingDirection(), ref stopPrice, ref targetPrice, out clamped);

            if (tick > 0)
            {
                armedStopOffsetTicks = (stopPrice - entryPrice) / tick;
                armedTargetOffsetTicks = (targetPrice - entryPrice) / tick;
            }
            else
            {
                armedStopOffsetTicks = 0;
                armedTargetOffsetTicks = 0;
            }
            userAdjustedStopWhileArmed = false;
            userAdjustedTargetWhileArmed = false;
            LogDebug($"ARM offsets init: stop={armedStopOffsetTicks:F1}t, target={armedTargetOffsetTicks:F1}t");

            entryLineDirty = true;
            stopLineDirty = true;
            targetLineDirty = true;
            entryLabelDirty = true;
            stopLabelDirty = true;
            targetLabelDirty = true;
            ApplyLineUpdates();
            UpdateLabelsOnly();

            if (clamped)
                LogClampOnce("Default lines clamped to stay off-market");
        }

        // Entry line follows live bid/ask while ARMED so user always confirms at current market-relative level.
        private void UpdateEntryLineFromMarket()
        {
            if (!isArmed || armedDirection == ArmDirection.None || hasPendingEntry || Position.MarketPosition != MarketPosition.Flat)
                return;

            // Freeze entry auto-follow while user is interacting with lines to keep drag smooth.
            if (Mouse.LeftButton == MouseButtonState.Pressed || isDraggingStop || isDraggingTarget || dragFinalizePending)
                return;
            if (lastUserInteractionUtc != DateTime.MinValue
                && (DateTime.UtcNow - lastUserInteractionUtc).TotalMilliseconds < UserInteractionGraceMs)
                return;

            double tick = sizing.TickSize();
            if (tick <= 0)
                return;

            double refPrice = GetEntryReference(armedDirection);
            double newEntry = sizing.RoundToTick(refPrice);
            if (Math.Abs(newEntry - entryPrice) > tick / 4)
            {
                double oldStopPrice = stopPrice;
                double oldTargetPrice = targetPrice;
                entryPrice = newEntry;
                entryLineDirty = true;
                entryLabelDirty = true;

                // While dragging (or after manual adjustment), keep user-defined SL/TP fixed.
                bool mousePressed = Mouse.LeftButton == MouseButtonState.Pressed;
                bool dragGuardActive = mousePressed || dragFinalizePending;
                bool canMoveStop = !isDraggingStop && !dragGuardActive && !userAdjustedStopWhileArmed;
                bool canMoveTarget = !isDraggingTarget && !dragGuardActive && !userAdjustedTargetWhileArmed;

                if (canMoveStop)
                    stopPrice = sizing.RoundToTick(entryPrice + armedStopOffsetTicks * tick);
                if (canMoveTarget)
                    targetPrice = sizing.RoundToTick(entryPrice + armedTargetOffsetTicks * tick);

                bool clamped;
                double clampedStop = stopPrice;
                double clampedTarget = targetPrice;
                EnforceValidity(GetWorkingDirection(), ref clampedStop, ref clampedTarget, out clamped);
                if (canMoveStop)
                    stopPrice = clampedStop;
                if (canMoveTarget)
                    targetPrice = clampedTarget;

                if (canMoveStop && Math.Abs(stopPrice - oldStopPrice) > tick / 8)
                {
                    stopLineDirty = true;
                    stopLabelDirty = true;
                    armedStopOffsetTicks = (stopPrice - entryPrice) / tick;
                }
                if (canMoveTarget && Math.Abs(targetPrice - oldTargetPrice) > tick / 8)
                {
                    targetLineDirty = true;
                    targetLabelDirty = true;
                    armedTargetOffsetTicks = (targetPrice - entryPrice) / tick;
                }
            }
        }

        // Fallback capture on mouse-up: if drag was not detected during ticks, read actual line positions from chart.
        private bool CaptureManualLineAdjustmentsOnFinalize()
        {
            if (chartLines == null)
                return false;

            double tick = sizing.TickSize();
            if (tick <= 0)
                return false;

            bool changed = false;

            double? stopCandidate = chartLines.GetLinePrice(RiskRayChartLines.LineKind.Stop);
            if (stopCandidate.HasValue)
            {
                double snapped = sizing.RoundToTick(stopCandidate.Value);
                if (Math.Abs(snapped - stopPrice) >= tick / 4)
                {
                    stopPrice = snapped;
                    changed = true;
                    isDraggingStop = true;
                    MarkUserInteraction();
                    if (isArmed && !hasPendingEntry)
                    {
                        userAdjustedStopWhileArmed = true;
                        armedStopOffsetTicks = (stopPrice - entryPrice) / tick;
                        LogDebug($"ARM stop offset updated on finalize: {armedStopOffsetTicks:F1}t");
                    }
                }
            }

            double? targetCandidate = chartLines.GetLinePrice(RiskRayChartLines.LineKind.Target);
            if (targetCandidate.HasValue)
            {
                double snapped = sizing.RoundToTick(targetCandidate.Value);
                if (Math.Abs(snapped - targetPrice) >= tick / 4)
                {
                    targetPrice = snapped;
                    changed = true;
                    isDraggingTarget = true;
                    MarkUserInteraction();
                    if (isArmed && !hasPendingEntry)
                    {
                        userAdjustedTargetWhileArmed = true;
                        armedTargetOffsetTicks = (targetPrice - entryPrice) / tick;
                        LogDebug($"ARM target offset updated on finalize: {armedTargetOffsetTicks:F1}t");
                    }
                }
            }

            if (!changed)
                return false;
            MarkUserInteraction();

            bool clamped = false;
            if (ShouldClampDraggedLines())
            {
                double stop = stopPrice;
                double target = targetPrice;
                EnforceValidity(GetWorkingDirection(), ref stop, ref target, out clamped);
                stopPrice = stop;
                targetPrice = target;
            }
            chartLines.SetLinePrice(RiskRayChartLines.LineKind.Stop, stopPrice);
            chartLines.SetLinePrice(RiskRayChartLines.LineKind.Target, targetPrice);
            stopLineDirty = false;
            targetLineDirty = false;
            stopLabelDirty = true;
            targetLabelDirty = true;
            if (clamped)
                LogClampOnce("Line clamped at drag end");

            return true;
        }

        // Detect user drags on stop/target lines, clamp to valid prices, and push ChangeOrder when live orders exist.
        private void ProcessLineDrag(RiskRayChartLines.LineKind kind, ref double trackedPrice, bool isStop)
        {
            bool userInteracting = Mouse.LeftButton == MouseButtonState.Pressed || isDraggingStop || isDraggingTarget;
            if (chartLines == null || (suppressLineEvents && !userInteracting))
                return;

            double? candidate = chartLines.GetLinePrice(kind);
            if (candidate == null)
                return;

            double snapped = sizing.RoundToTick(candidate.Value);
            if (Math.Abs(snapped - trackedPrice) < sizing.TickSize() / 4)
                return;
            MarkUserInteraction();

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

            bool clamped = false;
            double stop = stopPrice;
            double target = targetPrice;
            if (ShouldClampDraggedLines())
                EnforceValidity(direction, ref stop, ref target, out clamped);

            if (isStop)
            {
                stopPrice = stop;
                chartLines.SetLinePrice(RiskRayChartLines.LineKind.Stop, stopPrice);
                stopLineDirty = false;
                stopLabelDirty = true;
                if (isArmed && !hasPendingEntry)
                {
                    double tick = sizing.TickSize();
                    if (tick > 0)
                    {
                        userAdjustedStopWhileArmed = true;
                        armedStopOffsetTicks = (stopPrice - entryPrice) / tick;
                        LogDebug($"ARM stop offset updated by drag: {armedStopOffsetTicks:F1}t");
                    }
                }
                LogDebugDrag("DragMove SL ->", stopPrice);
                if (IsOrderActive(stopOrder) && Position.MarketPosition != MarketPosition.Flat && EnsureSelfCheckPassed())
                {
                    double currentStop = stopOrder.StopPrice;
                    if (Math.Abs(currentStop - stopPrice) >= sizing.TickSize() / 8)
                    {
                        SafeExecute("ChangeOrder-StopDrag", () => ChangeOrder(stopOrder, stopOrder.Quantity, stopOrder.LimitPrice, stopPrice));
                        LogInfo($"SL modified -> {stopPrice:F2}");
                    }
                }
            }
            else
            {
                targetPrice = target;
                chartLines.SetLinePrice(RiskRayChartLines.LineKind.Target, targetPrice);
                targetLineDirty = false;
                targetLabelDirty = true;
                if (isArmed && !hasPendingEntry)
                {
                    double tick = sizing.TickSize();
                    if (tick > 0)
                    {
                        userAdjustedTargetWhileArmed = true;
                        armedTargetOffsetTicks = (targetPrice - entryPrice) / tick;
                        LogDebug($"ARM target offset updated by drag: {armedTargetOffsetTicks:F1}t");
                    }
                }
                LogDebugDrag("DragMove TP ->", targetPrice);
                if (targetOrder != null && targetOrder.OrderState == OrderState.Working && EnsureSelfCheckPassed())
                    SafeExecute("ChangeOrder-TargetDrag", () => ChangeOrder(targetOrder, targetOrder.Quantity, targetPrice, targetOrder.StopPrice));
            }

            if (clamped)
                LogClampOnce("Line clamped to stay off-market");

            UpdateLabelsOnly();
        }

        // Snap entry price to tick and mirror HUD label; used on fills and ARMED updates.
        private void UpdateEntryLine(double price, string reason)
        {
            entryPrice = sizing.RoundToTick(price);
            RiskRayHud.Snapshot snapshot = BuildHudSnapshot();
            chartLines.UpsertLine(RiskRayChartLines.LineKind.Entry, entryPrice, $"{hud.GetQtyLabel(snapshot)} ({reason})");
        }

        #endregion
        #endregion

        #region Order Management

        #region Orders

        // CONFIRM step: clamps lines against market, computes quantity, and submits unmanaged entry + OCO stop/target with tag prefix (INVARIANT: self-check must pass and qty>=1).
        private void ConfirmEntry()
        {
            EnsureHelpers();
            SetMilestone("ConfirmEntry-Start");
            if (!isArmed || armedDirection == ArmDirection.None)
                return;

            if (!EnsureSelfCheckPassed())
                return;

            bool clamped;
            EnforceValidity(GetWorkingDirection(), ref stopPrice, ref targetPrice, out clamped);
            if (clamped)
            {
                stopLineDirty = true;
                targetLineDirty = true;
                LogClampOnce("Lines clamped before entry to avoid immediate triggers");
            }

            int qty = sizing.CalculateQuantity(entryPrice, stopPrice, FixedRiskUSD, CommissionMode == CommissionModeOption.On, CommissionPerContractRoundTurn, MaxContracts);
            if (qty < 1)
            {
                LogQtyBlocked();
                return;
            }

            OrderAction entryAction = armedDirection == ArmDirection.Long ? OrderAction.Buy : OrderAction.SellShort;
            OrderAction stopAction = armedDirection == ArmDirection.Long ? OrderAction.Sell : OrderAction.BuyToCover;
            OrderAction targetAction = stopAction;
            string entryName = armedDirection == ArmDirection.Long ? tags.EntrySignalLong : tags.EntrySignalShort;

            SafeExecute("SubmitOrders", () =>
            {
                entryOrder = SubmitOrderUnmanaged(0, entryAction, OrderType.Market, qty, 0, 0, null, entryName);
                currentOco = Guid.NewGuid().ToString("N");
                stopOrder = SubmitOrderUnmanaged(0, stopAction, OrderType.StopMarket, qty, 0, stopPrice, currentOco, tags.StopSignal);
                targetOrder = SubmitOrderUnmanaged(0, targetAction, OrderType.Limit, qty, targetPrice, 0, currentOco, tags.TargetSignal);
            });

            isArmed = false;
            hasPendingEntry = true;
            UpdateUiState();
            string side = entryAction == OrderAction.Buy ? "BUY" : "SELL SHORT";
            LogInfo($"{side} entry submitted: qty {qty}, SL {stopPrice:F2}, TP {targetPrice:F2}");
            ApplyLineUpdates();
            UpdateLabelsOnly();
            SetMilestone("ConfirmEntry-End");
        }

        // CLOSE flow: cancels working entry/exit orders, flattens position, and clears HUD/arming state.
        private void ResetAndFlatten(string reason)
        {
            EnsureHelpers();
            SetMilestone("ResetAndFlatten-Start");
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
                    SafeExecute("ClosePosition", () => SubmitOrderUnmanaged(0, action, OrderType.Market, qty, 0, 0, null, tags.CloseSignal));
                }
            }

            DisarmAndClearLines();

            entryOrder = null;
            stopOrder = null;
            targetOrder = null;
            currentOco = null;
            hasPendingEntry = false;
            avgEntryPrice = 0;
            processedExitIds.Clear();
            SetMilestone("ResetAndFlatten-End");
        }

        // Break-even helper: blocks when not profitable and clamps stop/target to avoid invalid placement.
        private void MoveStopToBreakEven()
        {
            EnsureHelpers();
            if (Position.MarketPosition == MarketPosition.Flat || stopOrder == null)
            {
                LogInfo("BE failed: no working stop order");
                return;
            }

            if (!EnsureSelfCheckPassed())
                return;

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
                ? avgEntryPrice + (BreakEvenPlusTicks * sizing.TickSize())
                : avgEntryPrice - (BreakEvenPlusTicks * sizing.TickSize());

            stopPrice = sizing.RoundToTick(newStop);
            bool clamped;
            double target = targetPrice;
            EnforceValidity(Position.MarketPosition, ref stopPrice, ref target, out clamped);
            if (Math.Abs(target - targetPrice) > sizing.TickSize() / 8)
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

        // Trail helper: moves stop relative to current bid/ask using configured offset; errors displayed via message box.
        private void ExecuteTrailStop()
        {
            EnsureHelpers();
            if (Position.MarketPosition == MarketPosition.Flat)
            {
                ShowTrailMessage("TRAIL: No open position.");
                return;
            }

            if (!EnsureSelfCheckPassed())
                return;

            if (!IsOrderActive(stopOrder))
            {
                ShowTrailMessage("TRAIL: Stop-loss order not found.");
                return;
            }

            double refPrice = Position.MarketPosition == MarketPosition.Long ? GetCurrentBid() : GetCurrentAsk();
            if (refPrice <= 0)
                refPrice = Close[0];

            double newStop = Position.MarketPosition == MarketPosition.Long
                ? refPrice - TrailOffsetTicks * sizing.TickSize()
                : refPrice + TrailOffsetTicks * sizing.TickSize();

            newStop = sizing.RoundToTick(newStop);
            stopPrice = newStop;
            chartLines.SetLinePrice(RiskRayChartLines.LineKind.Stop, stopPrice);
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

        // Clears order references/UI when a stop or target fill flattens the position.
        private void HandlePositionClosed(string reason)
        {
            LogInfo($"Exit filled via {reason}");
            stopOrder = null;
            targetOrder = null;
            entryOrder = null;
            avgEntryPrice = 0;
            hasPendingEntry = false;
            processedExitIds.Clear();
            Disarm();
        }

        // Cleanup path for rejected/cancelled orders to avoid lingering ARMED state or draw objects.
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
        #endregion

        #region Helpers / Utilities

        private string NormalizedPrefix()
        {
            string prefix = string.IsNullOrWhiteSpace(OrderTagPrefix) ? "RR_" : OrderTagPrefix.Trim();
            return string.IsNullOrWhiteSpace(prefix) ? "RR_" : prefix;
        }

        private void EnsureHelpers()
        {
            string normalizedPrefix = NormalizedPrefix();
            bool created = false;
            if (tags == null)
            {
                tags = new RiskRayTagNames(normalizedPrefix, instanceId);
                created = true;
            }
            if (sizing == null)
            {
                sizing = new RiskRaySizing(() => Instrument, () => cachedTickSize, v => cachedTickSize = v);
                created = true;
            }
            if (hud == null)
            {
                hud = new RiskRayHud(
                    sizing,
                    () => Instrument,
                    GetEntryReferenceForRisk,
                    GetDisplayQuantity,
                    CurrencySymbol);
                created = true;
            }
            if (chartLines == null)
            {
                chartLines = new RiskRayChartLines(
                    this,
                    tags,
                    () => sizing.TickSize(),
                    price => sizing.RoundToTick(price),
                    GetWorkingDirection,
                    GetLabelBarsAgo,
                    GetLabelOffsetTicks,
                    () => suppressLineEvents,
                    value => suppressLineEvents = value,
                    NormalizedPrefix,
                    message =>
                    {
                        if ((DateTime.Now - lastCleanupLogTime).TotalSeconds < 1)
                            return;
                        lastCleanupLogTime = DateTime.Now;
                        Print($"{Prefix()} Draw cleanup skipped: {message}");
                    });
                created = true;
            }
            if (created && !helpersInitLogged)
            {
                helpersInitLogged = true;
                if (LogLevelSetting != LogLevelOption.Off)
                    Print($"{Prefix("DEBUG")} Helpers initialized: prefixRaw='{(OrderTagPrefix ?? "<null>")}', prefixNorm='{normalizedPrefix}'");
            }
        }

        // Resolve working direction for sizing and label offset when either armed or already in a position.
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

        // Entry reference defaults to last close unless configured to follow bid/ask for tighter arming.
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

        #region Risk & Sizing

        // Entry reference for risk sizing prefers live average price, otherwise current ARMED entry tracking.
        private double GetEntryReferenceForRisk()
        {
            if (Position != null && Position.MarketPosition != MarketPosition.Flat && Position.Quantity != 0)
                return Position.AveragePrice;

            if (isArmed)
                return GetEntryReference(armedDirection);

            return entryPrice;
        }

        #region HUD

        // Apply pending line updates guarded by dirty flags to avoid overriding user drags or redrawing every tick.
        private void ApplyLineUpdates()
        {
            EnsureHelpers();
            if (lastUserInteractionUtc != DateTime.MinValue
                && (DateTime.UtcNow - lastUserInteractionUtc).TotalMilliseconds < UserInteractionGraceMs)
                return;
            // Only move lines when their tracked price changed; avoids ticking redraws that used to override user drags.
            if (entryLineDirty)
            {
                chartLines.UpsertLine(RiskRayChartLines.LineKind.Entry, entryPrice, hud.GetQtyLabel(BuildHudSnapshot()));
                entryLineDirty = false;
            }

            if (stopLineDirty && !isDraggingStop)
            {
                chartLines.UpsertLine(RiskRayChartLines.LineKind.Stop, stopPrice, hud.GetStopLabel(BuildHudSnapshot()));
                stopLineDirty = false;
            }

            if (targetLineDirty && !isDraggingTarget)
            {
                chartLines.UpsertLine(RiskRayChartLines.LineKind.Target, targetPrice, hud.GetTargetLabel(BuildHudSnapshot()));
                targetLineDirty = false;
            }
        }

        // Refresh label text/caches only (no line moves) and throttle debug output to once per second.
        private void UpdateLabelsOnly()
        {
            EnsureHelpers();
            if (!chartLines.HasActiveLines())
                return;

            RiskRayHud.Snapshot snapshot = BuildHudSnapshot();
            string entryLabel = hud.GetEntryLabelSafe(snapshot);
            string stopLbl = hud.GetStopLabel(snapshot);
            string targetLbl = hud.GetTargetLabel(snapshot);
            double priceTolerance = Math.Max(sizing.TickSize() / 8, 0.0000001);

            bool entryPriceMoved = HasLabelPriceMoved(entryPrice, lastEntryLabelPrice, priceTolerance);
            bool stopPriceMoved = HasLabelPriceMoved(stopPrice, lastStopLabelPrice, priceTolerance);
            bool targetPriceMoved = HasLabelPriceMoved(targetPrice, lastTargetLabelPrice, priceTolerance);

            bool entryTextChanged = entryLabel != lastEntryLabel;
            if (entryLabelDirty || entryTextChanged || entryPriceMoved)
            {
                double oldEntryPrice = lastEntryLabelPrice;
                chartLines.UpsertLine(RiskRayChartLines.LineKind.Entry, entryPrice, entryLabel);
                lastEntryLabel = entryLabel;
                lastEntryLabelPrice = entryPrice;
                entryLabelDirty = false;
                LogLabelRefresh("ENTRY", entryTextChanged, entryPriceMoved, oldEntryPrice, entryPrice);
            }

            bool stopTextChanged = stopLbl != lastStopLabel;
            bool stopNeedsLineRefresh = stopLabelDirty || stopPriceMoved;
            bool stopNeedsLabelOnlyRefresh = !stopNeedsLineRefresh && stopTextChanged;
            if (isDraggingStop)
            {
                if (stopNeedsLineRefresh || stopNeedsLabelOnlyRefresh)
                {
                    double oldStopPrice = lastStopLabelPrice;
                    chartLines.UpdateLineLabel(RiskRayChartLines.LineKind.Stop, stopLbl);
                    lastStopLabel = stopLbl;
                    lastStopLabelPrice = stopPrice;
                    stopLabelDirty = false;
                    LogLabelRefresh("SL", stopTextChanged, stopPriceMoved, oldStopPrice, stopPrice);
                }
            }
            else
            {
                if (stopNeedsLineRefresh)
                {
                    double oldStopPrice = lastStopLabelPrice;
                    chartLines.UpsertLine(RiskRayChartLines.LineKind.Stop, stopPrice, stopLbl);
                    lastStopLabel = stopLbl;
                    lastStopLabelPrice = stopPrice;
                    stopLabelDirty = false;
                    LogLabelRefresh("SL", stopTextChanged, stopPriceMoved, oldStopPrice, stopPrice);
                }
                else if (stopNeedsLabelOnlyRefresh)
                {
                    double oldStopPrice = lastStopLabelPrice;
                    chartLines.UpdateLineLabel(RiskRayChartLines.LineKind.Stop, stopLbl);
                    lastStopLabel = stopLbl;
                    lastStopLabelPrice = stopPrice;
                    LogLabelRefresh("SL", true, false, oldStopPrice, stopPrice);
                }
            }

            bool targetTextChanged = targetLbl != lastTargetLabel;
            bool targetNeedsLineRefresh = targetLabelDirty || targetPriceMoved;
            bool targetNeedsLabelOnlyRefresh = !targetNeedsLineRefresh && targetTextChanged;
            if (isDraggingTarget)
            {
                if (targetNeedsLineRefresh || targetNeedsLabelOnlyRefresh)
                {
                    double oldTargetPrice = lastTargetLabelPrice;
                    chartLines.UpdateLineLabel(RiskRayChartLines.LineKind.Target, targetLbl);
                    lastTargetLabel = targetLbl;
                    lastTargetLabelPrice = targetPrice;
                    targetLabelDirty = false;
                    LogLabelRefresh("TP", targetTextChanged, targetPriceMoved, oldTargetPrice, targetPrice);
                }
            }
            else
            {
                if (targetNeedsLineRefresh)
                {
                    double oldTargetPrice = lastTargetLabelPrice;
                    chartLines.UpsertLine(RiskRayChartLines.LineKind.Target, targetPrice, targetLbl);
                    lastTargetLabel = targetLbl;
                    lastTargetLabelPrice = targetPrice;
                    targetLabelDirty = false;
                    LogLabelRefresh("TP", targetTextChanged, targetPriceMoved, oldTargetPrice, targetPrice);
                }
                else if (targetNeedsLabelOnlyRefresh)
                {
                    double oldTargetPrice = lastTargetLabelPrice;
                    chartLines.UpdateLineLabel(RiskRayChartLines.LineKind.Target, targetLbl);
                    lastTargetLabel = targetLbl;
                    lastTargetLabelPrice = targetPrice;
                    LogLabelRefresh("TP", true, false, oldTargetPrice, targetPrice);
                }
            }

            if (DebugBlink)
            {
                double tickDbg;
                double tickValueDbg;
                double entryRefDbg;
                string reasonDbg;
                if (hud.TryComputeSizing(snapshot, out tickDbg, out tickValueDbg, out entryRefDbg, out reasonDbg))
                {
                    double rewardTicksDbg = tickDbg > 0 ? Math.Abs(targetPrice - entryRefDbg) / tickDbg : 0;
                    double rewardQtyDbg = Math.Max(1, GetDisplayQuantity());
                    double rewardDbg = rewardTicksDbg * tickValueDbg * rewardQtyDbg;
                    string ptsTicksDbg = "CALC…";
                    int open = targetLbl.LastIndexOf('(');
                    int close = targetLbl.LastIndexOf(')');
                    if (open >= 0 && close > open + 1)
                        ptsTicksDbg = targetLbl.Substring(open + 1, close - open - 1);
                    Print($"{Prefix("DEBUG")} TP label -> $={rewardDbg:F2}, targetTicks={rewardTicksDbg:F1}, ptsTicks={ptsTicksDbg}");
                }
            }

            if (LogLevelSetting == LogLevelOption.Debug && ShouldLogDebug())
            {
                int qty = sizing.CalculateQuantity(entryPrice, stopPrice, FixedRiskUSD, CommissionMode == CommissionModeOption.On, CommissionPerContractRoundTurn, MaxContracts);
                double tick = sizing.TickSize();
                double entryRef = GetEntryReferenceForRisk();
                double stopTicks = tick > 0 ? Math.Abs(entryRef - stopPrice) / tick : 0;
                double targetTicks = tick > 0 ? Math.Abs(targetPrice - entryRef) / tick : 0;
                LogDebug($"Sizing: qty {qty}, stopTicks {stopTicks:F1}, targetTicks {targetTicks:F1}, entryPrice={entryPrice:F2}, stopPrice={stopPrice:F2}, targetPrice={targetPrice:F2}, entryRef={entryRef:F2}");
            }
        }

        private RiskRayHud.Snapshot BuildHudSnapshot()
        {
            return new RiskRayHud.Snapshot(
                entryPrice,
                stopPrice,
                targetPrice,
                FixedRiskUSD,
                CommissionMode == CommissionModeOption.On,
                CommissionPerContractRoundTurn,
                MaxContracts,
                MaxRiskWarningUSD);
        }

        private bool HasLabelPriceMoved(double currentPrice, double previousPrice, double tolerance)
        {
            if (double.IsNaN(currentPrice) || currentPrice <= 0)
                return false;
            if (double.IsNaN(previousPrice))
                return true;
            return Math.Abs(currentPrice - previousPrice) >= tolerance;
        }

        private bool ShouldLogLabelRefresh()
        {
            if ((DateTime.Now - lastLabelRefreshLogTime).TotalMilliseconds < 200)
                return false;
            lastLabelRefreshLogTime = DateTime.Now;
            return true;
        }

        private void LogLabelRefresh(string lineName, bool textChanged, bool priceMoved, double oldPrice, double newPrice)
        {
            if (LogLevelSetting != LogLevelOption.Debug)
                return;
            if (!textChanged && !priceMoved)
                return;
            if (!ShouldLogLabelRefresh())
                return;

            string reason = textChanged && priceMoved
                ? "text changed + price moved"
                : (textChanged ? "text changed" : "price moved");
            string oldText = double.IsNaN(oldPrice) ? "n/a" : oldPrice.ToString("F2");
            LogDebug($"Label redraw {lineName}: {reason}, oldPrice={oldText}, newPrice={newPrice:F2}");
        }

        // Places labels a set distance to the right while clamping to sane bounds for visibility.
        private int GetLabelBarsAgo()
        {
            int baseBarsRight = Math.Max(0, LabelBarsRightOffset);
            int barsAgo = -baseBarsRight - LabelHorizontalShift;
            // Clamp to avoid extreme placements while keeping consistent alignment
            barsAgo = Math.Max(-200, Math.Min(200, barsAgo));
            return barsAgo;
        }

        // Computes label offset in ticks; default preserves legacy tick/pixel max behavior.
        private double GetLabelOffsetTicks()
        {
            switch (LabelOffsetMode)
            {
                case LabelOffsetModeOption.TicksOnly:
                    return Math.Max(0, LabelOffsetTicks);
                case LabelOffsetModeOption.ApproxPixels:
                    // Approximate: treat pixels as ticks when no chart scale available; remains optional and non-default.
                    return Math.Max(Math.Max(0, LabelOffsetTicks), Math.Max(0, LabelOffsetPixels));
                case LabelOffsetModeOption.Legacy_TicksMax:
                default:
                    return Math.Max(Math.Max(0, LabelOffsetTicks), Math.Max(0, LabelOffsetPixels));
            }
        }

        #endregion

        #endregion

        #region Lines/Draw Objects (helpers)

        // During ARMED pre-entry, allow free manual placement; clamp only once at confirm/live-order phase.
        private bool ShouldClampDraggedLines()
        {
            return !(isArmed && !hasPendingEntry && Position.MarketPosition == MarketPosition.Flat);
        }

        // Clamp stop/target to stay one tick off current bid/ask; protects unmanaged orders from instantly triggering.
        private void EnforceValidity(MarketPosition direction, ref double stop, ref double target, out bool clamped)
        {
            EnsureHelpers();
            clamped = false;
            double tick = sizing.TickSize();
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
                    stop = sizing.RoundToTick(stopMax);
                    clamped = true;
                }
                if (target <= targetMin)
                {
                    target = sizing.RoundToTick(targetMin);
                    clamped = true;
                }
            }
            else if (direction == MarketPosition.Short)
            {
                double stopMin = ask + tick;
                double targetMax = bid - tick;
                if (stop <= stopMin)
                {
                    stop = sizing.RoundToTick(stopMin);
                    clamped = true;
                }
                if (target >= targetMax)
                {
                    target = sizing.RoundToTick(targetMax);
                    clamped = true;
                }
            }
        }

        // Clears label text/price caches so the next refresh always redraws at current anchors.
        private void ResetLabelTrackingCaches()
        {
            lastEntryLabel = null;
            lastStopLabel = null;
            lastTargetLabel = null;
            lastEntryLabelPrice = double.NaN;
            lastStopLabelPrice = double.NaN;
            lastTargetLabelPrice = double.NaN;
            entryLabelDirty = true;
            stopLabelDirty = true;
            targetLabelDirty = true;
            hud?.ResetCaches();
        }

        // Remove all strategy-owned draw objects and reset caches; used on disarm and cleanup.
        private void RemoveAllDrawObjects()
        {
            EnsureHelpers();
            SetMilestone("RemoveAllDrawObjects-Start");
            chartLines?.RemoveAllDrawObjects();
            entryLineDirty = false;
            stopLineDirty = false;
            targetLineDirty = false;
            ResetLabelTrackingCaches();
            SetMilestone("RemoveAllDrawObjects-End");
        }

        #endregion

        #region Logging & Diagnostics

        private string Prefix(string level = null)
        {
            var tag = NormalizedPrefix();
            return level == null
                ? $"[RiskRay][{tag}][I:{instanceId}]"
                : $"[RiskRay][{tag}][I:{instanceId}][{level}]";
        }

        // Safe dispatcher helpers: UI calls no-op if dispatcher unavailable; errors throttled to avoid noisy logs.
        private void UiInvoke(Action action)
        {
            if (action == null)
                return;
            Dispatcher dispatcher = ChartControl?.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted)
            {
                if (isRunningInstance && (DateTime.Now - lastUiUnavailableLogTime).TotalSeconds > 1)
                {
                    lastUiUnavailableLogTime = DateTime.Now;
                    Print($"{Prefix("UI")} Dispatcher unavailable for UiInvoke (null or shutdown)");
                }
                return;
            }
            try
            {
                dispatcher.Invoke(() =>
                {
                    try
                    {
                        action();
                    }
                    catch (Exception ex)
                    {
                        LogFatal("UI.Sync", ex);
                    }
                });
            }
            catch (Exception ex)
            {
                LogFatal("UiInvoke.Invoke", ex);
            }
        }

        private void UiBeginInvoke(Action action)
        {
            if (action == null)
                return;
            Dispatcher dispatcher = ChartControl?.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted)
            {
                if (isRunningInstance && (DateTime.Now - lastUiUnavailableLogTime).TotalSeconds > 1)
                {
                    lastUiUnavailableLogTime = DateTime.Now;
                    Print($"{Prefix("UI")} Dispatcher unavailable for UiBeginInvoke (null or shutdown)");
                }
                return;
            }
            try
            {
                dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        action();
                    }
                    catch (Exception ex)
                    {
                        LogFatal("UI.Async", ex);
                    }
                });
            }
            catch (Exception ex)
            {
                LogFatal("UiBeginInvoke.Schedule", ex);
            }
        }

        // Level-gated info logger for user actions and state transitions.
        private void LogInfo(string message)
        {
            if (LogLevelSetting == LogLevelOption.Off)
                return;
            Print($"{Prefix()} {message}");
        }

        // Milestone helper for stability diagnostics (no spam).
        private void SetMilestone(string marker)
        {
            lastMilestone = marker;
            lastMilestoneTime = DateTime.Now;
        }

        private void MarkUserInteraction()
        {
            lastUserInteractionUtc = DateTime.UtcNow;
        }

        private void LogUiError(string context, Exception ex)
        {
            if ((DateTime.Now - lastUiErrorLogTime).TotalSeconds < 1)
                return;
            lastUiErrorLogTime = DateTime.Now;
            Print($"{Prefix("UI")} {context}: {ex.Message}");
        }

        // Clamp logs are throttled to once per second to avoid spam while dragging near market.
        private void LogClampOnce(string message)
        {
            if (LogLevelSetting == LogLevelOption.Off)
                return;
            if ((DateTime.Now - lastClampLogTime).TotalSeconds < 1)
                return;
            lastClampLogTime = DateTime.Now;
            Print($"{Prefix()} {message}");
        }

        // Logs when qty calculation would be <1; throttled to avoid repeat noise.
        private void LogQtyBlocked()
        {
            if (LogLevelSetting == LogLevelOption.Off)
                return;
            if ((DateTime.Now - lastQtyBlockLogTime).TotalSeconds < 1)
                return;
            lastQtyBlockLogTime = DateTime.Now;
            Print($"{Prefix()} Qty < 1 => block confirmation");
        }

        // Wrapper to centralize fatal logging while preserving original context.
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

        // Hook chart mouse-up on UI thread; actual finalize work is marshaled back to strategy thread.
        private void AttachChartEvents()
        {
            UiInvoke(() =>
            {
                if (chartEventsAttached || ChartControl == null)
                    return;

                ChartControl.PreviewMouseLeftButtonUp += ChartControl_PreviewMouseLeftButtonUp;
                ChartControl.MouseLeftButtonUp += ChartControl_MouseLeftButtonUp;
                ChartControl.MouseLeave += ChartControl_MouseLeave;
                ChartControl.MouseMove += ChartControl_MouseMove;
                chartEventsAttached = true;
                SetMilestone("AttachChartEvents");
                LogInfo("Chart events attached");
            });
        }

        // Detach chart mouse-up when disposing UI to avoid leaks.
        private void DetachChartEvents()
        {
            if (ChartControl == null || ChartControl.Dispatcher == null || ChartControl.Dispatcher.HasShutdownStarted)
            {
                chartEventsAttached = false;
                return;
            }

            UiInvoke(() =>
            {
                if (!chartEventsAttached || ChartControl == null)
                    return;

                ChartControl.PreviewMouseLeftButtonUp -= ChartControl_PreviewMouseLeftButtonUp;
                ChartControl.MouseLeftButtonUp -= ChartControl_MouseLeftButtonUp;
                ChartControl.MouseLeave -= ChartControl_MouseLeave;
                ChartControl.MouseMove -= ChartControl_MouseMove;
                chartEventsAttached = false;
                SetMilestone("DetachChartEvents");
                LogInfo("Chart events detached");
            });
        }

        private void ChartControl_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            MarkUserInteraction();
            dragFinalizePending = true;
            TriggerCustomEvent(_ => SafeExecute("PreviewMouseUp", FinalizeDrag), null);
        }

        private void ChartControl_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            MarkUserInteraction();
            dragFinalizePending = true;
            TriggerCustomEvent(_ => SafeExecute("MouseUp", FinalizeDrag), null);
        }

        private void ChartControl_MouseLeave(object sender, MouseEventArgs e)
        {
            MarkUserInteraction();
            dragFinalizePending = true;
            TriggerCustomEvent(_ => SafeExecute("MouseLeave", FinalizeDrag), null);
        }

        private void ChartControl_MouseMove(object sender, MouseEventArgs e)
        {
            if (Mouse.LeftButton != MouseButtonState.Pressed)
                return;

            if (!isArmed && Position.MarketPosition == MarketPosition.Flat)
                return;

            double elapsedMs = (DateTime.UtcNow - lastMouseDragPulseUtc).TotalMilliseconds;
            if (lastMouseDragPulseUtc != DateTime.MinValue && elapsedMs < MouseDragPulseThrottleMs)
                return;

            lastMouseDragPulseUtc = DateTime.UtcNow;
            MarkUserInteraction();
            TriggerCustomEvent(_ => SafeExecute("MouseMoveDragPulse", HandleMouseDragPulse), null);
        }

        // Poll drag movement on mouse move so TP/SL labels and sizing update even between market ticks.
        private void HandleMouseDragPulse()
        {
            if (State != State.Realtime)
                return;

            ProcessLineDrag(RiskRayChartLines.LineKind.Stop, ref stopPrice, true);
            ProcessLineDrag(RiskRayChartLines.LineKind.Target, ref targetPrice, false);
        }

        // Finalizes drag (strategy thread): clamps prices, syncs ChangeOrder, and clears drag flags; ensures stops stay off-market.
        private void FinalizeDrag()
        {
            if (Interlocked.Exchange(ref finalizeDragInProgress, 1) == 1)
            {
                LogDebug("FinalizeDrag blocked by reentry guard");
                return;
            }

            try
            {
                bool hasDrag = isDraggingStop || isDraggingTarget;
                if (!hasDrag && CaptureManualLineAdjustmentsOnFinalize())
                    hasDrag = true;
                if (!hasDrag)
                {
                    double elapsedMs = (DateTime.UtcNow - lastFinalizeDragTimeUtc).TotalMilliseconds;
                    if (lastFinalizeDragTimeUtc != DateTime.MinValue && elapsedMs <= FinalizeDragDuplicateSuppressMs)
                        LogDebug($"FinalizeDrag duplicate suppressed ({elapsedMs:F0}ms)");
                    return;
                }
                MarkUserInteraction();

                SetMilestone("FinalizeDrag-Start");
                bool didLog = false;
                if (isDraggingStop)
                {
                    LogDebug($"Drag end SL final={stopPrice:F2}");
                    didLog = true;
                }
                if (isDraggingTarget)
                {
                    LogDebug($"Drag end TP final={targetPrice:F2}");
                    didLog = true;
                }

                if (isDraggingStop || isDraggingTarget)
                {
                    bool clamped = false;
                    if (ShouldClampDraggedLines())
                    {
                        double stop = stopPrice;
                        double target = targetPrice;
                        EnforceValidity(GetWorkingDirection(), ref stop, ref target, out clamped);
                        stopPrice = stop;
                        targetPrice = target;
                    }
                    if (chartLines != null)
                    {
                        chartLines.SetLinePrice(RiskRayChartLines.LineKind.Stop, stopPrice);
                        chartLines.SetLinePrice(RiskRayChartLines.LineKind.Target, targetPrice);
                    }
                    stopLineDirty = false;
                    targetLineDirty = false;
                    stopLabelDirty = true;
                    targetLabelDirty = true;
                    if (clamped)
                        LogClampOnce("Line clamped at drag end");
                    if (IsOrderActive(stopOrder) && Position.MarketPosition != MarketPosition.Flat && EnsureSelfCheckPassed())
                    {
                        SafeExecute("ChangeOrder-StopDragEnd", () => ChangeOrder(stopOrder, stopOrder.Quantity, stopOrder.LimitPrice, stopPrice));
                        LogInfo($"SL modified -> {stopPrice:F2}");
                    }
                    if (targetOrder != null && targetOrder.OrderState == OrderState.Working && EnsureSelfCheckPassed())
                        SafeExecute("ChangeOrder-TargetDragEnd", () => ChangeOrder(targetOrder, targetOrder.Quantity, targetPrice, targetOrder.StopPrice));
                }

                isDraggingStop = false;
                isDraggingTarget = false;
                if (didLog)
                    UpdateLabelsOnly();
                lastFinalizeDragTimeUtc = DateTime.UtcNow;
                SetMilestone("FinalizeDrag-End");
            }
            finally
            {
                dragFinalizePending = false;
                Interlocked.Exchange(ref finalizeDragInProgress, 0);
            }
        }

        // Marks fatal state and logs detailed exception for troubleshooting.
        private void LogFatal(string context, Exception ex)
        {
            fatalError = true;
            fatalErrorMessage = $"{context}: {ex.Message} | {ex.StackTrace}";
            fatalCount++;
            Print($"{Prefix("FATAL")} {fatalErrorMessage}");
        }

        private void ShowNotification(string title, string message)
        {
            EnsureHelpers();
            if (ChartControl == null)
            {
                Print($"{Prefix()} {title}: {message}");
                return;
            }

            if (NotificationMode == NotificationModeOption.HUD)
            {
                if ((DateTime.Now - lastHudMessageTime).TotalSeconds < 0.5)
                    return;
                lastHudMessageTime = DateTime.Now;
                chartLines.ShowHudNotification($"{title}: {message}");
                return;
            }

            UiBeginInvoke(() =>
            {
                MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
            });
        }

        // Trail user messaging; dispatcher used to satisfy WPF thread affinity. HUD option is non-blocking.
        private void ShowTrailMessage(string message)
        {
            if (ChartControl == null)
            {
                Print($"{Prefix()} {message}");
                return;
            }

            ShowNotification("RiskRay - Trail", message);
        }

        // Break-even blocked dialog; throttled and dispatcher-bound to avoid repeated popups during flat/profit checks.
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
            if (NotificationMode == NotificationModeOption.HUD)
            {
                ShowNotification("RiskRay - Break Even", "BE is not allowed because the position is not in profit yet. Needs at least +1 tick.");
                lastBeDialogTime = DateTime.Now;
                isBeDialogOpen = false;
                return;
            }

            UiBeginInvoke(() =>
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

        // Drag debug output throttled to 200ms per side to stay readable.
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

            Print($"{Prefix("DEBUG")} {prefix} {price:F2}");
        }

        // Debug logger gated by Debug level.
        private void LogDebug(string message)
        {
            if (LogLevelSetting != LogLevelOption.Debug)
                return;
            Print($"{Prefix("DEBUG")} {message}");
        }

        // Simple throttle to avoid chatty debug logs.
        private bool ShouldLogDebug()
        {
            if ((DateTime.Now - lastDebugLogTime).TotalSeconds < 1)
                return false;
            lastDebugLogTime = DateTime.Now;
            return true;
        }

        // Validates instrument metadata once per session and blocks order actions when invalid.
        private void RunSelfCheckOnce()
        {
            EnsureHelpers();
            if (selfCheckDone)
                return;

            selfCheckDone = true;
            double tick = sizing.TickSize();
            double point = Instrument?.MasterInstrument?.PointValue ?? 0;
            double usdPerTick = tick * point;
            bool commissionOn = CommissionMode == CommissionModeOption.On;
            if (tick > 0)
                cachedTickSize = tick;

            LogInfo($"SelfCheck: Instrument={Instrument?.FullName}, TickSize={tick}, PointValue={point}, UsdPerTick={usdPerTick}, MaxContracts={MaxContracts}, MaxRiskUsd={FixedRiskUSD}, CommissionOn={commissionOn}");

            List<string> reasons = new List<string>();
            if (tick <= 0) reasons.Add("TickSize invalid");
            if (point <= 0) reasons.Add("PointValue invalid");
            if (usdPerTick <= 0) reasons.Add("UsdPerTick invalid");
            if (MaxContracts <= 0) reasons.Add("MaxContracts invalid");
            if (FixedRiskUSD <= 0) reasons.Add("MaxRiskUsd invalid");

            if (reasons.Count > 0)
            {
                selfCheckFailed = true;
                selfCheckReason = string.Join("; ", reasons);
                LogInfo($"SelfCheck FAILED: {selfCheckReason}");
                ShowSelfCheckFailedDialog();
            }
            else
            {
                selfCheckFailed = false;
                selfCheckReason = null;
            }
        }

        // Guard used before sensitive operations; shows dialog when self-check failed.
        private bool EnsureSelfCheckPassed()
        {
            if (!selfCheckFailed)
                return true;

            ShowSelfCheckFailedDialog();
            return false;
        }

        // Throttled error dialog for self-check failures; orders remain blocked until resolved.
        private void ShowSelfCheckFailedDialog()
        {
            if (selfCheckDialogShown)
                return;

            selfCheckDialogShown = true;
            if (ChartControl == null)
            {
                Print($"{Prefix()} Orders blocked: Self-check failed: {selfCheckReason}");
                return;
            }

            ShowNotification("RiskRay - Self-check", $"RiskRay Self-check failed: {selfCheckReason}. Orders are blocked until fixed. Check instrument settings and restart the strategy.");
        }

        #endregion

        // Human-readable state for logs; preserves fatal flag even when positions exist.
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

        // Invariant helper: unmanaged order considered active unless filled/cancelled/rejected.
        private bool IsOrderActive(Order order)
        {
            if (order == null)
                return false;

            return order.OrderState != OrderState.Filled
                && order.OrderState != OrderState.Cancelled
                && order.OrderState != OrderState.Rejected;
        }

        // Cancel only when active; wraps in SafeExecute for consistent fatal handling.
        private void CancelActiveOrder(Order order, string context)
        {
            if (!IsOrderActive(order))
                return;

            SafeExecute(context, () => CancelOrder(order));
        }

        // Display qty prefers live position or working entry qty; falls back to calculated size for HUD.
        private int GetDisplayQuantity()
        {
            if (Position.MarketPosition != MarketPosition.Flat && Position.Quantity != 0)
                return Math.Abs(Position.Quantity);

            if (hasPendingEntry && entryOrder != null && entryOrder.Quantity > 0)
                return entryOrder.Quantity;

            return sizing.CalculateQuantity(entryPrice, stopPrice, FixedRiskUSD, CommissionMode == CommissionModeOption.On, CommissionPerContractRoundTurn, MaxContracts);
        }

        // Deduplicate and log exit fills to avoid repeated cleanup on multi-part events.
        private void ProcessExitFill(Order order, Execution exec, string source)
        {
            if (order == null)
                return;
            // Only process final fills for stop/target exits.
            if (order.OrderState != OrderState.Filled)
                return;
            string execId = exec?.ExecutionId;
            string orderKey = !string.IsNullOrEmpty(order.OrderId) ? $"ORD:{order.OrderId}" : $"ORDNAME:{order.Name}";
            if (processedExitIds.Contains(orderKey) || (!string.IsNullOrEmpty(execId) && processedExitIds.Contains($"EX:{execId}")))
            {
                LogDebug($"Exit fill deduped ({source}) name={order.Name} orderId={order.OrderId} execId={execId}");
                return;
            }
            processedExitIds.Add(orderKey);
            if (!string.IsNullOrEmpty(execId))
                processedExitIds.Add($"EX:{execId}");

            if (LogLevelSetting != LogLevelOption.Off)
            {
                string execMsg = exec != null ? $" execId={exec.ExecutionId} execQty={exec.Quantity} execPrice={exec.Price:F2}" : string.Empty;
                LogInfo($"Exit fill handled ({source}) name={order.Name} orderId={order.OrderId} oco={order.Oco} state={order.OrderState} filled={order.Filled}/{order.Quantity} avg={order.AverageFillPrice:F2}{execMsg}");
            }

            HandlePositionClosed(order.Name);
        }

        // Currency symbol helper for HUD labels; defaults to USD.
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

        #if true
        // Fallback helper types kept inside RiskRay so NT8 can compile this strategy
        // even when external helper files are not in the active compile scope.
        private sealed class RiskRayTagNames
        {
            private readonly string prefix;

            public RiskRayTagNames(string orderTagPrefix, string drawInstanceScope = null)
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

        private sealed class RiskRaySizing
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
                int qty = (int)Math.Floor(rawQty + 0.5);
                qty = Math.Min(qty, maxContracts);
                return qty;
            }
        }

        private sealed class RiskRayChartLines
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
                        line.IsLocked = kind == LineKind.Entry;
                        line.IsAutoScale = false;
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
                string normalizedPrefix = normalizedPrefixProvider != null ? normalizedPrefixProvider() : string.Empty;
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

                        if (tagStr.StartsWith(normalizedPrefix, StringComparison.Ordinal))
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
                    // Labels should follow line updates, not be independently draggable by user.
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

        private sealed class RiskRayHud
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

        #endif
        #endregion
    }
}
