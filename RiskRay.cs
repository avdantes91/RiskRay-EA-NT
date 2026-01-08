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

        // WPF panel controls for manual interaction and blink feedback.
        private Grid uiRoot;
        private Button buyButton;
        private Button sellButton;
        private Button closeButton;
        private Button beButton;
        private Button trailButton;
        private DispatcherTimer blinkTimer;
        private bool blinkOn;

        // ARMED state flags that gate line movement and order submission.
        private ArmDirection armedDirection = ArmDirection.None;
        private bool isArmed;
        private bool hasPendingEntry;

        // Draw objects that mirror current entry/stop/target intentions.
        private HorizontalLine entryLine;
        private HorizontalLine stopLine;
        private HorizontalLine targetLine;

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

        // Cached draw objects/labels to prevent leakage and redundant re-renders.
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

        #endregion
        #endregion

        #region Lifecycle

        // Lifecycle gate: builds UI in historical for chart availability, runs self-check once in realtime, and cleans up unmanaged artifacts on termination.
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
                selfCheckDone = false;
                selfCheckFailed = false;
                selfCheckReason = null;
                selfCheckDialogShown = false;
                DebugBlink = false;
                OrderTagPrefix = "RR_";
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
                RunSelfCheckOnce();
                SafeExecute("StartBlinkTimer", StartBlinkTimer);
                SafeExecute("AttachChartEvents", AttachChartEvents);
            }
            else if (State == State.Terminated)
            {
                DetachChartEvents();
                DisposeUi();
                StopBlinkTimer();
                RemoveAllDrawObjects();
                Print($"[RiskRay][{OrderTagPrefix}] [State.Terminated] state={DescribeState()} fatal={fatalError} detail={fatalErrorMessage}");
            }
        }

        // Main tick handler in realtime: keeps entry line following bid/ask while armed and refreshes HUD without changing user-placed stops/targets.
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

        // Responds to market data for smoother drag/line updates (especially in playback), keeping stops/targets in sync while respecting clamps.
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

        // Tracks unmanaged orders, fills, and rejects; updates local handles and resets ARMED state on entry fills.
        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPriceParam, int quantity, int filled, double averageFillPrice, OrderState orderState, DateTime time, ErrorCode error, string nativeError)
        {
            try
            {
                if (order == null)
                    return;

                if (order.Name == EntrySignalLong || order.Name == EntrySignalShort)
                    entryOrder = order;
                else if (order.Name == StopSignal)
                    stopOrder = order;
                else if (order.Name == TargetSignal)
                    targetOrder = order;

                if (order.OrderState == OrderState.Filled && (order.Name == EntrySignalLong || order.Name == EntrySignalShort))
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

        // Mirrors execution-level fills into avgEntryPrice and exit handling for unmanaged flow.
        protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity, MarketPosition marketPosition, string orderId, DateTime time)
        {
            try
            {
                if (execution == null || execution.Order == null)
                    return;

                if (execution.Order.Name == EntrySignalLong || execution.Order.Name == EntrySignalShort)
                    avgEntryPrice = execution.Order.AverageFillPrice;

                if (execution.Order.Name == StopSignal || execution.Order.Name == TargetSignal)
                    HandlePositionClosed(execution.Order.Name);
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

        #endregion

        #region UI Panel

        #region UI

        // Create the WPF chart-side button panel once ChartControl is available; must marshal to dispatcher to satisfy NinjaTrader UI thread rules.
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

        // Dispatcher-safe UI refresh for opacity/enabled states based on ARMED and position status.
        private void UpdateUiState()
        {
            if (ChartControl == null || !uiLoaded)
                return;

            ChartControl.Dispatcher.InvokeAsync(() =>
            {
                if (buyButton != null)
                    buyButton.Opacity = blinkBuy ? (blinkOn ? 1 : 0.55) : 1;
                if (sellButton != null)
                    sellButton.Opacity = blinkSell ? (blinkOn ? 1 : 0.55) : 1;
                if (DebugBlink)
                    Print($"[RiskRay][DEBUG] UpdateUiState: blinkBuy={blinkBuy} blinkSell={blinkSell} phase={(blinkOn ? "on" : "off")}");
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

            ChartControl.Dispatcher.InvokeAsync(() =>
            {
                blinkBuy = isArmed && armedDirection == ArmDirection.Long;
                blinkSell = isArmed && armedDirection == ArmDirection.Short;
                if (DebugBlink)
                    Print($"[RiskRay][DEBUG] UpdateArmButtonsUI: blinkBuy={blinkBuy} blinkSell={blinkSell} phase={(blinkOn ? "on" : "off")} btnNull buy:{buyButton == null} sell:{sellButton == null}");
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
            if (blinkTimer != null)
                return;

            if (ChartControl == null || ChartControl.Dispatcher == null)
                return;

            ChartControl.Dispatcher.InvokeAsync(() =>
            {
                if (blinkTimer != null)
                    return;

                blinkTimer = new DispatcherTimer(DispatcherPriority.Normal, ChartControl.Dispatcher)
                {
                    Interval = TimeSpan.FromMilliseconds(500)
                };
                blinkTimer.Tick += (s, e) =>
                {
                    blinkTickCounter++;
                    SafeExecute("BlinkTimer", () =>
                    {
                        if (!isArmed)
                            return;

                        blinkOn = !blinkOn;
                        if (DebugBlink && blinkTickCounter % 10 == 0)
                        {
                            Print($"[RiskRay][DEBUG] Blink tick #{blinkTickCounter} flags: buy={blinkBuy} sell={blinkSell} phase={(blinkOn ? "on" : "off")} btns null? buy:{buyButton == null} sell:{sellButton == null}");
                        }
                        UpdateUiState();
                    });
                };
                blinkTimer.Start();
                if (DebugBlink)
                    Print("[RiskRay][DEBUG] Blink: start timer (500ms)");
            });
        }

        // Stop and release blink timer when no longer needed.
        private void StopBlinkTimer()
        {
            if (blinkTimer == null)
                return;

            blinkTimer.Stop();
            blinkTimer.Tick -= null;
            blinkTimer = null;
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
                Print($"[RiskRay][{OrderTagPrefix}] CLOSE click received");
                ResetAndFlatten("UserClose");
            }
            catch (Exception ex)
            {
                Print($"[RiskRay][{OrderTagPrefix}] CLOSE exception: " + ex);
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
            if (removeLines)
                RemoveAllDrawObjects();
            UpdateUiState();
        }

        // Hard reset of arming and tracked prices; used on CLOSE flow to clear HUD artifacts.
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

        // Seeds entry/SL/TP lines from current bid/ask with default offsets; applies clamps to stay off-market before confirmation.
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

        // Entry line follows live bid/ask while ARMED so user always confirms at current market-relative level.
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

        // Detect user drags on stop/target lines, clamp to valid prices, and push ChangeOrder when live orders exist.
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
                if (IsOrderActive(stopOrder) && Position.MarketPosition != MarketPosition.Flat && EnsureSelfCheckPassed())
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
            entryPrice = RoundToTick(price);
            CreateOrUpdateEntryLine(entryPrice, $"{GetQtyLabel()} ({reason})");
        }

        // Create/update entry line + label while suppressing drag callbacks.
        private void CreateOrUpdateEntryLine(double price, string label)
        {
            suppressLineEvents = true;
            if (entryLine == null)
            {
                entryLine = Draw.HorizontalLine(this, EntryLineTag, price, Brushes.Black);
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

        // Create/update stop line; remains draggable so ChangeOrder can pick up user edits.
        private void CreateOrUpdateStopLine(double price, string label)
        {
            suppressLineEvents = true;
            if (stopLine == null)
            {
                stopLine = Draw.HorizontalLine(this, StopLineTag, price, Brushes.Red);
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

        // Create/update target line with unlocked drag behavior for user adjustments.
        private void CreateOrUpdateTargetLine(double price, string label)
        {
            suppressLineEvents = true;
            if (targetLine == null)
            {
                targetLine = Draw.HorizontalLine(this, TargetLineTag, price, Brushes.ForestGreen);
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
        #endregion

        #region Order Management

        #region Orders

        // CONFIRM step: clamps lines against market, computes quantity, and submits unmanaged entry + OCO stop/target with tag prefix (INVARIANT: self-check must pass and qty>=1).
        private void ConfirmEntry()
        {
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

            int qty = CalculateQuantity();
            if (qty < 1)
            {
                LogQtyBlocked();
                return;
            }

            OrderAction entryAction = armedDirection == ArmDirection.Long ? OrderAction.Buy : OrderAction.SellShort;
            OrderAction stopAction = armedDirection == ArmDirection.Long ? OrderAction.Sell : OrderAction.BuyToCover;
            OrderAction targetAction = stopAction;
            string entryName = armedDirection == ArmDirection.Long ? EntrySignalLong : EntrySignalShort;

            SafeExecute("SubmitOrders", () =>
            {
                entryOrder = SubmitOrderUnmanaged(0, entryAction, OrderType.Market, qty, 0, 0, null, entryName);
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

        // CLOSE flow: cancels working entry/exit orders, flattens position, and clears HUD/arming state.
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
                    SafeExecute("ClosePosition", () => SubmitOrderUnmanaged(0, action, OrderType.Market, qty, 0, 0, null, CloseSignal));
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

        // Break-even helper: blocks when not profitable and clamps stop/target to avoid invalid placement.
        private void MoveStopToBreakEven()
        {
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

        // Trail helper: moves stop relative to current bid/ask using configured offset; errors displayed via message box.
        private void ExecuteTrailStop()
        {
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

        // Clears order references/UI when a stop or target fill flattens the position.
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

        // Tag helpers keep unmanaged orders/draw objects grouped under the configured prefix (invariant: all tags start with OrderTagPrefix).
        private string Tag(string suffix)
        {
            return $"{OrderTagPrefix}{suffix}";
        }

        private string EntryLineTag => Tag("ENTRY_LINE");
        private string StopLineTag => Tag("STOP_LINE");
        private string TargetLineTag => Tag("TARGET_LINE");
        private string EntryLabelTag => Tag("ENTRY_LABEL");
        private string StopLabelTag => Tag("STOP_LABEL");
        private string TargetLabelTag => Tag("TARGET_LABEL");

        private string EntrySignalLong => Tag("ENTRY_LONG");
        private string EntrySignalShort => Tag("ENTRY_SHORT");
        private string StopSignal => Tag("SL");
        private string TargetSignal => Tag("TP");
        private string CloseSignal => Tag("CLOSE");
        private string BeSignal => Tag("BE");
        private string TrailSignal => Tag("TRAIL");

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

        // Safe accessor for draggable line anchor prices.
        private double? GetLinePrice(HorizontalLine line)
        {
            if (line == null || line.StartAnchor == null)
                return null;
            return line.StartAnchor.Price;
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

        // Tick metadata helpers used by all sizing math.
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

        // Entry reference for risk sizing prefers live average price, otherwise current ARMED entry tracking.
        private double GetEntryReferenceForRisk()
        {
            if (Position != null && Position.MarketPosition != MarketPosition.Flat && Position.Quantity != 0)
                return Position.AveragePrice;

            if (isArmed)
                return GetEntryReference(armedDirection);

            return entryPrice;
        }

        // Risk-per-trade sizing: half-up rounding with MaxContracts cap; returns 0 when distance invalid.
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

        #region HUD

        // Apply pending line updates guarded by dirty flags to avoid overriding user drags or redrawing every tick.
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

        // Refresh label text/caches only (no line moves) and throttle debug output to once per second.
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

        // Entry label merges qty + RR, falling back to cached text when sizing temporarily unavailable.
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

        // Computes display quantity text using cached placeholder when sizing inputs invalid.
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

        // Stop label displays currency risk and distance; warns when over MaxRiskWarningUSD threshold.
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

        // Target label shows reward estimate; reused cached placeholder if sizing unavailable.
        private string GetTargetLabel()
        {
            if (!TryComputeSizing(out double tick, out double tickValue, out double entryRef, out string reason))
                return UseCachedOrPlaceholder(ref cachedTargetLabelText, reason);

            double rewardTicks = Math.Abs(targetPrice - entryRef) / tick;
            if (double.IsNaN(rewardTicks) || double.IsInfinity(rewardTicks))
                return UseCachedOrPlaceholder(ref cachedTargetLabelText, "reward distance invalid");

            double rewardQty = Math.Max(1, GetDisplayQuantity());
            double reward = rewardTicks * tickValue * rewardQty;
            string ptsTicks = FormatPointsAndTicks(rewardTicks);
            string label = $"TP: +{CurrencySymbol()}{reward:F2} ({ptsTicks})";
            if (DebugBlink)
                Print($"[RiskRay][DEBUG] TP label -> $={reward:F2}, targetTicks={rewardTicks:F1}, ptsTicks={ptsTicks}");
            cachedTargetLabelText = label;
            return label;
        }

        // Computes R multiple for HUD; caches last known value if sizing inputs invalid.
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

        // Utility to compute stop distance in ticks for sizing; returns NaN when instrument info missing.
        private double GetStopDistanceTicks(double entryRef)
        {
            double tick = TickSize();
            if (tick <= 0 || double.IsNaN(entryRef) || double.IsInfinity(entryRef))
                return double.NaN;
            return Math.Abs(entryRef - stopPrice) / tick;
        }

        // Validates tick metadata + entry reference before sizing; prevents downstream NaNs and blocks orders when invalid.
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

        // Uses cached HUD text or placeholder while logging skips throttled to avoid spam.
        private string UseCachedOrPlaceholder(ref string cache, string reason)
        {
            if (ShouldLogLabelSkip())
                LogDebug($"Label update skipped: {reason}");
            if (!string.IsNullOrEmpty(cache))
                return cache;
            return "CALC…";
        }

        // Converts tick distance into points.ticks format for concise HUD display.
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

        // Places labels a set distance to the right while clamping to sane bounds for visibility.
        private int GetLabelBarsAgo()
        {
            int baseBarsRight = Math.Max(0, LabelBarsRightOffset);
            int barsAgo = -baseBarsRight - LabelHorizontalShift;
            // Clamp to avoid extreme placements while keeping consistent alignment
            barsAgo = Math.Max(-200, Math.Min(200, barsAgo));
            return barsAgo;
        }

        // Draws or updates text labels with directional offsets; assumes dispatcher-safe context.
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

        #endregion

        #endregion

        #region Lines/Draw Objects (helpers)

        // Update helper: set both anchors while suppressing drag events.
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

        // Clamp stop/target to stay one tick off current bid/ask; protects unmanaged orders from instantly triggering.
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

        // Remove all strategy-owned draw objects and reset caches; used on disarm and cleanup.
        private void RemoveAllDrawObjects()
        {
            suppressLineEvents = true;
            RemoveDrawObject(EntryLineTag);
            RemoveDrawObject(StopLineTag);
            RemoveDrawObject(TargetLineTag);
            RemoveDrawObject(EntryLabelTag);
            RemoveDrawObject(StopLabelTag);
            RemoveDrawObject(TargetLabelTag);
            foreach (var obj in trackedDrawObjects)
            {
                if (obj != null && obj.Tag != null && obj.Tag.ToString().StartsWith(OrderTagPrefix, StringComparison.Ordinal))
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

        // Track draw objects to avoid duplicates and simplify cleanup.
        private void TrackDrawObject(DrawingTool obj)
        {
            if (obj == null)
                return;

            trackedDrawObjects.RemoveAll(o => o != null && o.Tag == obj.Tag);
            trackedDrawObjects.Add(obj);
        }

        #endregion

        #region Logging & Diagnostics

        // Level-gated info logger for user actions and state transitions.
        private void LogInfo(string message)
        {
            if (LogLevelSetting == LogLevelOption.Off)
                return;
            Print($"[RiskRay][{OrderTagPrefix}] {message}");
        }

        // Clamp logs are throttled to once per second to avoid spam while dragging near market.
        private void LogClampOnce(string message)
        {
            if (LogLevelSetting == LogLevelOption.Off)
                return;
            if ((DateTime.Now - lastClampLogTime).TotalSeconds < 1)
                return;
            lastClampLogTime = DateTime.Now;
            Print($"[RiskRay][{OrderTagPrefix}] {message}");
        }

        // Logs when qty calculation would be <1; throttled to avoid repeat noise.
        private void LogQtyBlocked()
        {
            if (LogLevelSetting == LogLevelOption.Off)
                return;
            if ((DateTime.Now - lastQtyBlockLogTime).TotalSeconds < 1)
                return;
            lastQtyBlockLogTime = DateTime.Now;
            Print($"[RiskRay][{OrderTagPrefix}] Qty < 1 => block confirmation");
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

        // Hook chart mouse-up to finalize drag operations (UI thread).
        private void AttachChartEvents()
        {
            if (chartEventsAttached || ChartControl == null)
                return;

            ChartControl.MouseLeftButtonUp += ChartControl_MouseLeftButtonUp;
            chartEventsAttached = true;
        }

        // Detach chart mouse-up when disposing UI to avoid leaks.
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

        // Finalizes drag: clamps prices, syncs ChangeOrder, and clears drag flags; ensures stops stay off-market.
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
                if (IsOrderActive(stopOrder) && Position.MarketPosition != MarketPosition.Flat && EnsureSelfCheckPassed())
                {
                    SafeExecute("ChangeOrder-StopDragEnd", () => ChangeOrder(stopOrder, stopOrder.Quantity, stopOrder.LimitPrice, stopPrice));
                    LogInfo($"SL modified -> {stopPrice:F2}");
                }
                if (targetOrder != null && targetOrder.OrderState == OrderState.Working && EnsureSelfCheckPassed())
                    SafeExecute("ChangeOrder-TargetDragEnd", () => ChangeOrder(targetOrder, targetOrder.Quantity, targetPrice, targetOrder.StopPrice));
                UpdateLabelsOnly();
            }

            isDraggingStop = false;
            isDraggingTarget = false;
        }

        // Marks fatal state and logs detailed exception for troubleshooting.
        private void LogFatal(string context, Exception ex)
        {
            fatalError = true;
            fatalErrorMessage = $"{context}: {ex.Message} | {ex.StackTrace}";
            Print($"[RiskRay][{OrderTagPrefix}][FATAL] {fatalErrorMessage}");
        }

        // Trail user messaging; dispatcher used to satisfy WPF thread affinity.
        private void ShowTrailMessage(string message)
        {
            if (ChartControl == null)
            {
                Print($"[RiskRay][{OrderTagPrefix}] {message}");
                return;
            }

            ChartControl.Dispatcher.InvokeAsync(() =>
            {
                MessageBox.Show(message, "RiskRay - Trail", MessageBoxButton.OK, MessageBoxImage.Information);
            });
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

            Print($"[RiskRay][{OrderTagPrefix}][DEBUG] {prefix} {price:F2}");
        }

        // Debug logger gated by Debug level.
        private void LogDebug(string message)
        {
            if (LogLevelSetting != LogLevelOption.Debug)
                return;
            Print($"[RiskRay][{OrderTagPrefix}][DEBUG] {message}");
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
            if (selfCheckDone)
                return;

            selfCheckDone = true;
            double tick = TickSize();
            double point = Instrument?.MasterInstrument?.PointValue ?? 0;
            double usdPerTick = tick * point;
            bool commissionOn = CommissionMode == CommissionModeOption.On;

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
                Print($"[RiskRay][{OrderTagPrefix}] Orders blocked: Self-check failed: {selfCheckReason}");
                return;
            }

            ChartControl.Dispatcher.InvokeAsync(() =>
            {
                MessageBox.Show($"RiskRay Self-check failed: {selfCheckReason}. Orders are blocked until fixed. Check instrument settings and restart the strategy.", "RiskRay - Self-check", MessageBoxButton.OK, MessageBoxImage.Error);
            });
        }

        // Prevent label skip logs from spamming more than once per second.
        private bool ShouldLogLabelSkip()
        {
            if ((DateTime.Now - lastLabelSkipLogTime).TotalSeconds < 1)
                return false;
            lastLabelSkipLogTime = DateTime.Now;
            return true;
        }

        #endregion

        // Quick helper to see if HUD elements exist; used to skip unnecessary updates.
        private bool HasActiveLines()
        {
            return entryLine != null || stopLine != null || targetLine != null;
        }

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

            return CalculateQuantity();
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

        #endregion
    }
}
