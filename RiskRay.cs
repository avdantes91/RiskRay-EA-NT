// RiskRay strategy
// Manual in-chart button panel drives unmanaged bracket orders through ARMED/CONFIRM actions (BUY/SELL/BE/TRAIL/CLOSE).
// Constraints: strictly user-driven (no automation), single position per instrument, market replay friendly, unmanaged order model only.
// Components: WPF panel with blink feedback, risk-based sizing, draggable entry/SL/TP lines with clamps, unmanaged submission/tracking, and BE/TRAIL/CLOSE flows.
// Safety updates: split UI vs trade-safe execution, UI-unavailable latch, fatal trade guard + notifications,
// and on-demand diagnostic snapshots for bracket issues and critical exceptions.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
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
    public interface IRiskRayPanelHost
    {
        ChartControl ChartControl { get; }
        bool UiUnavailablePermanently { get; }
        bool IsArmed { get; }
        bool IsArmedLong { get; }
        bool IsArmedShort { get; }
        bool HasPosition { get; }
        bool HasPendingEntry { get; }
        bool HasStopOrder { get; }
        bool HasTargetOrder { get; }
        bool DebugBlinkEnabled { get; }

        void ArmLong();
        void ArmShort();
        void Confirm();
        void Close();
        void BreakEven();
        void Trail();

        void UiInvoke(Action action);
        void UiBeginInvoke(Action action);
        void SafeExecuteUI(string context, Action action);
        void SafeExecute(string context, Action action);
        void SetMilestone(string marker);
        void LogInfo(string message);
        void LogDebug(string message);
        void LogUiError(string context, Exception ex);
        void Print(string message);
        string Prefix(string level = null);
        bool ShouldLogDebugBlink();
    }

    internal sealed class RiskRayPanel
    {
        private readonly IRiskRayPanelHost host;
        private Grid uiRoot;
        private Button buyButton;
        private Button sellButton;
        private Button closeButton;
        private Button beButton;
        private Button trailButton;
        private Grid chartGrid;
        private bool uiLoaded;
        private DispatcherTimer blinkTimer;
        private EventHandler blinkTickHandler;
        private bool blinkOn;
        private bool blinkBuy;
        private bool blinkSell;
        private int blinkTickCounter;
        private RoutedEventHandler buyClickHandler;
        private RoutedEventHandler sellClickHandler;
        private RoutedEventHandler closeClickHandler;
        private RoutedEventHandler beClickHandler;
        private RoutedEventHandler trailClickHandler;

        public RiskRayPanel(IRiskRayPanelHost host)
        {
            this.host = host;
        }

        public bool IsBlinkTimerRunning => blinkTimer != null;

        public void BuildUi()
        {
            if (host.ChartControl == null || uiLoaded)
                return;

            host.SetMilestone("BuildUi-Start");
            host.UiBeginInvoke(() =>
            {
                host.SafeExecuteUI("BuildUi.Dispatcher", () =>
                {
                    if (uiLoaded)
                        return;
                    if (host.ChartControl == null)
                        return;

                    chartGrid = host.ChartControl.Parent as Grid;
                    if (chartGrid == null)
                        return;

                    uiRoot = new Grid
                    {
                        HorizontalAlignment = HorizontalAlignment.Left,
                        VerticalAlignment = VerticalAlignment.Top,
                        Margin = new Thickness(8),
                        Background = Brushes.Transparent,
                        IsHitTestVisible = true
                    };

                    uiRoot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    uiRoot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    uiRoot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    uiRoot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    uiRoot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    buyClickHandler = (s, e) => host.SafeExecuteUI("UI.BUY", () => host.ArmLong());
                    sellClickHandler = (s, e) => host.SafeExecuteUI("UI.SELL", () => host.ArmShort());
                    closeClickHandler = (s, e) => host.SafeExecuteUI("UI.CLOSE", () => host.Close());
                    beClickHandler = (s, e) => host.SafeExecuteUI("UI.BE", () => host.BreakEven());
                    trailClickHandler = (s, e) => host.SafeExecuteUI("UI.TRAIL", () => host.Trail());

                    buyButton = CreateButton("BUY", Brushes.DarkGreen, buyClickHandler);
                    sellButton = CreateButton("SELL", Brushes.DarkRed, sellClickHandler);
                    closeButton = CreateButton("CLOSE", Brushes.DimGray, closeClickHandler);
                    beButton = CreateButton("BE", Brushes.DarkSlateBlue, beClickHandler);
                    trailButton = CreateButton("TRAIL", Brushes.DarkOrange, trailClickHandler);

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
                    Panel.SetZIndex(uiRoot, 10000);
                    uiLoaded = true;
                    UpdateUiState();
                    host.SetMilestone("BuildUi-End");
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

        public void DisposeUi(bool forceSync = false)
        {
            if (!uiLoaded)
                return;

            host.SetMilestone("DisposeUi-Start");
            if (host.ChartControl == null || host.ChartControl.Dispatcher == null || host.ChartControl.Dispatcher.HasShutdownStarted)
            {
                ReleaseUiReferencesNoDispatcher();
                host.SetMilestone("DisposeUi-End");
                return;
            }

            Action disposer = () =>
            {
                if (uiRoot != null && chartGrid != null)
                {
                    try
                    {
                        if (chartGrid.Children.Contains(uiRoot))
                            chartGrid.Children.Remove(uiRoot);
                    }
                    catch (Exception ex)
                    {
                        host.LogUiError("DisposeUi.Remove", ex);
                    }
                }

                if (buyButton != null && buyClickHandler != null)
                    buyButton.Click -= buyClickHandler;
                if (sellButton != null && sellClickHandler != null)
                    sellButton.Click -= sellClickHandler;
                if (closeButton != null && closeClickHandler != null)
                    closeButton.Click -= closeClickHandler;
                if (beButton != null && beClickHandler != null)
                    beButton.Click -= beClickHandler;
                if (trailButton != null && trailClickHandler != null)
                    trailButton.Click -= trailClickHandler;

                uiRoot = null;
                buyButton = null;
                sellButton = null;
                closeButton = null;
                beButton = null;
                trailButton = null;
                chartGrid = null;
                uiLoaded = false;
                host.LogInfo("UI disposed");
                host.SetMilestone("DisposeUi-End");
            };

            if (forceSync)
                host.UiInvoke(() => host.SafeExecuteUI("DisposeUi", disposer));
            else
                host.UiBeginInvoke(() => host.SafeExecuteUI("DisposeUi", disposer));
        }

        public void ReleaseUiReferencesNoDispatcher()
        {
            buyButton = null;
            sellButton = null;
            closeButton = null;
            beButton = null;
            trailButton = null;
            uiRoot = null;
            chartGrid = null;
            uiLoaded = false;
        }

        public void UpdateUiState()
        {
            if (host.ChartControl == null || !uiLoaded)
                return;

            host.UiBeginInvoke(() =>
            {
                host.SafeExecuteUI("UpdateUiState", () =>
                {
                    if (buyButton != null)
                        buyButton.Opacity = blinkBuy ? (blinkOn ? 1 : 0.55) : 1;
                    if (sellButton != null)
                        sellButton.Opacity = blinkSell ? (blinkOn ? 1 : 0.55) : 1;
                    if (host.ShouldLogDebugBlink())
                        host.Print($"{host.Prefix("DEBUG")} UpdateUiState: blinkBuy={blinkBuy} blinkSell={blinkSell} phase={(blinkOn ? "on" : "off")}");
                    if (closeButton != null)
                    {
                        closeButton.IsEnabled = host.IsArmed || host.HasPosition || host.HasPendingEntry;
                        closeButton.IsHitTestVisible = true;
                    }
                    if (trailButton != null)
                    {
                        trailButton.IsEnabled = host.HasPosition && host.HasStopOrder;
                        trailButton.IsHitTestVisible = true;
                    }
                    if (beButton != null)
                    {
                        beButton.IsEnabled = host.HasPosition && host.HasStopOrder;
                        beButton.IsHitTestVisible = true;
                    }

                    UpdateArmButtonsUI();
                });
            });
        }

        private void UpdateArmButtonsUI()
        {
            if (host.ChartControl == null || !uiLoaded)
                return;

            host.UiBeginInvoke(() =>
            {
                host.SafeExecuteUI("UpdateArmButtonsUI", () =>
                {
                    blinkBuy = host.IsArmedLong;
                    blinkSell = host.IsArmedShort;
                    if (host.ShouldLogDebugBlink())
                        host.Print($"{host.Prefix("DEBUG")} UpdateArmButtonsUI: blinkBuy={blinkBuy} blinkSell={blinkSell} phase={(blinkOn ? "on" : "off")} btnNull buy:{buyButton == null} sell:{sellButton == null}");
                    if (buyButton != null)
                        buyButton.Content = host.IsArmedLong ? "BUY ARMED" : "BUY";
                    if (sellButton != null)
                        sellButton.Content = host.IsArmedShort ? "SELL ARMED" : "SELL";
                    if (buyButton != null)
                        buyButton.Opacity = blinkBuy ? (blinkOn ? 1 : 0.55) : 1;
                    if (sellButton != null)
                        sellButton.Opacity = blinkSell ? (blinkOn ? 1 : 0.55) : 1;
                });
            });
        }

        public void StartBlinkTimer()
        {
            host.UiInvoke(() =>
            {
                if (blinkTimer != null || host.ChartControl == null || host.ChartControl.Dispatcher == null)
                    return;

                host.SetMilestone("StartBlinkTimer");
                blinkTimer = new DispatcherTimer(DispatcherPriority.Normal, host.ChartControl.Dispatcher)
                {
                    Interval = TimeSpan.FromMilliseconds(500)
                };
                blinkTickHandler = (s, e) =>
                {
                    blinkTickCounter++;
                    host.SafeExecuteUI("BlinkTimer", () =>
                    {
                        if (!host.IsArmed)
                            return;

                        blinkOn = !blinkOn;
                        if (host.DebugBlinkEnabled && blinkTickCounter % 10 == 0)
                        {
                            host.Print($"{host.Prefix("DEBUG")} Blink tick #{blinkTickCounter} flags: buy={blinkBuy} sell={blinkSell} phase={(blinkOn ? "on" : "off")} btns null? buy:{buyButton == null} sell:{sellButton == null}");
                        }
                        UpdateUiState();
                    });
                };
                blinkTimer.Tick += blinkTickHandler;
                blinkTimer.Start();
                host.LogInfo("Blink timer started");
            });
        }

        public void StopBlinkTimer()
        {
            if (blinkTimer != null && (host.ChartControl == null || host.ChartControl.Dispatcher == null || host.ChartControl.Dispatcher.HasShutdownStarted))
            {
                DispatcherTimer timerRef = blinkTimer;
                EventHandler tickRef = blinkTickHandler;
                Dispatcher timerDispatcher = timerRef.Dispatcher;
                bool stoppedOnOwner = false;
                if (timerDispatcher != null && !timerDispatcher.HasShutdownStarted)
                {
                    try
                    {
                        Action stopAction = () =>
                        {
                            try
                            {
                                if (tickRef != null)
                                    timerRef.Tick -= tickRef;
                            }
                            catch (Exception)
                            {
                                // Best-effort teardown on timer owner dispatcher.
                            }
                            try
                            {
                                timerRef.Stop();
                            }
                            catch (Exception)
                            {
                                // Best-effort teardown on timer owner dispatcher.
                            }
                        };

                        if (timerDispatcher.CheckAccess())
                            stopAction();
                        else
                            timerDispatcher.Invoke(stopAction);

                        stoppedOnOwner = true;
                    }
                    catch (Exception)
                    {
                        stoppedOnOwner = false;
                    }
                }
                if (!stoppedOnOwner)
                {
                    try
                    {
                        if (tickRef != null)
                            timerRef.Tick -= tickRef;
                    }
                    catch (Exception)
                    {
                        // No dispatcher available; best-effort local detach only.
                    }
                    try
                    {
                        timerRef.Stop();
                    }
                    catch (Exception)
                    {
                        // No dispatcher available; best-effort local stop only.
                    }
                }
                blinkTimer = null;
                blinkTickHandler = null;
                ResetBlinkState();
                return;
            }

            host.UiInvoke(() =>
            {
                host.SafeExecuteUI("StopBlinkTimer", () =>
                {
                    if (blinkTimer == null)
                        return;

                    if (blinkTickHandler != null)
                        blinkTimer.Tick -= blinkTickHandler;
                    blinkTimer.Stop();
                    blinkTimer = null;
                    blinkTickHandler = null;
                    ResetBlinkState();
                    host.SetMilestone("StopBlinkTimer");
                    host.LogInfo("Blink timer stopped");
                });
            });
        }

        private void ResetBlinkState()
        {
            blinkOn = false;
            blinkBuy = false;
            blinkSell = false;
            blinkTickCounter = 0;
        }
    }

    public class RiskRay : Strategy, IRiskRayPanelHost
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

        private enum OrderFlowEventKind
        {
            ConfirmClicked,
            EntryFullyFilled,
            OrderRejected,
            StopDragged,
            TargetDragged,
            BeClicked,
            TrailClicked,
            CloseClicked,
            PositionFlat
        }

        private enum OrderFlowCommandKind
        {
            SubmitEntry,
            SubmitExitBracket,
            ChangeStop,
            ChangeTarget,
            CleanupAfterError,
            ResetAndFlatten,
            NoOp
        }

        private sealed class OrderFlowEvent
        {
            public OrderFlowEventKind Kind;
            public string Fingerprint;
            public string Scope;
            public string Reason;
            public OrderAction EntryAction;
            public string EntryName;
            public int Quantity;
            public double StopPrice;
            public double TargetPrice;
            public Order Order;
            public int OrderQuantity;
            public double LimitPrice;
            public double StopOrderPrice;
        }

        private sealed class OrderFlowCommand
        {
            public OrderFlowCommandKind Kind;
            public string Fingerprint;
            public TimeSpan DedupeTtl;
            public Func<RiskRay, bool> Execute;
        }

        private sealed class OrderModifyGate
        {
            public OrderModifyGate(string label)
            {
                Label = label;
            }

            public string Label;
            public bool Pending;
            public string LastOrderId;
            public double LastRequestedLimit = double.NaN;
            public double LastRequestedStop = double.NaN;
            public DateTime LastRequestedUtc = DateTime.MinValue;
            public DateTime LastSubmittedUtc = DateTime.MinValue;
            public bool HasQueuedRequest;
            public int QueuedQuantity;
            public double QueuedLimit = double.NaN;
            public double QueuedStop = double.NaN;
            public string QueuedScope;
            public string LastBlockedReason;
            public DateTime LastBlockedUtc = DateTime.MinValue;
        }

        // WPF panel wrapper for manual interaction and blink feedback.
        private RiskRayPanel panel;

        // ARMED state flags that gate line movement and order submission.
        private ArmDirection armedDirection = ArmDirection.None;
        private bool isArmed;
        private bool hasPendingEntry;

        // Tracked prices for sizing and HUD labels.
        private double entryPrice;
        private double stopPrice;
        private double targetPrice;
        private double avgEntryPrice;
        private double lastEntryFillPrice;
        private bool entryFillConfirmed;

        // Unmanaged order handles and OCO group identifier (OrderTagPrefix invariant).
        private Order entryOrder;
        private Order stopOrder;
        private Order targetOrder;
        private string currentOco;
        private bool isExiting;
        private string exitOrderId;
        private DateTime exitOrderStartedUtc = DateTime.MinValue;
        private readonly OrderModifyGate stopModifyGate = new OrderModifyGate("RR_SL");
        private readonly OrderModifyGate targetModifyGate = new OrderModifyGate("RR_TP");
        private const int OrderModifyThrottleMs = 350;
        private int nextTradeCycleId = 1;
        private int activeTradeCycleId;

        // Per-event throttles to avoid noisy logs while dragging/clamping/sizing.
        private DateTime lastClampLogTime = DateTime.MinValue;
        private DateTime lastDebugLogTime = DateTime.MinValue;
        private DateTime lastQtyBlockLogTime = DateTime.MinValue;
        private DateTime lastEntryLineLogTime = DateTime.MinValue;
        private DateTime lastConfirmEntryUtc = DateTime.MinValue;
        private string lastConfirmFingerprint = null;
        private DateTime lastConfirmFingerprintUtc = DateTime.MinValue;
        private const int EntryLineLogThrottleMs = 500;

        // Chart attachment state and suppression flags used while programmatically moving lines.
        private bool suppressLineEvents;

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
        private DateTime lastDebugBlinkLogTime = DateTime.MinValue;
        private DateTime lastMouseDragPulseUtc = DateTime.MinValue;
        private DateTime lastUserInteractionUtc = DateTime.MinValue;
        private const int MouseDragPulseThrottleMs = 20;
        private const int UserInteractionGraceMs = 200;
        private const int DebugBlinkThrottleMs = 500;
        private DateTime lastLabelRefreshLogTime = DateTime.MinValue;
        private bool chartEventsAttached;
        private ChartControl attachedChartControl;
        private bool chartDetachObserved;
        private DateTime chartDetachObservedTime = DateTime.MinValue;
        private string chartDetachObservedContext;
        private bool uiCleanupPending;
        private DateTime uiCleanupPendingSinceUtc = DateTime.MinValue;
        private string uiCleanupPendingReason;
        // Dialog/blink/self-check guards that throttle popups and enforce safety invariants.
        private bool isBeDialogOpen;
        private DateTime lastBeDialogTime = DateTime.MinValue;
        private bool selfCheckDone;
        private bool selfCheckFailed;
        private string selfCheckReason;
        private bool selfCheckDialogShown;
        private DateTime lastCleanupLogTime = DateTime.MinValue;
        private bool uiUnavailablePermanently;
        private DateTime lastUiFailureLogTime = DateTime.MinValue;
        private const int UiFailureLogThrottleMs = 2000;
        private bool receivedMarketDataThisSession;
        private double cachedTickSize;
        private string lastMilestone;
        private DateTime lastMilestoneTime = DateTime.MinValue;
        private int fatalCount;
        private bool fatalNotified;
        private DateTime lastFatalGuardLogTime = DateTime.MinValue;
        private const int FatalGuardLogThrottleMs = 2000;
        private DateTime lastHotFuseLogTime = DateTime.MinValue;
        private const int HotFuseLogThrottleMs = 2000;
        private DateTime lastHudMessageTime = DateTime.MinValue;
        private readonly string instanceId = Guid.NewGuid().ToString("N").Substring(0, 6);
        private int stateChangeSeq = 0;
        private bool terminatedCleanupDone;
        private bool terminationSummaryLogged;
        private const int TerminationEventBufferSize = 50;
        private readonly Queue<string> terminationEvents = new Queue<string>();
        private readonly object terminationEventLock = new object();
        private string lastUiEvent;
        private string lastOrderAction;
        private Exception lastException;
        private string lastExceptionScope;
        private DateTime lastExceptionTime = DateTime.MinValue;
        private DateTime lastCriticalExceptionTime = DateTime.MinValue;
        private string lastCriticalExceptionScope;
        private State lastStateTransition;
        private DateTime lastStateTransitionTime = DateTime.MinValue;
        private State lastNonTerminatedState;
        private DateTime lastNonTerminatedStateTime = DateTime.MinValue;
        private bool bracketIncompleteHandled;
        private DateTime lastSizingDebugLogTime = DateTime.MinValue;
        private int lastSizingDebugQty = int.MinValue;
        private double lastSizingDebugStopTicks = double.NaN;
        private double lastSizingDebugTargetTicks = double.NaN;
        private double lastSizingDebugEntryRef = double.NaN;
        private bool forceSizingDebugLog;
        private const int SizingDebugThrottleMs = 250;
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
        private readonly Dictionary<string, DateTime> orderFlowDedupByFingerprintUtc = new Dictionary<string, DateTime>();
        private DateTime lastOrderFlowDedupPruneUtc = DateTime.MinValue;
        private DateTime lastUiAttachAttemptUtc = DateTime.MinValue;
        private const int UiAttachRetryMs = 1000;
        private DateTime lastStopUiRefreshUtc = DateTime.MinValue;
        private const int StopUiRefreshThrottleMs = 100;
        private string lastTerminationCoreCleanup = "n/a";
        private string lastTerminationUiCleanup = "n/a";
        private string lastUiStateSignature;
        private DateTime lastUiStateLogTime = DateTime.MinValue;
        private const int UiStateLogHeartbeatMs = 5000;
        private const int ModifyBlockLogThrottleMs = 600;
        private DateTime lastFailSafeFlattenAttemptUtc = DateTime.MinValue;
        private bool failSafeFlattenAttemptedThisEpisode;
        private DateTime lastFailSafeFlattenThrottleLogUtc = DateTime.MinValue;
        private const int FailSafeFlattenThrottleMs = 1000;

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
        [Display(Name = "VerboseSizingDebug", Order = 19, GroupName = "Debug")]
        public bool VerboseSizingDebug { get; set; }

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
            SafeExecute("OnStateChange", () =>
            {
                stateChangeSeq++;
                RecordStateTransition(State);
                if (LogLevelSetting == LogLevelOption.Debug && State != State.Terminated)
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
                    VerboseSizingDebug = false;
                    OrderTagPrefix = "RR_";
                    LabelOffsetMode = LabelOffsetModeOption.Legacy_TicksMax;
                    NotificationMode = NotificationModeOption.MessageBox;
                    receivedMarketDataThisSession = false;
                    cachedTickSize = 0;
                    lastMilestone = null;
                    lastMilestoneTime = DateTime.MinValue;
                    fatalCount = 0;
                    uiUnavailablePermanently = false;
                    lastUiFailureLogTime = DateTime.MinValue;
                    fatalNotified = false;
                    lastFatalGuardLogTime = DateTime.MinValue;
                    terminatedCleanupDone = false;
                    terminationSummaryLogged = false;
                    lock (terminationEventLock)
                    {
                        terminationEvents.Clear();
                    }
                    lastUiEvent = null;
                    lastOrderAction = null;
                    lastException = null;
                    lastExceptionScope = null;
                    lastExceptionTime = DateTime.MinValue;
                    lastCriticalExceptionTime = DateTime.MinValue;
                    lastCriticalExceptionScope = null;
                    lastStateTransition = State.SetDefaults;
                    lastStateTransitionTime = DateTime.MinValue;
                    lastNonTerminatedState = State.SetDefaults;
                    lastNonTerminatedStateTime = DateTime.MinValue;
                    panel = null;
                    bracketIncompleteHandled = false;
                    forceSizingDebugLog = false;
                    lastSizingDebugLogTime = DateTime.MinValue;
                    lastSizingDebugQty = int.MinValue;
                    lastSizingDebugStopTicks = double.NaN;
                    lastSizingDebugTargetTicks = double.NaN;
                    lastSizingDebugEntryRef = double.NaN;
                    lastEntryFillPrice = 0;
                    entryFillConfirmed = false;
                    userAdjustedStopWhileArmed = false;
                    userAdjustedTargetWhileArmed = false;
                    armedStopOffsetTicks = 0;
                    armedTargetOffsetTicks = 0;
                    dragFinalizePending = false;
                    lastUserInteractionUtc = DateTime.MinValue;
                    chartDetachObserved = false;
                    chartDetachObservedTime = DateTime.MinValue;
                    chartDetachObservedContext = null;
                    uiCleanupPending = false;
                    uiCleanupPendingSinceUtc = DateTime.MinValue;
                    uiCleanupPendingReason = null;
                    orderFlowDedupByFingerprintUtc.Clear();
                    lastOrderFlowDedupPruneUtc = DateTime.MinValue;
                    isExiting = false;
                    exitOrderId = null;
                    exitOrderStartedUtc = DateTime.MinValue;
                    ResetModifyGate(stopModifyGate);
                    ResetModifyGate(targetModifyGate);
                    nextTradeCycleId = 1;
                    activeTradeCycleId = 0;
                    lastUiAttachAttemptUtc = DateTime.MinValue;
                    lastStopUiRefreshUtc = DateTime.MinValue;
                    lastTerminationCoreCleanup = "n/a";
                    lastTerminationUiCleanup = "n/a";
                    attachedChartControl = null;
                    lastUiStateSignature = null;
                    lastUiStateLogTime = DateTime.MinValue;
                    lastFailSafeFlattenAttemptUtc = DateTime.MinValue;
                    failSafeFlattenAttemptedThisEpisode = false;
                    lastFailSafeFlattenThrottleLogUtc = DateTime.MinValue;
                }
                else if (State == State.Configure)
                {
                    tags = null;
                    sizing = null;
                    chartLines = null;
                    hud = null;
                    panel = new RiskRayPanel(this);
                }
                else if (State == State.Historical)
                {
                    if (LogLevelSetting == LogLevelOption.Debug)
                        Print($"{Prefix("TRACE")} State.Historical begin");
                    TryMarkRunningInstance();
                    SetMilestone("Historical");
                    SafeExecuteUI("BuildUi", BuildUi);
                    SafeExecuteUI("AttachChartEvents", AttachChartEvents);
                    if (LogLevelSetting == LogLevelOption.Debug)
                        Print($"{Prefix("TRACE")} State.Historical end");
                }
                else if (State == State.Realtime)
                {
                    if (LogLevelSetting == LogLevelOption.Debug)
                        Print($"{Prefix("TRACE")} State.Realtime begin");
                    TryMarkRunningInstance();
                    SetMilestone("Realtime");
                    RunSelfCheckOnce();
                    SafeExecuteUI("StartBlinkTimer", StartBlinkTimer);
                    SafeExecuteUI("AttachChartEvents", AttachChartEvents);
                    if (LogLevelSetting == LogLevelOption.Debug)
                        Print($"{Prefix("TRACE")} State.Realtime end");
                }
                else if (State == State.Terminated)
                {
                    bool chartNull = ChartControl == null;
                    Dispatcher stateDispatcher = ChartControl?.Dispatcher;
                    bool dispatcherNull = stateDispatcher == null;
                    bool dispatcherShutdown = !dispatcherNull && stateDispatcher.HasShutdownStarted;
                    bool dispatcherOk = CanTouchUi();
                    bool uiAvailable = !chartNull && dispatcherOk;
                    string cleanupMode = "AlreadyCompleted";
                    bool cleanupAttempted = false;
                    bool cleanupFailed = false;
                    bool precededByTransition = WasRecentStateTransition(State.Configure, State.DataLoaded, State.Historical, State.Realtime);
                    string reason = ResolveTerminationReason(chartNull, dispatcherOk, precededByTransition);
                    SetMilestone("Terminated-Start");
                    try
                    {
                        if (!terminatedCleanupDone)
                        {
                            cleanupAttempted = true;
                            RunTerminationCoreCleanup();
                            lastTerminationCoreCleanup = "done";
                            if (uiAvailable)
                            {
                                RunTerminationUiCleanup();
                                lastTerminationUiCleanup = "done";
                            }
                            else
                            {
                                LogUiAvailabilityOnce("TerminationCleanup", chartNull, dispatcherOk, dispatcherNull, dispatcherShutdown);
                                // UI unavailable at termination: release references now and skip UI-bound cleanup safely.
                                CleanupStateWithoutUi();
                                uiCleanupPending = false;
                                uiCleanupPendingSinceUtc = DateTime.MinValue;
                                uiCleanupPendingReason = null;
                                lastTerminationUiCleanup = "deferred";
                                uiUnavailablePermanently = true;
                            }
                            terminatedCleanupDone = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        cleanupFailed = true;
                        LogFatal("OnStateChange.Terminated", ex);
                    }
                    finally
                    {
                        if (cleanupAttempted)
                            cleanupMode = cleanupFailed
                                ? $"core={lastTerminationCoreCleanup}|ui=failed"
                                : $"core={lastTerminationCoreCleanup}|ui={lastTerminationUiCleanup}";
                        if (!terminationSummaryLogged)
                        {
                            string level = fatalError ? "WARN" : "INFO";
                            Print($"{Prefix(level)} Terminated reason={reason} coreCleanup={lastTerminationCoreCleanup} uiCleanup={lastTerminationUiCleanup} chartNull={chartNull} dispatcherOk={dispatcherOk} fatal={fatalError}");
                            if (fatalError && !string.IsNullOrEmpty(fatalErrorMessage))
                                Print($"{Prefix("WARN")} Terminated detail={fatalErrorMessage}");
                            string snapshot = BuildTerminationSnapshot(reason, cleanupMode, chartNull, dispatcherOk, precededByTransition);
                            Print($"{Prefix(level)} Terminated snapshot:\n{snapshot}");
                            terminationSummaryLogged = true;
                        }
                    }
                }
            });
        }

        private void TryMarkRunningInstance()
        {
            if (isRunningInstance)
                return;
            Dispatcher dispatcher = ChartControl?.Dispatcher;
            if (ChartControl != null && dispatcher != null && !dispatcher.HasShutdownStarted)
            {
                isRunningInstance = true;
                if (LogLevelSetting == LogLevelOption.Debug)
                    Print($"{Prefix("TRACE")} RUNNING_INSTANCE chartAttached=True instrument={Instrument?.FullName}");
            }
        }

        private string ResolveTerminationReason(bool chartNull, bool dispatcherOk, bool precededByTransition)
        {
            if (lastCriticalExceptionTime != DateTime.MinValue
                && (DateTime.Now - lastCriticalExceptionTime).TotalSeconds <= 10)
                return "UnhandledExceptionSuspected";
            if (lastException != null)
                return "UnhandledExceptionSuspected";

            bool earlyLifecycle = lastNonTerminatedState == State.Configure || lastNonTerminatedState == State.DataLoaded;
            if (chartNull || chartDetachObserved)
                return earlyLifecycle ? "StrategyDisabledBeforeChartAttach" : "ChartDetachedOrClosed";
            if (!dispatcherOk)
                return earlyLifecycle ? "StrategyDisabledBeforeUiAttach" : "UiDispatcherUnavailable";
            if (precededByTransition)
                return "StrategyDisabledOrReloaded";
            if (earlyLifecycle)
                return "StartupAbortedBeforeAttach";
            return "StrategyTerminatedNoException";
        }

        private bool CanTouchUi()
        {
            Dispatcher dispatcher = ChartControl?.Dispatcher;
            return ChartControl != null && dispatcher != null && !dispatcher.HasShutdownStarted;
        }

        private void MarkChartDetachObserved(string context, bool chartNull, bool dispatcherOk)
        {
            chartDetachObserved = true;
            chartDetachObservedTime = DateTime.Now;
            chartDetachObservedContext = $"{context} chartNull={chartNull} dispatcherOk={dispatcherOk}";
        }

        private void MarkUiCleanupPending(string reason)
        {
            if (State == State.Terminated)
                return;
            uiCleanupPending = true;
            if (uiCleanupPendingSinceUtc == DateTime.MinValue)
                uiCleanupPendingSinceUtc = DateTime.UtcNow;
            if (!string.IsNullOrWhiteSpace(reason))
                uiCleanupPendingReason = reason;
        }

        private void TryFinalizePendingUiCleanup(string source)
        {
            if (!uiCleanupPending)
                return;

            bool chartNull = ChartControl == null;
            bool dispatcherOk = CanTouchUi();
            if (chartNull || !dispatcherOk)
                return;

            try
            {
                DisposeUi(false);
                uiCleanupPending = false;
                uiCleanupPendingSinceUtc = DateTime.MinValue;
                uiCleanupPendingReason = null;
                LogInfo($"UI cleanup completed ({source})");
            }
            catch (Exception ex)
            {
                LogUiError("TryFinalizePendingUiCleanup", ex);
            }
        }

        private void DetachChartEventSubscriptions(ChartControl source)
        {
            if (source == null)
                return;

            source.PreviewMouseLeftButtonUp -= ChartControl_PreviewMouseLeftButtonUp;
            source.MouseLeftButtonUp -= ChartControl_MouseLeftButtonUp;
            source.MouseLeave -= ChartControl_MouseLeave;
            source.MouseMove -= ChartControl_MouseMove;
        }

        private void DetachChartEventHandlers(ChartControl source, bool log)
        {
            if (source == null)
                return;

            DetachChartEventSubscriptions(source);
            if (ReferenceEquals(attachedChartControl, source))
            {
                chartEventsAttached = false;
                attachedChartControl = null;
            }
            if (log)
            {
                SetMilestone("DetachChartEvents");
                LogInfo("Chart events detached");
            }
        }

        private void TryDetachChartEventsWithoutDispatcher()
        {
            ChartControl source = attachedChartControl;
            if (source == null)
            {
                chartEventsAttached = false;
                attachedChartControl = null;
                return;
            }

            Dispatcher dispatcher = source.Dispatcher;
            chartEventsAttached = false;
            attachedChartControl = null;
            if (dispatcher == null || dispatcher.HasShutdownStarted)
            {
                MarkChartDetachObserved("DetachChartEvents.NoDispatcher", false, false);
                return;
            }

            try
            {
                Action detach = () =>
                {
                    try
                    {
                        DetachChartEventSubscriptions(source);
                    }
                    catch (Exception)
                    {
                        // Best-effort detach while dispatcher is in flux.
                    }
                };

                if (dispatcher.CheckAccess())
                    detach();
                else
                    dispatcher.InvokeAsync(detach, DispatcherPriority.Send);
            }
            catch (Exception ex)
            {
                LogUiError("DetachChartEvents.NoDispatcherInvoke", ex);
            }
        }

        private void DetachChartEventsCore()
        {
            ChartControl source = attachedChartControl;
            if (source == null)
            {
                chartEventsAttached = false;
                attachedChartControl = null;
                return;
            }

            Dispatcher sourceDispatcher = source.Dispatcher;
            chartEventsAttached = false;
            attachedChartControl = null;
            if (sourceDispatcher == null || sourceDispatcher.HasShutdownStarted)
            {
                MarkChartDetachObserved("DetachChartEventsCore.DispatcherUnavailable", false, false);
                return;
            }

            try
            {
                Action detach = () =>
                {
                    try
                    {
                        DetachChartEventSubscriptions(source);
                    }
                    catch (Exception ex)
                    {
                        LogUiError("DetachChartEventsCore.Detach", ex);
                    }
                };
                if (sourceDispatcher.CheckAccess())
                    detach();
                else
                    sourceDispatcher.InvokeAsync(detach, DispatcherPriority.Send);
            }
            catch (Exception ex)
            {
                LogUiError("DetachChartEventsCore", ex);
            }
        }

        private void ReleaseUiReferencesNoDispatcher()
        {
            MarkUiCleanupPending("ReleaseUiReferencesNoDispatcher");
            TryDetachChartEventsWithoutDispatcher();
            panel?.ReleaseUiReferencesNoDispatcher();
        }

        private void CleanupStateWithoutUi()
        {
            ReleaseUiReferencesNoDispatcher();
            entryLineDirty = false;
            stopLineDirty = false;
            targetLineDirty = false;
            ResetLabelTrackingCaches();
        }

        private void RunTerminationCoreCleanup()
        {
            StopBlinkTimer();
            DetachChartEventsCore();
            entryLineDirty = false;
            stopLineDirty = false;
            targetLineDirty = false;
            isDraggingStop = false;
            isDraggingTarget = false;
            dragFinalizePending = false;
            ResetLabelTrackingCaches();
        }

        private void RunTerminationUiCleanup()
        {
            DisposeUi(true);
            RemoveAllDrawObjects();
            uiCleanupPending = false;
            uiCleanupPendingSinceUtc = DateTime.MinValue;
            uiCleanupPendingReason = null;
        }

        // Main tick handler in realtime: keeps entry line following bid/ask while armed and refreshes HUD without changing user-placed stops/targets.
        protected override void OnBarUpdate()
        {
            SafeExecute("OnBarUpdate", () =>
            {
                EnsureHelpers();
                if (State != State.Realtime)
                    return;
                EnsureUiAttachedRuntime("OnBarUpdate");
                FlushQueuedOrderModifyPulse();

                // Fallback pulse when MarketData is unavailable; skips if realtime market data already observed.
                if (receivedMarketDataThisSession)
                    return;

                // Only entry line follows price while armed; stops/targets stay where user placed them.
                UpdateEntryLineFromMarket();
                ApplyLineUpdates();
                UpdateLabelsOnly();
            });
        }

        // Responds to market data for smoother drag/line updates (especially in playback), keeping stops/targets in sync while respecting clamps.
        protected override void OnMarketData(MarketDataEventArgs marketDataUpdate)
        {
            SafeExecute("OnMarketData", () =>
            {
                EnsureHelpers();
                if (State != State.Realtime)
                    return;
                EnsureUiAttachedRuntime("OnMarketData");
                FlushQueuedOrderModifyPulse();

                receivedMarketDataThisSession = true;

                ProcessLineDrag(RiskRayChartLines.LineKind.Stop, ref stopPrice, true);
                ProcessLineDrag(RiskRayChartLines.LineKind.Target, ref targetPrice, false);
                UpdateEntryLineFromMarket();
                ApplyLineUpdates();
                UpdateLabelsOnly();
            });
        }

        // Tracks unmanaged orders, fills, and rejects; updates local handles and resets ARMED state on entry fills.
        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPriceParam, int quantity, int filled, double averageFillPrice, OrderState orderState, DateTime time, ErrorCode error, string nativeError)
        {
            SafeExecute("OnOrderUpdate", () =>
            {
                EnsureHelpers();
                if (order == null)
                    return;

                LogOrderUpdateTrace(order, orderState, filled, averageFillPrice, error, nativeError);

                if (order.Name == tags.EntrySignalLong || order.Name == tags.EntrySignalShort)
                    entryOrder = order;
                else if (order.Name == tags.StopSignal)
                    stopOrder = order;
                else if (order.Name == tags.TargetSignal)
                    targetOrder = order;
                else if (LogLevelSetting == LogLevelOption.Debug
                    && !string.IsNullOrEmpty(order.Name)
                    && order.Name.IndexOf("SL", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    LogDebug($"Stop signal name mismatch: expected={tags.StopSignal} actual={order.Name} orderId={order.OrderId}");
                }

                MaybeRefreshUiForStopOrderUpdate(order);

                UpdateOrderModifyGateFromUpdate(order);
                UpdateExitLockFromOrderUpdate(order);

                bool isEntryOrder = order.Name == tags.EntrySignalLong || order.Name == tags.EntrySignalShort;
                if (isEntryOrder)
                {
                    if (order.OrderState == OrderState.Submitted)
                    {
                        EnsureTradeCycle("EntrySubmitted");
                        RecordOrderAction($"Entry submitted qty={order.Quantity}");
                        LogInfo($"Entry submitted: qty={order.Quantity}");
                    }
                    else if ((order.OrderState == OrderState.Working || order.OrderState == OrderState.PartFilled) && order.Filled > 0)
                    {
                        RecordOrderAction($"Entry PartFilled filled={order.Filled} qty={order.Quantity} avg={order.AverageFillPrice:F2}");
                        LogInfo($"Entry PartFilled: filled={order.Filled} qty={order.Quantity} avg={order.AverageFillPrice:F2} (bracket deferred until full fill)");
                    }
                }

                if (order.OrderState == OrderState.Filled && (order.Name == tags.EntrySignalLong || order.Name == tags.EntrySignalShort))
                {
                    avgEntryPrice = order.AverageFillPrice;
                    double fillPrice = order.AverageFillPrice;
                    if (fillPrice <= 0 && Position != null && Position.AveragePrice > 0)
                        fillPrice = Position.AveragePrice;
                    if (fillPrice > 0)
                        avgEntryPrice = fillPrice;
                    lastEntryFillPrice = fillPrice;
                    entryFillConfirmed = fillPrice > 0;
                    hasPendingEntry = false;
                    armedDirection = ArmDirection.None;
                    isArmed = false;
                    StopBlinkTimer();
                    UpdateUiState();
                    SafeExecuteUI("EntryLineFillUpdate", () => UpdateEntryLine(lastEntryFillPrice > 0 ? lastEntryFillPrice : avgEntryPrice, "Entry fill"));
                    RecordOrderAction($"Entry filled qty={order.Quantity} avg={avgEntryPrice:F2}");
                    LogInfo($"Entry filled @ {avgEntryPrice:F2} ({order.Quantity} contracts)");

                    // Submit bracket only on full fill; exactly once per entry (idempotent: guard by no existing bracket).
                    if (stopOrder == null && targetOrder == null)
                    {
                        if (!EnsureSelfCheckPassed())
                            return;

                        int qty = order.Quantity;
                        if (qty <= 0)
                        {
                            LogInfo("Bracket skipped: entry fill qty<=0");
                            return;
                        }

                        double stop = sizing.RoundToTick(stopPrice);
                        double target = sizing.RoundToTick(targetPrice);
                        if (double.IsNaN(stop) || double.IsInfinity(stop) || stop <= 0
                            || double.IsNaN(target) || double.IsInfinity(target) || target <= 0)
                        {
                            Print($"{Prefix("WARN")} BracketPriceInvalid: stop={stop:F2} target={target:F2} -> flatten");
                            FailSafeFlatten("BracketPriceInvalid");
                            return;
                        }

                        string stopFp = RoundForFp(stop).ToString("G17", CultureInfo.InvariantCulture);
                        string targetFp = RoundForFp(target).ToString("G17", CultureInfo.InvariantCulture);
                        int did = HandleOrderFlowEvent(new OrderFlowEvent
                        {
                            Kind = OrderFlowEventKind.EntryFullyFilled,
                            Scope = "SubmitExitBracket",
                            Fingerprint = $"entryfill|{order.OrderId}|qty={qty}|stop={stopFp}|target={targetFp}",
                            EntryAction = order.OrderAction,
                            Quantity = qty,
                            StopPrice = stop,
                            TargetPrice = target
                        });
                        if (did > 0)
                        {
                            RecordOrderAction($"Bracket submitted qty={qty} SL={stop:F2} TP={target:F2}");
                            LogInfo($"Bracket submitted (full fill): qty={qty}, SL={stop:F2}, TP={target:F2}");
                        }
                    }
                    else
                    {
                        LogDebug("Bracket already submitted, skip");
                    }
                }

                if (order.OrderState == OrderState.Filled && (order.Name == tags.StopSignal || order.Name == tags.TargetSignal))
                {
                    RecordOrderAction($"Exit filled {order.Name} qty={order.Filled} avg={order.AverageFillPrice:F2}");
                    ProcessExitFill(order, null, "OnOrderUpdate");
                }

                if (order.OrderState == OrderState.Cancelled)
                {
                    RecordOrderAction($"Order cancelled {order.Name} qty={order.Quantity}");
                    if (order == entryOrder)
                        entryOrder = null;
                    if (order == stopOrder)
                        stopOrder = null;
                    if (order == targetOrder)
                        targetOrder = null;
                    if ((order.Name == tags.StopSignal || order.Name == tags.TargetSignal) && Position.MarketPosition != MarketPosition.Flat)
                        CheckBracketIntegrityAfterExitOrderState($"ExitOrder{order.OrderState}:{order.Name}");
                }

                if (order.OrderState == OrderState.Rejected)
                {
                    RecordOrderAction($"Order rejected {order.Name}: {nativeError ?? error.ToString()}");
                    LogInfo($"Order rejected ({order.Name}): {nativeError ?? error.ToString()}");
                    HandleOrderFlowEvent(new OrderFlowEvent
                    {
                        Kind = OrderFlowEventKind.OrderRejected,
                        Fingerprint = $"rejected|{order.OrderId}|{order.Name}|{error}"
                    });
                    if ((order.Name == tags.StopSignal || order.Name == tags.TargetSignal) && Position.MarketPosition != MarketPosition.Flat)
                        CheckBracketIntegrityAfterExitOrderState($"ExitOrder{order.OrderState}:{order.Name}");
                    if (Position.MarketPosition == MarketPosition.Flat)
                        EndTradeCycle($"OrderRejected:{order.Name}");
                }
            });
        }

        // Mirrors execution-level fills into avgEntryPrice and exit handling for unmanaged flow.
        protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity, MarketPosition marketPosition, string orderId, DateTime time)
        {
            SafeExecute("OnExecutionUpdate", () =>
            {
                EnsureHelpers();
                if (execution == null || execution.Order == null)
                    return;

                bool isEntryExec = execution.Order.Name == tags.EntrySignalLong || execution.Order.Name == tags.EntrySignalShort;
                if (isEntryExec)
                {
                    // Bracket is submitted only on full fill in OnOrderUpdate; do not submit or resize here.
                    double executionEntry = execution.Order.AverageFillPrice;
                    if (executionEntry <= 0 && Position != null && Position.AveragePrice > 0)
                        executionEntry = Position.AveragePrice;
                    if (executionEntry <= 0 && execution.Price > 0)
                        executionEntry = execution.Price;
                    if (executionEntry > 0)
                        avgEntryPrice = executionEntry;
                }

                bool isExitExec = execution.Order.Name == tags.StopSignal || execution.Order.Name == tags.TargetSignal;
                if (isExitExec)
                    ProcessExitFill(execution.Order, execution, "OnExecutionUpdate");

                if (!isExitExec)
                {
                    bool stopActive = IsOrderActive(stopOrder);
                    bool targetActive = IsOrderActive(targetOrder);
                    if (Position.MarketPosition != MarketPosition.Flat && (stopActive ^ targetActive))
                    {
                        if (!bracketIncompleteHandled)
                        {
                            if (fatalError)
                            {
                                bracketIncompleteHandled = true;
                                HandleBracketIncompleteDuringFatal("BracketIncomplete");
                                return;
                            }
                            LogDiagnosticSnapshot("BracketIncomplete");
                            bracketIncompleteHandled = true;
                        }
                        FailSafeFlatten("BracketIncomplete");
                    }
                }
            });
        }

        // When flat, fully reset local state and draw objects to match broker position.
        protected override void OnPositionUpdate(Position position, double averagePrice, int quantity, MarketPosition marketPosition)
        {
            SafeExecute("OnPositionUpdate", () =>
            {
                EnsureHelpers();
                if (position == null)
                    return;

                LogPositionUpdateTrace(position, quantity, marketPosition, averagePrice);

                if (marketPosition == MarketPosition.Flat)
                {
                    bracketIncompleteHandled = false;
                    lastFailSafeFlattenAttemptUtc = DateTime.MinValue;
                    failSafeFlattenAttemptedThisEpisode = false;
                    lastFailSafeFlattenThrottleLogUtc = DateTime.MinValue;
                    avgEntryPrice = 0;
                    lastEntryFillPrice = 0;
                    entryFillConfirmed = false;
                    entryOrder = null;
                    stopOrder = null;
                    targetOrder = null;
                    currentOco = null;
                    hasPendingEntry = false;
                    armedDirection = ArmDirection.None;
                    isArmed = false;
                    processedExitIds.Clear();
                    ResetModifyGate(stopModifyGate);
                    ResetModifyGate(targetModifyGate);
                    ReleaseExitLock("PositionFlat");
                    EndTradeCycle("PositionFlat");
                    RemoveAllDrawObjects();
                    UpdateUiState();
                    HandleOrderFlowEvent(new OrderFlowEvent
                    {
                        Kind = OrderFlowEventKind.PositionFlat,
                        Fingerprint = "position-flat"
                    });
                }
            });
        }

        #endregion

        #region UI Panel

        #region UI

        // Create the WPF chart-side button panel once ChartControl is available; must marshal to dispatcher to satisfy NinjaTrader UI thread rules.
        private void BuildUi()
        {
            if (panel == null)
                panel = new RiskRayPanel(this);
            bool chartNull = ChartControl == null;
            Dispatcher dispatcher = ChartControl?.Dispatcher;
            bool dispatcherNull = dispatcher == null;
            bool dispatcherShutdown = !dispatcherNull && dispatcher.HasShutdownStarted;
            bool dispatcherOk = !chartNull && !dispatcherNull && !dispatcherShutdown;
            if (chartNull || !dispatcherOk)
            {
                if (isRunningInstance)
                    LogUiAvailabilityOnce("BuildUi", chartNull, dispatcherOk, dispatcherNull, dispatcherShutdown);
                return;
            }
            panel.BuildUi();
        }

        // Tear down the WPF panel and detach events on disposal or termination to avoid stale references.
        private void DisposeUi(bool forceSync = false)
        {
            bool chartNull = ChartControl == null;
            Dispatcher dispatcher = ChartControl?.Dispatcher;
            bool dispatcherNull = dispatcher == null;
            bool dispatcherShutdown = !dispatcherNull && dispatcher.HasShutdownStarted;
            bool dispatcherOk = !chartNull && !dispatcherNull && !dispatcherShutdown;
            if (chartNull || !dispatcherOk)
            {
                if (isRunningInstance)
                    LogUiAvailabilityOnce("DisposeUi", chartNull, dispatcherOk, dispatcherNull, dispatcherShutdown);
                return;
            }
            panel?.DisposeUi(forceSync);
            DetachChartEvents();
        }

        // Dispatcher-safe UI refresh for opacity/enabled states based on ARMED and position status.
        private void UpdateUiState()
        {
            bool chartNull = ChartControl == null;
            Dispatcher dispatcher = ChartControl?.Dispatcher;
            bool dispatcherNull = dispatcher == null;
            bool dispatcherShutdown = !dispatcherNull && dispatcher.HasShutdownStarted;
            bool dispatcherOk = !chartNull && !dispatcherNull && !dispatcherShutdown;
            if (chartNull || !dispatcherOk)
            {
                if (isRunningInstance)
                    LogUiAvailabilityOnce("UpdateUiState", chartNull, dispatcherOk, dispatcherNull, dispatcherShutdown);
                return;
            }
            if (LogLevelSetting == LogLevelOption.Debug)
            {
                bool hasPosition = Position.MarketPosition != MarketPosition.Flat;
                bool hasStop = IsStopOrderUiActive(stopOrder);
                bool stopActiveAny = IsOrderActive(stopOrder);
                bool closeEnabled = isArmed || hasPosition || hasPendingEntry || entryOrder != null;
                bool beEnabled = hasPosition && hasStop;
                bool trailEnabled = hasPosition && hasStop;
                string stopState = stopOrder == null ? "null" : stopOrder.OrderState.ToString();
                LogUiStateSnapshot(closeEnabled, beEnabled, trailEnabled, hasPosition, hasStop, stopActiveAny, stopState);
            }
            panel?.UpdateUiState();
        }

        // Blink timer (500ms) drives visual feedback for ARMED state; must run on chart dispatcher to avoid cross-thread WPF access.
        private void StartBlinkTimer()
        {
            bool chartNull = ChartControl == null;
            Dispatcher dispatcher = ChartControl?.Dispatcher;
            bool dispatcherNull = dispatcher == null;
            bool dispatcherShutdown = !dispatcherNull && dispatcher.HasShutdownStarted;
            bool dispatcherOk = !chartNull && !dispatcherNull && !dispatcherShutdown;
            if (chartNull || !dispatcherOk)
            {
                if (isRunningInstance)
                    LogUiAvailabilityOnce("StartBlinkTimer", chartNull, dispatcherOk, dispatcherNull, dispatcherShutdown);
                return;
            }
            panel?.StartBlinkTimer();
        }

        // Stop and release blink timer when no longer needed.
        private void StopBlinkTimer()
        {
            bool chartNull = ChartControl == null;
            Dispatcher dispatcher = ChartControl?.Dispatcher;
            bool dispatcherNull = dispatcher == null;
            bool dispatcherShutdown = !dispatcherNull && dispatcher.HasShutdownStarted;
            bool dispatcherOk = !chartNull && !dispatcherNull && !dispatcherShutdown;
            if (chartNull || !dispatcherOk)
            {
                if (isRunningInstance)
                    LogUiAvailabilityOnce("StopBlinkTimer", chartNull, dispatcherOk, dispatcherNull, dispatcherShutdown);
                panel?.StopBlinkTimer();
                return;
            }
            panel?.StopBlinkTimer();
        }

        private void EnsureUiAttachedRuntime(string source)
        {
            DateTime nowUtc = DateTime.UtcNow;
            if (lastUiAttachAttemptUtc != DateTime.MinValue
                && (nowUtc - lastUiAttachAttemptUtc).TotalMilliseconds < UiAttachRetryMs)
                return;

            lastUiAttachAttemptUtc = nowUtc;
            bool chartNull = ChartControl == null;
            Dispatcher dispatcher = ChartControl?.Dispatcher;
            bool dispatcherNull = dispatcher == null;
            bool dispatcherShutdown = !dispatcherNull && dispatcher.HasShutdownStarted;
            bool dispatcherOk = !chartNull && !dispatcherNull && !dispatcherShutdown;
            if (chartNull || !dispatcherOk)
            {
                bool alreadyLatched = uiUnavailablePermanently;
                if (dispatcherShutdown)
                    uiUnavailablePermanently = true;
                if (alreadyLatched && (chartNull || dispatcherNull || dispatcherShutdown))
                {
                    if (chartNull || dispatcherNull || dispatcherShutdown)
                        ReleaseUiReferencesNoDispatcher();
                    return;
                }
                MarkChartDetachObserved($"EnsureUiAttachedRuntime.{source}", chartNull, dispatcherOk);
                MarkUiCleanupPending($"EnsureUiAttachedRuntime.{source}");
                if (chartNull || dispatcherNull || dispatcherShutdown)
                    ReleaseUiReferencesNoDispatcher();
                return;
            }

            TryFinalizePendingUiCleanup(source);
            chartDetachObserved = false;
            chartDetachObservedTime = DateTime.MinValue;
            chartDetachObservedContext = null;
            ChartControl currentChart = ChartControl;
            BuildUi();
            AttachChartEvents();
            if (chartEventsAttached && currentChart != null && ReferenceEquals(attachedChartControl, currentChart))
                uiUnavailablePermanently = false;
            UpdateUiState();
            if (LogLevelSetting == LogLevelOption.Debug && ShouldLogDebug())
                LogDebug($"UI attach pulse ({source}): chartNull={chartNull} dispatcherOk={dispatcherOk} eventsAttached={chartEventsAttached}");
        }

        #endregion

        #region Buttons

        // BUY uses ARMED -> CONFIRM: first click arms, second submits entry + bracket if flat and no pending entry.
        private void OnBuyClicked(object sender, RoutedEventArgs e)
        {
            TriggerCustomEvent(_ =>
            {
                RecordUiEvent("BUYARM");
                LogInfo("[UI] UserClick: BUYARM");
                forceSizingDebugLog = true;
                SafeExecuteTrade("OnBuyClicked", () =>
                {
                    if (Position.MarketPosition != MarketPosition.Flat || entryOrder != null)
                    {
                        LogInfo("[UI] BUYARM blocked: PositionActive");
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
            }, null);
        }

        // SELL mirrors BUY flow for shorts and blocks arming while any position or pending entry exists.
        private void OnSellClicked(object sender, RoutedEventArgs e)
        {
            TriggerCustomEvent(_ =>
            {
                RecordUiEvent("SELLARM");
                LogInfo("[UI] UserClick: SELLARM");
                forceSizingDebugLog = true;
                SafeExecuteTrade("OnSellClicked", () =>
                {
                    if (Position.MarketPosition != MarketPosition.Flat || entryOrder != null)
                    {
                        LogInfo("[UI] SELLARM blocked: PositionActive");
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
            }, null);
        }

        // CLOSE cancels all working orders and flattens exposure regardless of ARMED state.
        private void OnCloseClicked(object sender, RoutedEventArgs e)
        {
            TriggerCustomEvent(_ =>
            {
                RecordUiEvent("CLOSE");
                LogInfo("[UI] UserClick: CLOSE");
                forceSizingDebugLog = true;
                SafeExecuteTrade("OnCloseClicked", () =>
                {
                    LogInfo("CLOSE start");
                    Print($"{Prefix()} CLOSE click received");
                    HandleOrderFlowEvent(new OrderFlowEvent
                    {
                        Kind = OrderFlowEventKind.CloseClicked,
                        Fingerprint = $"close|UserClose|{Position.MarketPosition}|qty={Math.Abs(Position.Quantity)}",
                        Reason = "UserClose"
                    });
                    LogInfo("CLOSE end");
                });
            }, null);
        }

        // BE shifts stop to break-even (+offset) only when trade is in profit; otherwise shows throttled dialog.
        private void OnBeClicked(object sender, RoutedEventArgs e)
        {
            HandleBeClick();
        }

        // TRAIL repositions the stop relative to current market using configured offset when a working stop exists.
        private void OnTrailClicked(object sender, RoutedEventArgs e)
        {
            HandleTrailClick();
        }

        private void HandleBeClick()
        {
            TriggerCustomEvent(_ =>
            {
                RecordUiEvent("BE");
                LogInfo("[UI] UserClick: BE");
                string blockReason = GetBeClickBlockReason();
                if (!string.IsNullOrEmpty(blockReason))
                    LogInfo($"[UI] BE blocked: {blockReason}");
                forceSizingDebugLog = true;
                SafeExecuteTrade("OnBeClicked", MoveStopToBreakEven);
            }, null);
        }

        private void HandleTrailClick()
        {
            TriggerCustomEvent(_ =>
            {
                RecordUiEvent("TRAIL");
                LogInfo("[UI] UserClick: TRAIL");
                string blockReason = GetTrailClickBlockReason();
                if (!string.IsNullOrEmpty(blockReason))
                    LogInfo($"[UI] TRAIL blocked: {blockReason}");
                forceSizingDebugLog = true;
                SafeExecuteTrade("OnTrailClicked", ExecuteTrailStop);
            }, null);
        }

        private string GetBeClickBlockReason()
        {
            if (State != State.Realtime)
                return "NotRealtime";
            if (isExiting)
                return "ExitLock";
            if (Position.MarketPosition == MarketPosition.Flat)
                return "PositionFlat";
            if (!IsOrderActive(stopOrder))
                return "NoStopOrder";
            return null;
        }

        private string GetTrailClickBlockReason()
        {
            if (State != State.Realtime)
                return "NotRealtime";
            if (isExiting)
                return "ExitLock";
            if (Position.MarketPosition == MarketPosition.Flat)
                return "PositionFlat";
            if (!IsOrderActive(stopOrder))
                return "NoStopOrder";
            return null;
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
            if (panel == null || !panel.IsBlinkTimerRunning)
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
            StopBlinkTimer();
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
                forceSizingDebugLog = true;
                LogDebugDrag("DragStart SL at", snapped);
            }
            else if (!isStop && !isDraggingTarget)
            {
                isDraggingTarget = true;
                forceSizingDebugLog = true;
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
                        int did = HandleOrderFlowEvent(new OrderFlowEvent
                        {
                            Kind = OrderFlowEventKind.StopDragged,
                            Scope = "ChangeOrder-StopDrag",
                            Fingerprint = $"stopdrag|{stopOrder.OrderId}|{RoundForFp(stopPrice).ToString("G17", CultureInfo.InvariantCulture)}",
                            Order = stopOrder,
                            OrderQuantity = stopOrder.Quantity,
                            LimitPrice = stopOrder.LimitPrice,
                            StopOrderPrice = stopPrice
                        });
                        if (did > 0)
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
                if (IsOrderSafeForResize(targetOrder) && EnsureSelfCheckPassed())
                {
                    HandleOrderFlowEvent(new OrderFlowEvent
                    {
                        Kind = OrderFlowEventKind.TargetDragged,
                        Scope = "ChangeOrder-TargetDrag",
                        Fingerprint = $"targetdrag|{targetOrder.OrderId}|{RoundForFp(targetPrice).ToString("G17", CultureInfo.InvariantCulture)}",
                        Order = targetOrder,
                        OrderQuantity = targetOrder.Quantity,
                        LimitPrice = targetPrice,
                        StopOrderPrice = targetOrder.StopPrice
                    });
                }
            }

            if (clamped)
                LogClampOnce("Line clamped to stay off-market");

            UpdateLabelsOnly();
        }

        // Snap entry price to tick and mirror HUD label; used on fills and ARMED updates.
        private void UpdateEntryLine(double price, string reason)
        {
            double prevEntry = entryPrice;
            bool useFillPrice = entryFillConfirmed || (Position != null && Position.MarketPosition != MarketPosition.Flat);
            double targetPrice = useFillPrice && lastEntryFillPrice > 0 ? lastEntryFillPrice : price;
            entryPrice = sizing.RoundToTick(targetPrice);
            RiskRayHud.Snapshot snapshot = BuildHudSnapshot();
            chartLines.UpsertLine(RiskRayChartLines.LineKind.Entry, entryPrice, $"{hud.GetQtyLabel(snapshot)} ({reason})");
            if (useFillPrice && lastEntryFillPrice > 0
                && (DateTime.Now - lastEntryLineLogTime).TotalMilliseconds > EntryLineLogThrottleMs)
            {
                lastEntryLineLogTime = DateTime.Now;
                LogDebug($"ENTRY_LINE updated to avgFill={lastEntryFillPrice:F2} prev={prevEntry:F2} mp={Position.MarketPosition} qty={Position.Quantity}");
            }
        }

        #endregion
        #endregion

        #region Order Management

        #region Orders

        private int HandleOrderFlowEvent(OrderFlowEvent flowEvent)
        {
            if (flowEvent == null)
                return 0;

            List<OrderFlowCommand> commands = Decide(flowEvent);
            return ApplyOrderFlowCommands(commands);
        }

        private int ApplyOrderFlowCommands(List<OrderFlowCommand> commands)
        {
            if (commands == null || commands.Count == 0)
                return 0;

            int executed = 0;
            DateTime nowUtc = DateTime.UtcNow;
            PruneOrderFlowDedupes(nowUtc);

            foreach (OrderFlowCommand command in commands)
            {
                if (command == null)
                    continue;

                string fingerprint = command.Fingerprint;
                if (!string.IsNullOrWhiteSpace(fingerprint) && command.DedupeTtl > TimeSpan.Zero)
                {
                    DateTime lastSeenUtc;
                    if (orderFlowDedupByFingerprintUtc.TryGetValue(fingerprint, out lastSeenUtc)
                        && (nowUtc - lastSeenUtc) < command.DedupeTtl)
                    {
                        continue;
                    }
                }

                bool didRun = command.Execute != null && command.Execute(this);
                if (!didRun)
                    continue;

                executed++;
                if (!string.IsNullOrWhiteSpace(fingerprint) && command.DedupeTtl > TimeSpan.Zero)
                    orderFlowDedupByFingerprintUtc[fingerprint] = nowUtc;
            }

            return executed;
        }

        private void PruneOrderFlowDedupes(DateTime nowUtc)
        {
            if (orderFlowDedupByFingerprintUtc.Count == 0)
                return;
            if (lastOrderFlowDedupPruneUtc != DateTime.MinValue && (nowUtc - lastOrderFlowDedupPruneUtc).TotalSeconds < 5)
                return;

            lastOrderFlowDedupPruneUtc = nowUtc;
            List<string> expired = null;
            foreach (KeyValuePair<string, DateTime> kv in orderFlowDedupByFingerprintUtc)
            {
                if ((nowUtc - kv.Value).TotalMinutes <= 2)
                    continue;
                if (expired == null)
                    expired = new List<string>();
                expired.Add(kv.Key);
            }

            if (expired == null)
                return;
            for (int i = 0; i < expired.Count; i++)
                orderFlowDedupByFingerprintUtc.Remove(expired[i]);
        }

        private List<OrderFlowCommand> Decide(OrderFlowEvent flowEvent)
        {
            List<OrderFlowCommand> commands = new List<OrderFlowCommand>();
            if (flowEvent == null)
                return commands;

            switch (flowEvent.Kind)
            {
                case OrderFlowEventKind.ConfirmClicked:
                    commands.Add(new OrderFlowCommand
                    {
                        Kind = OrderFlowCommandKind.SubmitEntry,
                        Fingerprint = flowEvent.Fingerprint,
                        DedupeTtl = TimeSpan.FromMilliseconds(1500),
                        Execute = strategy =>
                        {
                            bool didRun = false;
                            strategy.SafeExecuteTrade(flowEvent.Scope ?? "SubmitOrders", () =>
                            {
                                didRun = true;
                                strategy.LogDebug($"OrderSubmit signal={flowEvent.EntryName} qty={flowEvent.Quantity} cycle={strategy.activeTradeCycleId} scope={flowEvent.Scope ?? "SubmitOrders"}");
                                strategy.entryOrder = strategy.SubmitOrderUnmanaged(0, flowEvent.EntryAction, OrderType.Market, flowEvent.Quantity, 0, 0, null, flowEvent.EntryName);
                                strategy.stopOrder = null;
                                strategy.targetOrder = null;
                                strategy.currentOco = null;
                            });
                            return didRun;
                        }
                    });
                    break;

                case OrderFlowEventKind.EntryFullyFilled:
                    commands.Add(new OrderFlowCommand
                    {
                        Kind = OrderFlowCommandKind.SubmitExitBracket,
                        Fingerprint = flowEvent.Fingerprint,
                        DedupeTtl = TimeSpan.FromMilliseconds(2000),
                        Execute = strategy =>
                        {
                            bool didRun = false;
                            strategy.SafeExecuteTrade(flowEvent.Scope ?? "SubmitExitBracket", () =>
                            {
                                didRun = true;
                                OrderAction stopAction = flowEvent.EntryAction == OrderAction.Buy ? OrderAction.Sell : OrderAction.BuyToCover;
                                strategy.currentOco = Guid.NewGuid().ToString("N");
                                strategy.LogDebug($"OrderSubmit signal={strategy.tags.StopSignal} qty={flowEvent.Quantity} stop={flowEvent.StopPrice:F2} oco={strategy.currentOco} cycle={strategy.activeTradeCycleId}");
                                strategy.stopOrder = strategy.SubmitOrderUnmanaged(0, stopAction, OrderType.StopMarket, flowEvent.Quantity, 0, flowEvent.StopPrice, strategy.currentOco, strategy.tags.StopSignal);
                                strategy.LogDebug($"OrderSubmit signal={strategy.tags.TargetSignal} qty={flowEvent.Quantity} limit={flowEvent.TargetPrice:F2} oco={strategy.currentOco} cycle={strategy.activeTradeCycleId}");
                                strategy.targetOrder = strategy.SubmitOrderUnmanaged(0, stopAction, OrderType.Limit, flowEvent.Quantity, flowEvent.TargetPrice, 0, strategy.currentOco, strategy.tags.TargetSignal);
                            });
                            return didRun;
                        }
                    });
                    break;

                case OrderFlowEventKind.OrderRejected:
                    commands.Add(new OrderFlowCommand
                    {
                        Kind = OrderFlowCommandKind.CleanupAfterError,
                        Fingerprint = flowEvent.Fingerprint,
                        DedupeTtl = TimeSpan.FromMilliseconds(1500),
                        Execute = strategy =>
                        {
                            strategy.CleanupAfterError();
                            return true;
                        }
                    });
                    break;

                case OrderFlowEventKind.StopDragged:
                case OrderFlowEventKind.BeClicked:
                case OrderFlowEventKind.TrailClicked:
                    commands.Add(new OrderFlowCommand
                    {
                        Kind = OrderFlowCommandKind.ChangeStop,
                        Fingerprint = flowEvent.Fingerprint,
                        DedupeTtl = flowEvent.Kind == OrderFlowEventKind.StopDragged
                            ? TimeSpan.FromMilliseconds(120)
                            : TimeSpan.FromMilliseconds(400),
                        Execute = strategy =>
                        {
                            return strategy.TryRequestOrderModify(
                                flowEvent.Order,
                                flowEvent.OrderQuantity,
                                flowEvent.LimitPrice,
                                flowEvent.StopOrderPrice,
                                true,
                                flowEvent.Scope ?? "ChangeOrder-Stop");
                        }
                    });
                    break;

                case OrderFlowEventKind.TargetDragged:
                    commands.Add(new OrderFlowCommand
                    {
                        Kind = OrderFlowCommandKind.ChangeTarget,
                        Fingerprint = flowEvent.Fingerprint,
                        DedupeTtl = TimeSpan.FromMilliseconds(120),
                        Execute = strategy =>
                        {
                            return strategy.TryRequestOrderModify(
                                flowEvent.Order,
                                flowEvent.OrderQuantity,
                                flowEvent.LimitPrice,
                                flowEvent.StopOrderPrice,
                                false,
                                flowEvent.Scope ?? "ChangeOrder-Target");
                        }
                    });
                    break;

                case OrderFlowEventKind.CloseClicked:
                    commands.Add(new OrderFlowCommand
                    {
                        Kind = OrderFlowCommandKind.ResetAndFlatten,
                        Fingerprint = flowEvent.Fingerprint,
                        DedupeTtl = TimeSpan.FromMilliseconds(750),
                        Execute = strategy =>
                        {
                            strategy.ResetAndFlatten(flowEvent.Reason ?? "UserClose");
                            return true;
                        }
                    });
                    break;

                case OrderFlowEventKind.PositionFlat:
                    commands.Add(new OrderFlowCommand
                    {
                        Kind = OrderFlowCommandKind.NoOp,
                        Fingerprint = flowEvent.Fingerprint,
                        DedupeTtl = TimeSpan.Zero,
                        Execute = strategy => false
                    });
                    break;
            }

            return commands;
        }

        private void ResetModifyGate(OrderModifyGate gate)
        {
            if (gate == null)
                return;

            gate.Pending = false;
            gate.LastOrderId = null;
            gate.LastRequestedLimit = double.NaN;
            gate.LastRequestedStop = double.NaN;
            gate.LastRequestedUtc = DateTime.MinValue;
            gate.LastSubmittedUtc = DateTime.MinValue;
            gate.HasQueuedRequest = false;
            gate.QueuedQuantity = 0;
            gate.QueuedLimit = double.NaN;
            gate.QueuedStop = double.NaN;
            gate.QueuedScope = null;
            gate.LastBlockedReason = null;
            gate.LastBlockedUtc = DateTime.MinValue;
        }

        private void QueueOrderModifyRequest(OrderModifyGate gate, int quantity, double limitPrice, double stopPriceParam, string scope)
        {
            if (gate == null)
                return;

            gate.HasQueuedRequest = true;
            gate.QueuedQuantity = quantity;
            gate.QueuedLimit = limitPrice;
            gate.QueuedStop = stopPriceParam;
            gate.QueuedScope = scope;
        }

        private void ClearQueuedOrderModifyRequest(OrderModifyGate gate)
        {
            if (gate == null)
                return;

            gate.HasQueuedRequest = false;
            gate.QueuedQuantity = 0;
            gate.QueuedLimit = double.NaN;
            gate.QueuedStop = double.NaN;
            gate.QueuedScope = null;
        }

        private void LogModifyBlocked(OrderModifyGate gate, string reason, string message)
        {
            if (LogLevelSetting != LogLevelOption.Debug)
                return;

            DateTime now = DateTime.Now;
            bool sameReason = gate != null && string.Equals(gate.LastBlockedReason, reason, StringComparison.Ordinal);
            if (sameReason && gate != null && (now - gate.LastBlockedUtc).TotalMilliseconds < ModifyBlockLogThrottleMs)
                return;

            if (gate != null)
            {
                gate.LastBlockedReason = reason;
                gate.LastBlockedUtc = now;
            }
            LogDebug(message);
        }

        private void FlushQueuedOrderModifyPulse()
        {
            if (Position.MarketPosition == MarketPosition.Flat)
                return;

            if (stopModifyGate.HasQueuedRequest && stopOrder != null)
                TryFlushQueuedOrderModify(stopOrder, true, "Pulse");
            if (targetModifyGate.HasQueuedRequest && targetOrder != null)
                TryFlushQueuedOrderModify(targetOrder, false, "Pulse");
        }

        private bool TryFlushQueuedOrderModify(Order order, bool isStop, string trigger)
        {
            if (order == null)
                return false;

            OrderModifyGate gate = isStop ? stopModifyGate : targetModifyGate;
            if (gate == null || !gate.HasQueuedRequest)
                return false;

            string scope = string.IsNullOrWhiteSpace(gate.QueuedScope) ? $"ChangeOrder-{gate.Label}-Queued" : $"{gate.QueuedScope}|Queued:{trigger}";
            return TryRequestOrderModify(order, gate.QueuedQuantity, gate.QueuedLimit, gate.QueuedStop, isStop, scope, true);
        }

        private void EnsureTradeCycle(string reason)
        {
            if (activeTradeCycleId > 0)
                return;

            activeTradeCycleId = nextTradeCycleId++;
            LogDebug($"TradeCycle start id={activeTradeCycleId} reason={reason}");
        }

        private void EndTradeCycle(string reason)
        {
            if (activeTradeCycleId <= 0)
                return;

            LogDebug($"TradeCycle end id={activeTradeCycleId} reason={reason}");
            activeTradeCycleId = 0;
        }

        private void ReleaseExitLock(string reason)
        {
            if (!isExiting && string.IsNullOrEmpty(exitOrderId))
                return;

            LogDebug($"ExitLock release reason={reason} exitOrderId={exitOrderId ?? "n/a"}");
            isExiting = false;
            exitOrderId = null;
            exitOrderStartedUtc = DateTime.MinValue;
        }

        private bool SubmitCloseOrder(string scope, OrderAction action, int qty, string reason)
        {
            if (qty <= 0)
                return false;
            if (isExiting)
            {
                LogDebug($"OrderSubmit blocked signal={tags.CloseSignal} reason=ExitLockActive exitOrderId={exitOrderId ?? "n/a"} scope={scope} qty={qty}");
                return false;
            }

            bool didRun = false;
            Order close = null;
            SafeExecuteTrade(scope, () =>
            {
                didRun = true;
                EnsureTradeCycle("CloseSubmit");
                close = SubmitOrderUnmanaged(0, action, OrderType.Market, qty, 0, 0, null, tags.CloseSignal);
            });

            if (!didRun || close == null)
            {
                LogDebug($"OrderSubmit failed signal={tags.CloseSignal} scope={scope} qty={qty} cycle={activeTradeCycleId}");
                return false;
            }

            isExiting = true;
            exitOrderId = close.OrderId;
            exitOrderStartedUtc = DateTime.UtcNow;
            RecordOrderAction($"RR_CLOSE submit qty={qty} reason={reason} orderId={exitOrderId ?? "n/a"} cycle={activeTradeCycleId}");
            LogDebug($"OrderSubmit signal={tags.CloseSignal} qty={qty} reason={reason} orderId={exitOrderId ?? "n/a"} cycle={activeTradeCycleId}");
            return true;
        }

        private bool TryRequestOrderModify(Order order, int quantity, double limitPrice, double stopPriceParam, bool isStop, string scope, bool fromQueuedFlush = false)
        {
            EnsureHelpers();
            OrderModifyGate gate = isStop ? stopModifyGate : targetModifyGate;
            string orderTag = isStop ? tags.StopSignal : tags.TargetSignal;
            double tick = sizing.TickSize();
            if (tick <= 0)
                tick = 0.01;

            if (isExiting)
            {
                LogModifyBlocked(gate, "ExitLockActive", $"ChangeOrder blocked signal={orderTag} reason=ExitLockActive scope={scope} exitOrderId={exitOrderId ?? "n/a"}");
                return false;
            }
            if (order == null)
            {
                LogModifyBlocked(gate, "NullOrder", $"ChangeOrder blocked signal={orderTag} reason=NullOrder scope={scope}");
                return false;
            }
            if (!IsOrderSafeForResize(order))
            {
                QueueOrderModifyRequest(gate, quantity, limitPrice, stopPriceParam, scope);
                LogModifyBlocked(gate, $"OrderState:{order.OrderState}", $"ChangeOrder blocked signal={orderTag} reason=OrderState state={order.OrderState} scope={scope} queued=True");
                return false;
            }

            double roundedLimit = sizing.RoundToTick(limitPrice);
            double roundedStop = sizing.RoundToTick(stopPriceParam);
            double currentLimit = sizing.RoundToTick(order.LimitPrice);
            double currentStop = sizing.RoundToTick(order.StopPrice);
            bool limitMoved = Math.Abs(roundedLimit - currentLimit) >= tick * 0.999;
            bool stopMoved = Math.Abs(roundedStop - currentStop) >= tick * 0.999;
            if (!limitMoved && !stopMoved)
            {
                ClearQueuedOrderModifyRequest(gate);
                LogModifyBlocked(gate, "NoTickMove", $"ChangeOrder skipped signal={orderTag} reason=NoTickMove scope={scope} oldL={currentLimit:F2} newL={roundedLimit:F2} oldS={currentStop:F2} newS={roundedStop:F2} pending={gate.Pending}");
                return false;
            }

            DateTime nowUtc = DateTime.UtcNow;
            if (gate.Pending && string.Equals(gate.LastOrderId, order.OrderId, StringComparison.Ordinal))
            {
                QueueOrderModifyRequest(gate, quantity, roundedLimit, roundedStop, scope);
                LogModifyBlocked(gate, "Pending", $"ChangeOrder blocked signal={orderTag} reason=Pending scope={scope} reqL={gate.LastRequestedLimit:F2} reqS={gate.LastRequestedStop:F2} orderId={order.OrderId} queued=True");
                return false;
            }

            if (gate.LastSubmittedUtc != DateTime.MinValue
                && (nowUtc - gate.LastSubmittedUtc).TotalMilliseconds < OrderModifyThrottleMs)
            {
                double elapsed = (nowUtc - gate.LastSubmittedUtc).TotalMilliseconds;
                QueueOrderModifyRequest(gate, quantity, roundedLimit, roundedStop, scope);
                LogModifyBlocked(gate, "Throttle", $"ChangeOrder blocked signal={orderTag} reason=Throttle scope={scope} elapsedMs={elapsed:F0} throttleMs={OrderModifyThrottleMs} queued=True");
                return false;
            }

            bool sameAsLastRequested = string.Equals(gate.LastOrderId, order.OrderId, StringComparison.Ordinal)
                && !double.IsNaN(gate.LastRequestedLimit)
                && !double.IsNaN(gate.LastRequestedStop)
                && Math.Abs(gate.LastRequestedLimit - roundedLimit) < tick / 8
                && Math.Abs(gate.LastRequestedStop - roundedStop) < tick / 8;
            if (sameAsLastRequested)
            {
                ClearQueuedOrderModifyRequest(gate);
                LogModifyBlocked(gate, "DuplicateRequest", $"ChangeOrder skipped signal={orderTag} reason=DuplicateRequest scope={scope} orderId={order.OrderId} reqL={roundedLimit:F2} reqS={roundedStop:F2}");
                return false;
            }

            bool didRun = false;
            SafeExecuteTrade(scope, () =>
            {
                didRun = true;
                ChangeOrder(order, quantity, roundedLimit, roundedStop);
            });
            if (!didRun)
                return false;

            gate.Pending = true;
            gate.LastOrderId = order.OrderId;
            gate.LastRequestedLimit = roundedLimit;
            gate.LastRequestedStop = roundedStop;
            gate.LastRequestedUtc = nowUtc;
            gate.LastSubmittedUtc = nowUtc;
            ClearQueuedOrderModifyRequest(gate);

            string source = fromQueuedFlush ? "queued" : "live";
            LogDebug($"ChangeOrder send signal={orderTag} scope={scope} source={source} orderId={order.OrderId} qty={quantity} oldL={currentLimit:F2} newL={roundedLimit:F2} oldS={currentStop:F2} newS={roundedStop:F2} pending={gate.Pending} throttleMs={OrderModifyThrottleMs}");
            return true;
        }

        private void UpdateOrderModifyGateFromUpdate(Order order)
        {
            if (order == null)
                return;

            OrderModifyGate gate = null;
            if (order.Name == tags.StopSignal || order == stopOrder)
                gate = stopModifyGate;
            else if (order.Name == tags.TargetSignal || order == targetOrder)
                gate = targetModifyGate;

            if (gate == null)
                return;

            if (order.OrderState == OrderState.Accepted
                || order.OrderState == OrderState.Working
                || order.OrderState == OrderState.PartFilled
                || order.OrderState == OrderState.Filled
                || order.OrderState == OrderState.Cancelled
                || order.OrderState == OrderState.Rejected)
            {
                if (gate.Pending)
                    LogDebug($"ChangeOrder ack signal={gate.Label} orderId={order.OrderId} state={order.OrderState} pending->false");
                gate.Pending = false;
                gate.LastBlockedReason = null;
                if (order.OrderState == OrderState.Accepted
                    || order.OrderState == OrderState.Working
                    || order.OrderState == OrderState.PartFilled)
                    TryFlushQueuedOrderModify(order, gate == stopModifyGate, "OrderUpdateAck");
                else
                    ClearQueuedOrderModifyRequest(gate);
            }
        }

        private void UpdateExitLockFromOrderUpdate(Order order)
        {
            if (order == null || order.Name != tags.CloseSignal)
                return;

            if (order.OrderState == OrderState.Submitted
                || order.OrderState == OrderState.Accepted
                || order.OrderState == OrderState.Working
                || order.OrderState == OrderState.PartFilled)
            {
                isExiting = true;
                if (!string.IsNullOrEmpty(order.OrderId))
                    exitOrderId = order.OrderId;
                return;
            }

            if (order.OrderState == OrderState.Filled
                || order.OrderState == OrderState.Cancelled
                || order.OrderState == OrderState.Rejected)
            {
                if (string.IsNullOrEmpty(exitOrderId) || string.Equals(exitOrderId, order.OrderId, StringComparison.Ordinal))
                    ReleaseExitLock($"CloseOrder{order.OrderState}");
            }
        }

        private void LogOrderUpdateTrace(Order order, OrderState state, int filled, double averageFillPrice, ErrorCode error, string nativeError)
        {
            if (LogLevelSetting != LogLevelOption.Debug || order == null)
                return;
            string native = string.IsNullOrWhiteSpace(nativeError) ? "-" : nativeError;
            LogDebug($"OrderUpdate cycle={activeTradeCycleId} name={order.Name} orderId={order.OrderId} state={state} filled={filled}/{order.Quantity} avg={averageFillPrice:F2} oco={order.Oco} err={error} native={native}");
        }

        private void LogPositionUpdateTrace(Position position, int quantity, MarketPosition marketPosition, double averagePrice)
        {
            if (LogLevelSetting != LogLevelOption.Debug || position == null)
                return;
            LogDebug($"PositionUpdate cycle={activeTradeCycleId} instrument={position.Instrument?.FullName} qty={quantity} mp={marketPosition} avg={averagePrice:F2}");
        }

        // CONFIRM step: clamps lines against market, computes quantity, and submits unmanaged entry + OCO stop/target with tag prefix (INVARIANT: self-check must pass and qty>=1).
        private void ConfirmEntry()
        {
            EnsureHelpers();
            if (State != State.Realtime)
            {
                LogInfo($"ConfirmEntry blocked: NotRealtime state={State}");
                return;
            }
            SetMilestone("ConfirmEntry-Start");
            if (!isArmed || armedDirection == ArmDirection.None)
                return;
            if (isExiting)
            {
                LogInfo($"ConfirmEntry blocked: exit in progress (exitOrderId={exitOrderId ?? "n/a"})");
                return;
            }

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

            if (!HasValidEntryRiskStop())
            {
                LogInfo("Entry blocked: invalid stop placement (stop must stay on loss side of entry)");
                return;
            }

            int qty = 0;
            SafeExecuteTrade("CalculateQuantity", () =>
            {
                qty = sizing.CalculateQuantity(entryPrice, stopPrice, FixedRiskUSD, CommissionMode == CommissionModeOption.On, CommissionPerContractRoundTurn, MaxContracts);
            });
            if (fatalError)
                return;
            if (qty < 1)
            {
                LogQtyBlocked();
                return;
            }

            OrderAction entryAction = armedDirection == ArmDirection.Long ? OrderAction.Buy : OrderAction.SellShort;
            string entryName = armedDirection == ArmDirection.Long ? tags.EntrySignalLong : tags.EntrySignalShort;
            string instr = (Instrument?.FullName ?? string.Empty).Trim();
            string entryR = RoundForFp(sizing.RoundToTick(entryPrice)).ToString("G17", CultureInfo.InvariantCulture);
            string stopR = RoundForFp(sizing.RoundToTick(stopPrice)).ToString("G17", CultureInfo.InvariantCulture);
            string tgtR = RoundForFp(sizing.RoundToTick(targetPrice)).ToString("G17", CultureInfo.InvariantCulture);
            string fingerprint = $"{instr}|{armedDirection}|qty={qty}|entry={entryR}|stop={stopR}|target={tgtR}";
            DateTime nowUtc = DateTime.UtcNow;
            if ((nowUtc - lastConfirmEntryUtc).TotalMilliseconds < 400)
            {
                LogDebug("ConfirmEntry throttled");
                return;
            }
            if (lastConfirmFingerprint == fingerprint && (nowUtc - lastConfirmFingerprintUtc).TotalMilliseconds < 1500)
            {
                LogDebug("ConfirmEntry deduped");
                return;
            }
            lastConfirmEntryUtc = nowUtc;

            int did = HandleOrderFlowEvent(new OrderFlowEvent
            {
                Kind = OrderFlowEventKind.ConfirmClicked,
                Scope = "SubmitOrders",
                Fingerprint = $"confirm|{fingerprint}",
                EntryAction = entryAction,
                EntryName = entryName,
                Quantity = qty
            });

            if (fatalError || entryOrder == null || did <= 0)
            {
                hasPendingEntry = false;
                isArmed = true;
                armedDirection = entryAction == OrderAction.Buy ? ArmDirection.Long : ArmDirection.Short;
                UpdateUiState();
                LogInfo("Entry submit failed; pending not set");
                return;
            }

            lastConfirmFingerprint = fingerprint;
            lastConfirmFingerprintUtc = nowUtc;
            EnsureTradeCycle("EntrySubmit");
            isArmed = false;
            hasPendingEntry = true;
            UpdateUiState();
            string side = entryAction == OrderAction.Buy ? "BUY" : "SELL SHORT";
            RecordOrderAction($"{side} entry submit qty={qty} SL={stopPrice:F2} TP={targetPrice:F2}");
            LogInfo($"{side} entry submitted: qty {qty}, SL {stopPrice:F2}, TP {targetPrice:F2}");
            ApplyLineUpdates();
            UpdateLabelsOnly();
            SetMilestone("ConfirmEntry-End");
        }

        private double RoundForFp(double price)
        {
            if (sizing == null)
                return price;
            return sizing.RoundToTick(price);
        }

        // CLOSE flow: cancels working entry/exit orders, flattens position, and clears HUD/arming state.
        private void ResetAndFlatten(string reason)
        {
            EnsureHelpers();
            if (State != State.Realtime)
            {
                LogInfo($"ResetAndFlatten blocked: NotRealtime state={State}");
                return;
            }
            SetMilestone("ResetAndFlatten-Start");
            LogInfo("CLOSE pressed -> cancel orders + flatten + UI reset");
            forceSizingDebugLog = false;

            CancelActiveOrder(entryOrder, "CancelEntry");
            CancelActiveOrder(stopOrder, "CancelStop");
            CancelActiveOrder(targetOrder, "CancelTarget");

            if (Position.MarketPosition != MarketPosition.Flat)
            {
                int qty = Math.Abs(Position.Quantity);
                if (qty > 0)
                {
                    OrderAction action = Position.MarketPosition == MarketPosition.Long ? OrderAction.Sell : OrderAction.BuyToCover;
                    RecordOrderAction($"Flatten via CLOSE qty={qty}");
                    SubmitCloseOrder("ClosePosition", action, qty, reason ?? "UserClose");
                }
            }

            DisarmAndClearLines();

            entryOrder = null;
            stopOrder = null;
            targetOrder = null;
            currentOco = null;
            hasPendingEntry = false;
            avgEntryPrice = 0;
            lastEntryFillPrice = 0;
            entryFillConfirmed = false;
            processedExitIds.Clear();
            SetMilestone("ResetAndFlatten-End");
        }

        // Break-even helper: blocks when not profitable and clamps stop/target to avoid invalid placement.
        private void MoveStopToBreakEven()
        {
            EnsureHelpers();
            if (State != State.Realtime)
            {
                LogInfo($"MoveStopToBreakEven blocked: NotRealtime state={State}");
                return;
            }
            if (isExiting)
            {
                LogInfo("BE blocked: exit in progress");
                return;
            }
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

            double beAnchor = avgEntryPrice > 0
                ? avgEntryPrice
                : ((Position != null && Position.AveragePrice > 0) ? Position.AveragePrice : 0);
            if (beAnchor <= 0)
            {
                LogInfo("BE failed: avg entry price unavailable");
                return;
            }

            if ((Position.MarketPosition == MarketPosition.Long && marketRef <= beAnchor)
                || (Position.MarketPosition == MarketPosition.Short && marketRef >= beAnchor))
            {
                LogInfo("BE blocked: position not in profit");
                ShowBeBlockedDialog();
                return;
            }

            double newStop = Position.MarketPosition == MarketPosition.Long
                ? beAnchor + (BreakEvenPlusTicks * sizing.TickSize())
                : beAnchor - (BreakEvenPlusTicks * sizing.TickSize());

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
                int did = HandleOrderFlowEvent(new OrderFlowEvent
                {
                    Kind = OrderFlowEventKind.BeClicked,
                    Scope = "ChangeOrder-BE",
                    Fingerprint = $"be|{stopOrder.OrderId}|{RoundForFp(stopPrice).ToString("G17", CultureInfo.InvariantCulture)}",
                    Order = stopOrder,
                    OrderQuantity = stopOrder.Quantity,
                    LimitPrice = stopOrder.LimitPrice,
                    StopOrderPrice = stopPrice
                });
                if (did > 0)
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
            if (State != State.Realtime)
            {
                LogInfo($"ExecuteTrailStop blocked: NotRealtime state={State}");
                return;
            }
            if (isExiting)
            {
                ShowTrailMessage("TRAIL: Exit in progress.");
                return;
            }
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
                int did = HandleOrderFlowEvent(new OrderFlowEvent
                {
                    Kind = OrderFlowEventKind.TrailClicked,
                    Scope = "ChangeOrder-TRAIL",
                    Fingerprint = $"trail|{stopOrder.OrderId}|{RoundForFp(stopPrice).ToString("G17", CultureInfo.InvariantCulture)}",
                    Order = stopOrder,
                    OrderQuantity = stopOrder.Quantity,
                    LimitPrice = stopOrder.LimitPrice,
                    StopOrderPrice = stopPrice
                });
                if (did > 0)
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
            RecordOrderAction($"Position closed via {reason}");
            LogInfo($"Exit filled via {reason}");
            stopOrder = null;
            targetOrder = null;
            entryOrder = null;
            avgEntryPrice = 0;
            lastEntryFillPrice = 0;
            entryFillConfirmed = false;
            hasPendingEntry = false;
            processedExitIds.Clear();
            Disarm();
        }

        // Cleanup path for rejected/cancelled orders to avoid lingering ARMED state or draw objects.
        private void CleanupAfterError()
        {
            CancelActiveOrder(entryOrder, "CleanupAfterError.CancelEntry");
            CancelActiveOrder(stopOrder, "CleanupAfterError.CancelStop");
            CancelActiveOrder(targetOrder, "CleanupAfterError.CancelTarget");
            if (Position.MarketPosition != MarketPosition.Flat)
            {
                int qty = Math.Abs(Position.Quantity);
                if (qty > 0)
                {
                    OrderAction action = Position.MarketPosition == MarketPosition.Long ? OrderAction.Sell : OrderAction.BuyToCover;
                    RecordOrderAction($"CleanupAfterError flatten qty={qty}");
                    SubmitCloseOrder("CloseAfterReject", action, qty, "CleanupAfterError");
                }
            }
            Disarm();
            RemoveAllDrawObjects();
        }

        private bool IsOrderSafeForResize(Order order)
        {
            if (order == null)
                return false;
            return order.OrderState == OrderState.Working
                || order.OrderState == OrderState.Accepted
                || order.OrderState == OrderState.PartFilled;
        }

        private void CheckBracketIntegrityAfterExitOrderState(string source)
        {
            bool stopActive = IsOrderActive(stopOrder);
            bool targetActive = IsOrderActive(targetOrder);
            if (!(stopActive ^ targetActive))
                return;

            if (!bracketIncompleteHandled)
            {
                if (fatalError)
                {
                    bracketIncompleteHandled = true;
                    HandleBracketIncompleteDuringFatal(source);
                    return;
                }
                LogDiagnosticSnapshot(source);
                bracketIncompleteHandled = true;
            }

            FailSafeFlatten(source);
        }

        private void FailSafeFlatten(string reason)
        {
            if (Position.MarketPosition == MarketPosition.Flat)
                return;

            DateTime nowUtc = DateTime.UtcNow;
            // First attempt per episode is not throttled; subsequent are throttled.
            if (failSafeFlattenAttemptedThisEpisode
                && lastFailSafeFlattenAttemptUtc != DateTime.MinValue
                && (nowUtc - lastFailSafeFlattenAttemptUtc).TotalMilliseconds < FailSafeFlattenThrottleMs)
            {
                if (lastFailSafeFlattenThrottleLogUtc == DateTime.MinValue
                    || (nowUtc - lastFailSafeFlattenThrottleLogUtc).TotalMilliseconds >= FailSafeFlattenThrottleMs)
                {
                    lastFailSafeFlattenThrottleLogUtc = nowUtc;
                    LogDebug($"FailSafeFlatten throttled reason={reason} elapsedMs={(nowUtc - lastFailSafeFlattenAttemptUtc).TotalMilliseconds:F0}");
                }
                return;
            }
            failSafeFlattenAttemptedThisEpisode = true;
            lastFailSafeFlattenAttemptUtc = nowUtc;
            if (!bracketIncompleteHandled)
                bracketIncompleteHandled = true;

            Print($"{Prefix("WARN")} BracketIncomplete: {reason} -> flatten");

            CancelActiveOrder(entryOrder, "BracketFailSafeCancelEntry");
            CancelActiveOrder(stopOrder, "BracketFailSafeCancelStop");
            CancelActiveOrder(targetOrder, "BracketFailSafeCancelTarget");

            int qty = Math.Abs(Position.Quantity);
            if (qty > 0)
            {
                OrderAction action = Position.MarketPosition == MarketPosition.Long ? OrderAction.Sell : OrderAction.BuyToCover;
                RecordOrderAction($"FailSafeFlatten {reason} qty={qty}");
                SubmitCloseOrder("BracketFailSafeClose", action, qty, reason ?? "BracketIncomplete");
            }

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
            try
            {
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
            }
            catch (FormatException ex)
            {
                HandleFormatException("EnsureHelpers", ex);
            }

            if (created && !helpersInitLogged)
            {
                helpersInitLogged = true;
                if (LogLevelSetting != LogLevelOption.Off)
                    Print($"{Prefix("DEBUG")} Helpers initialized: orderPrefix='{normalizedPrefix}', drawPrefix='{normalizedPrefix}{instanceId}_', instanceId='{instanceId}'");
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

        private bool HasValidEntryRiskStop()
        {
            double tick = sizing.TickSize();
            if (tick <= 0)
                return false;

            if (armedDirection == ArmDirection.Long)
                return (entryPrice - stopPrice) > tick / 4;
            if (armedDirection == ArmDirection.Short)
                return (stopPrice - entryPrice) > tick / 4;
            return false;
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

        private bool TryGetDirectionalStopTicks(double entryRef, double stopRef, out double stopTicks)
        {
            stopTicks = 0;
            double tick = sizing.TickSize();
            if (tick <= 0)
                return false;

            MarketPosition direction = GetWorkingDirection();
            if (direction == MarketPosition.Long)
            {
                stopTicks = (entryRef - stopRef) / tick;
                return true;
            }
            if (direction == MarketPosition.Short)
            {
                stopTicks = (stopRef - entryRef) / tick;
                return true;
            }

            return false;
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
            bool entryNeedsLineRefresh = entryLabelDirty || entryPriceMoved;
            bool entryNeedsLabelOnlyRefresh = !entryNeedsLineRefresh && entryTextChanged;
            // Label-only refresh must not move anchors; prevents snap-back after drag.
            if (entryNeedsLineRefresh)
            {
                double oldEntryPrice = lastEntryLabelPrice;
                chartLines.UpsertLine(RiskRayChartLines.LineKind.Entry, entryPrice, entryLabel);
                lastEntryLabel = entryLabel;
                lastEntryLabelPrice = entryPrice;
                entryLabelDirty = false;
                LogLabelRefresh("ENTRY", entryTextChanged, entryPriceMoved, oldEntryPrice, entryPrice);
            }
            else if (entryNeedsLabelOnlyRefresh)
            {
                double oldEntryPrice = lastEntryLabelPrice;
                chartLines.UpdateLineLabel(RiskRayChartLines.LineKind.Entry, entryLabel);
                lastEntryLabel = entryLabel;
                lastEntryLabelPrice = entryPrice;
                LogLabelRefresh("ENTRY", true, false, oldEntryPrice, entryPrice);
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

            if (ShouldLogDebugBlink())
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

            if (LogLevelSetting != LogLevelOption.Debug && forceSizingDebugLog)
                forceSizingDebugLog = false;

            if (LogLevelSetting == LogLevelOption.Debug)
            {
                bool forceLog = forceSizingDebugLog;
                double tick = sizing.TickSize();
                double entryRef = GetEntryReferenceForRisk();
                double targetTicks = tick > 0 ? Math.Abs(targetPrice - entryRef) / tick : 0;
                double stopTicks;
                bool directional = TryGetDirectionalStopTicks(entryRef, stopPrice, out stopTicks);
                if (!directional)
                    stopTicks = tick > 0 ? Math.Abs(entryRef - stopPrice) / tick : 0;
                int qty = sizing.CalculateQuantity(entryPrice, stopPrice, FixedRiskUSD, CommissionMode == CommissionModeOption.On, CommissionPerContractRoundTurn, MaxContracts);

                if (ShouldLogSizingDebug(qty, stopTicks, targetTicks, entryRef, forceLog))
                {
                    if (forceLog)
                        forceSizingDebugLog = false;
                    if (directional && stopTicks <= 0)
                    {
                        string qtyDisplay = Position.MarketPosition != MarketPosition.Flat && Position.Quantity != 0
                            ? $"{Math.Abs(Position.Quantity)} contracts"
                            : "N/A";
                        LogDebug($"Sizing: risk-free stop (stopTicks<=0) -> risk=0, qtyDisplay={qtyDisplay}, targetTicks={targetTicks:F1}, entryPrice={entryPrice:F2}, stopPrice={stopPrice:F2}, targetPrice={targetPrice:F2}, entryRef={entryRef:F2}");
                    }
                    else
                    {
                        LogDebug($"Sizing: qty {qty}, stopTicks {stopTicks:F1}, targetTicks {targetTicks:F1}, entryPrice={entryPrice:F2}, stopPrice={stopPrice:F2}, targetPrice={targetPrice:F2}, entryRef={entryRef:F2}");
                    }
                }
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

        private bool ShouldLogDebugBlink()
        {
            if (!DebugBlink)
                return false;
            if ((DateTime.Now - lastDebugBlinkLogTime).TotalMilliseconds < DebugBlinkThrottleMs)
                return false;
            lastDebugBlinkLogTime = DateTime.Now;
            return true;
        }

        ChartControl IRiskRayPanelHost.ChartControl => ChartControl;
        bool IRiskRayPanelHost.UiUnavailablePermanently => uiUnavailablePermanently;
        bool IRiskRayPanelHost.IsArmed => isArmed;
        bool IRiskRayPanelHost.IsArmedLong => isArmed && armedDirection == ArmDirection.Long;
        bool IRiskRayPanelHost.IsArmedShort => isArmed && armedDirection == ArmDirection.Short;
        bool IRiskRayPanelHost.HasPosition => Position.MarketPosition != MarketPosition.Flat;
        bool IRiskRayPanelHost.HasPendingEntry => hasPendingEntry || entryOrder != null;
        bool IRiskRayPanelHost.HasStopOrder => IsStopOrderUiActive(stopOrder);
        bool IRiskRayPanelHost.HasTargetOrder => targetOrder != null;
        bool IRiskRayPanelHost.DebugBlinkEnabled => DebugBlink;

        void IRiskRayPanelHost.ArmLong() => OnBuyClicked(null, null);
        void IRiskRayPanelHost.ArmShort() => OnSellClicked(null, null);
        void IRiskRayPanelHost.Confirm() => TriggerCustomEvent(_ => SafeExecuteTrade("ConfirmEntry", ConfirmEntry), null);
        void IRiskRayPanelHost.Close() => OnCloseClicked(null, null);
        void IRiskRayPanelHost.BreakEven() => HandleBeClick();
        void IRiskRayPanelHost.Trail() => HandleTrailClick();

        void IRiskRayPanelHost.UiInvoke(Action action) => UiInvoke(action);
        void IRiskRayPanelHost.UiBeginInvoke(Action action) => UiBeginInvoke(action);
        void IRiskRayPanelHost.SafeExecuteUI(string context, Action action) => SafeExecuteUI(context, action);
        void IRiskRayPanelHost.SafeExecute(string context, Action action) => SafeExecuteTrade(context, action);
        void IRiskRayPanelHost.SetMilestone(string marker) => SetMilestone(marker);
        void IRiskRayPanelHost.LogInfo(string message) => LogInfo(message);
        void IRiskRayPanelHost.LogDebug(string message) => LogDebug(message);
        void IRiskRayPanelHost.LogUiError(string context, Exception ex) => LogUiError(context, ex);
        void IRiskRayPanelHost.Print(string message) => Print(message);
        string IRiskRayPanelHost.Prefix(string level) => Prefix(level);
        bool IRiskRayPanelHost.ShouldLogDebugBlink() => ShouldLogDebugBlink();

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
            int cycle = activeTradeCycleId;
            return level == null
                ? $"[RiskRay][{tag}][I:{instanceId}][C:{cycle}]"
                : $"[RiskRay][{tag}][I:{instanceId}][C:{cycle}][{level}]";
        }

        // Safe dispatcher helpers: UI calls no-op if dispatcher unavailable; errors throttled to avoid noisy logs.
        private bool TryOnUi(string context, Action action, bool scheduleAsync)
        {
            if (action == null)
                return false;

            bool chartNull = ChartControl == null;
            Dispatcher dispatcher = ChartControl?.Dispatcher;
            bool dispatcherNull = dispatcher == null;
            bool dispatcherShutdown = !dispatcherNull && dispatcher.HasShutdownStarted;
            bool dispatcherOk = !chartNull && !dispatcherNull && !dispatcherShutdown;
            if (!dispatcherOk)
            {
                if (dispatcherShutdown)
                    uiUnavailablePermanently = true;
                if (isRunningInstance)
                    LogUiAvailabilityOnce(context, chartNull, dispatcherOk, dispatcherNull, dispatcherShutdown);
                return false;
            }

            try
            {
                if (dispatcher.CheckAccess())
                {
                    SafeExecuteUI(context, action);
                    return true;
                }

                if (scheduleAsync)
                    dispatcher.InvokeAsync(() => SafeExecuteUI(context, action));
                else
                    dispatcher.Invoke(() => SafeExecuteUI(context, action));
                return true;
            }
            catch (Exception ex)
            {
                RecordException($"UI.{context}.Invoke", ex, false);
                LogUiError($"{context}.Invoke", ex);
                bool failChartNull = ChartControl == null;
                Dispatcher failDispatcher = ChartControl?.Dispatcher;
                bool failDispatcherNull = failDispatcher == null;
                bool failDispatcherShutdown = !failDispatcherNull && failDispatcher.HasShutdownStarted;
                bool failDispatcherOk = !failChartNull && !failDispatcherNull && !failDispatcherShutdown;
                if (failDispatcherShutdown)
                    uiUnavailablePermanently = true;
                LogUiAvailabilityOnce($"{context}.Invoke", failChartNull, failDispatcherOk, failDispatcherNull, failDispatcherShutdown);
                return false;
            }
        }

        private void UiInvoke(Action action)
        {
            TryOnUi("UiInvoke", action, false);
        }

        private void UiBeginInvoke(Action action)
        {
            TryOnUi("UiBeginInvoke", action, true);
        }

        private void RecordDiagEvent(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;
            string entry = $"{DateTime.Now:HH:mm:ss.fff} {message}";
            lock (terminationEventLock)
            {
                terminationEvents.Enqueue(entry);
                while (terminationEvents.Count > TerminationEventBufferSize)
                    terminationEvents.Dequeue();
            }
        }

        private void RecordUiEvent(string message)
        {
            lastUiEvent = $"{DateTime.Now:HH:mm:ss.fff} {message}";
            RecordDiagEvent($"UI {message}");
        }

        private void RecordOrderAction(string message)
        {
            string withCycle = $"cycle={activeTradeCycleId} {message}";
            lastOrderAction = $"{DateTime.Now:HH:mm:ss.fff} {withCycle}";
            RecordDiagEvent($"ORDER {withCycle}");
        }

        private void RecordStateTransition(State state)
        {
            lastStateTransition = state;
            lastStateTransitionTime = DateTime.Now;
            if (state != State.Terminated)
            {
                lastNonTerminatedState = state;
                lastNonTerminatedStateTime = lastStateTransitionTime;
            }
            RecordDiagEvent($"STATE {state}");
        }

        private void RecordException(string scope, Exception ex, bool critical)
        {
            if (ex == null)
                return;
            lastException = ex;
            lastExceptionScope = scope;
            lastExceptionTime = DateTime.Now;
            if (critical)
            {
                lastCriticalExceptionTime = lastExceptionTime;
                lastCriticalExceptionScope = scope;
            }
            RecordDiagEvent($"EX {(critical ? "CRIT" : "WARN")} {scope}: {ex.GetType().Name} {ex.Message}");
        }

        private string DumpDiagEvents()
        {
            lock (terminationEventLock)
            {
                if (terminationEvents.Count == 0)
                    return "none";
                return string.Join("\n", terminationEvents);
            }
        }

        private bool WasRecentStateTransition(params State[] states)
        {
            if (lastNonTerminatedStateTime == DateTime.MinValue)
                return false;
            if ((DateTime.Now - lastNonTerminatedStateTime).TotalSeconds > 10)
                return false;
            foreach (var state in states)
                if (lastNonTerminatedState == state)
                    return true;
            return false;
        }

        private string GetExceptionSummary()
        {
            if (lastException == null)
                return "none";
            string age = lastExceptionTime == DateTime.MinValue
                ? "n/a"
                : $"{(DateTime.Now - lastExceptionTime).TotalSeconds:F1}s ago";
            return $"{lastExceptionScope}: {lastException.GetType().Name} {lastException.Message} ({age})";
        }

        private string BuildTerminationSnapshot(string reason, string cleanupMode, bool chartNull, bool dispatcherOk, bool precededByTransition)
        {
            string lastUi = string.IsNullOrWhiteSpace(lastUiEvent) ? "none" : lastUiEvent;
            string lastOrder = string.IsNullOrWhiteSpace(lastOrderAction) ? "none" : lastOrderAction;
            string lastState = lastNonTerminatedStateTime == DateTime.MinValue
                ? "none"
                : $"{lastNonTerminatedState} @ {lastNonTerminatedStateTime:HH:mm:ss.fff}";
            string lastStateAny = lastStateTransitionTime == DateTime.MinValue
                ? "none"
                : $"{lastStateTransition} @ {lastStateTransitionTime:HH:mm:ss.fff}";
            string milestone = string.IsNullOrWhiteSpace(lastMilestone)
                ? "none"
                : $"{lastMilestone} @ {lastMilestoneTime:HH:mm:ss.fff}";
            string criticalEx = lastCriticalExceptionTime == DateTime.MinValue
                ? "none"
                : $"{lastCriticalExceptionScope} @ {lastCriticalExceptionTime:HH:mm:ss.fff}";
            string chartDetach = chartDetachObserved
                ? $"{chartDetachObservedContext ?? "unknown"} @ {chartDetachObservedTime:HH:mm:ss.fff}"
                : "none";
            string pendingCleanup = uiCleanupPending
                ? $"{uiCleanupPendingReason ?? "pending"} since {uiCleanupPendingSinceUtc:O}"
                : "none";
            return
                $"  reason={reason}\n" +
                $"  cleanup={cleanupMode}\n" +
                $"  coreCleanup={lastTerminationCoreCleanup}\n" +
                $"  uiCleanup={lastTerminationUiCleanup}\n" +
                $"  fatalError={fatalError} fatalNotified={fatalNotified}\n" +
                $"  uiUnavailablePermanently={uiUnavailablePermanently}\n" +
                $"  chartDetachObserved={chartDetach}\n" +
                $"  uiCleanupPending={pendingCleanup}\n" +
                $"  chartNull={chartNull} dispatcherOk={dispatcherOk}\n" +
                $"  precededByTransition={precededByTransition} lastNonTerminatedState={lastState}\n" +
                $"  lastStateTransition={lastStateAny}\n" +
                $"  lastMilestone={milestone}\n" +
                $"  lastUiEvent={lastUi}\n" +
                $"  lastOrderAction={lastOrder}\n" +
                $"  lastException={GetExceptionSummary()}\n" +
                $"  lastCriticalException={criticalEx}\n" +
                $"  events:\n{DumpDiagEvents()}";
        }

        private void LogDiagnosticSnapshot(string reason)
        {
            bool chartNull = ChartControl == null;
            bool dispatcherOk = CanTouchUi();
            bool precededByTransition = WasRecentStateTransition(State.Configure, State.DataLoaded, State.Historical, State.Realtime);
            string snapshot = BuildTerminationSnapshot(reason, "Snapshot", chartNull, dispatcherOk, precededByTransition);
            Print($"{Prefix("WARN")} Diagnostic snapshot ({reason}):\n{snapshot}");
        }

        private void HandleBracketIncompleteDuringFatal(string reason)
        {
            LogDiagnosticSnapshot(reason);
            if ((DateTime.Now - lastHotFuseLogTime).TotalMilliseconds < HotFuseLogThrottleMs)
                return;
            lastHotFuseLogTime = DateTime.Now;
            string message = "Bracket incomplete (stop XOR target). Strategy is in FATAL mode (Variant A). " +
                             "Trading actions are disabled by design. Close position manually and verify bracket on broker.";
            LogInfo(message);
            RecordOrderAction($"HOT_FUSE {reason} fatal");
            RecordDiagEvent($"HOT_FUSE {reason}");
            if (!uiUnavailablePermanently && CanTouchUi())
                SafeExecuteUI("HotFuse.Notify", () => NotifyFatalOnce(message));
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
            if ((DateTime.Now - lastUiFailureLogTime).TotalMilliseconds < UiFailureLogThrottleMs)
                return;
            lastUiFailureLogTime = DateTime.Now;
            RecordException($"UI.{context}", ex, false);
            Print($"{Prefix("UI")} {context}: {ex.Message}");
        }

        private void LogUiAvailabilityOnce(string context, bool chartNull, bool dispatcherOk, bool dispatcherNull = false, bool dispatcherShutdown = false)
        {
            if (chartNull || !dispatcherOk)
                MarkChartDetachObserved(context, chartNull, dispatcherOk);
            if ((DateTime.Now - lastUiFailureLogTime).TotalMilliseconds < UiFailureLogThrottleMs)
                return;
            lastUiFailureLogTime = DateTime.Now;
            Print($"{Prefix("UI")} {context}: chartNull={chartNull} dispatcherOk={dispatcherOk} dispatcherNull={dispatcherNull} dispatcherShutdown={dispatcherShutdown} -> UI deferred");
            RecordDiagEvent($"UI_UNAVAILABLE {context} chartNull={chartNull} dispatcherOk={dispatcherOk} dispatcherNull={dispatcherNull} dispatcherShutdown={dispatcherShutdown}");
        }

        private void LogFatalGuardOnce(string scope)
        {
            if ((DateTime.Now - lastFatalGuardLogTime).TotalMilliseconds < FatalGuardLogThrottleMs)
                return;
            lastFatalGuardLogTime = DateTime.Now;
            LogInfo($"Fatal error: trading actions disabled. Close position manually. (skip {scope})");
            RecordOrderAction($"SKIP {scope} fatal");
            RecordDiagEvent($"FATAL_GUARD {scope}");
        }

        private void NotifyFatalOnce(string message)
        {
            if (fatalNotified)
                return;
            fatalNotified = true;
            string title = "RiskRay - Fatal";
            if (NotificationMode == NotificationModeOption.HUD)
            {
                if (chartLines != null && !uiUnavailablePermanently)
                {
                    SafeExecuteUI("NotifyFatal.HUD", () => chartLines.ShowHudNotification($"{title}: {message}"));
                }
                else
                {
                    Print($"{Prefix("WARN")} {title}: {message}");
                }
                return;
            }

            if (uiUnavailablePermanently || ChartControl == null)
            {
                Print($"{Prefix("WARN")} {title}: {message}");
                return;
            }

            SafeExecuteUI("NotifyFatal.MessageBox", () =>
            {
                MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
            });
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

        private void SafeExecuteUI(string scope, Action action)
        {
            bool chartNull = ChartControl == null;
            Dispatcher dispatcher = ChartControl?.Dispatcher;
            bool dispatcherNull = dispatcher == null;
            bool dispatcherShutdown = !dispatcherNull && dispatcher.HasShutdownStarted;
            bool dispatcherOk = !chartNull && !dispatcherNull && !dispatcherShutdown;
            if (!dispatcherOk)
            {
                if (dispatcherShutdown)
                    uiUnavailablePermanently = true;
                LogUiAvailabilityOnce(scope, chartNull, dispatcherOk, dispatcherNull, dispatcherShutdown);
                return;
            }
            if (action == null)
                return;
            try
            {
                action();
            }
            catch (Exception ex)
            {
                RecordException($"UI.{scope}", ex, false);
                LogUiError(scope, ex);
                bool canTouchUi = CanTouchUi();
                if (!canTouchUi)
                {
                    bool failChartNull = ChartControl == null;
                    Dispatcher failDispatcher = ChartControl?.Dispatcher;
                    bool failDispatcherNull = failDispatcher == null;
                    bool failDispatcherShutdown = !failDispatcherNull && failDispatcher.HasShutdownStarted;
                    bool failDispatcherOk = !failChartNull && !failDispatcherNull && !failDispatcherShutdown;
                    LogUiAvailabilityOnce(scope, failChartNull, failDispatcherOk, failDispatcherNull, failDispatcherShutdown);
                }
            }
        }

        private void HandleCriticalException(string scope, Exception ex)
        {
            if (ex == null)
                return;
            fatalError = true;
            fatalErrorMessage = $"{scope}: {ex.Message} | {ex.StackTrace}";
            lastCriticalExceptionTime = DateTime.Now;
            lastCriticalExceptionScope = scope;
            RecordException(scope, ex, true);
            fatalCount++;
            Print($"{Prefix("FATAL")} {fatalErrorMessage}");
            NotifyFatalOnce($"A critical error occurred. Trading actions are disabled.\n{ex.Message}");
            LogDiagnosticSnapshot($"TradeException:{scope}");
        }

        private void HandleFormatException(string scope, FormatException ex)
        {
            if (ex == null)
                return;
            RecordException(scope, ex, false);
            string culture = CultureInfo.CurrentCulture != null ? CultureInfo.CurrentCulture.Name : "unknown";
            Print($"{Prefix("WARN")} Format error in {scope}: {ex.Message} (culture={culture}). Action skipped.");
        }

        private void SafeExecuteTrade(string scope, Action action)
        {
            if (fatalError)
            {
                LogFatalGuardOnce(scope);
                return;
            }
            if (action == null)
                return;
            try
            {
                action();
            }
            catch (FormatException ex)
            {
                HandleFormatException(scope, ex);
            }
            catch (Exception ex)
            {
                HandleCriticalException(scope, ex);
            }
        }

        // Wrapper to centralize fatal logging while preserving original context.
        private void SafeExecute(string context, Action action)
        {
            try
            {
                action?.Invoke();
            }
            catch (FormatException ex)
            {
                HandleFormatException(context, ex);
            }
            catch (Exception ex)
            {
                HandleCriticalException(context, ex);
            }
        }

        // Hook chart mouse-up on UI thread; actual finalize work is marshaled back to strategy thread.
        private void AttachChartEvents()
        {
            UiInvoke(() =>
            {
                ChartControl currentChart = ChartControl;
                if (currentChart == null)
                    return;

                // Chart source changed: always detach old handlers first to avoid leaks.
                if (attachedChartControl != null && !ReferenceEquals(attachedChartControl, currentChart))
                {
                    ChartControl oldSource = attachedChartControl;
                    Dispatcher oldDispatcher = oldSource.Dispatcher;
                    chartEventsAttached = false;
                    attachedChartControl = null;

                    if (oldDispatcher != null && !oldDispatcher.HasShutdownStarted)
                    {
                        try
                        {
                            Action detachOld = () => DetachChartEventSubscriptions(oldSource);
                            if (oldDispatcher.CheckAccess())
                                detachOld();
                            else
                                oldDispatcher.InvokeAsync(detachOld, DispatcherPriority.Send);
                        }
                        catch (Exception ex)
                        {
                            LogUiError("AttachChartEvents.DetachOld", ex);
                        }
                    }
                    else
                    {
                        MarkChartDetachObserved("AttachChartEvents.DetachOldDispatcherUnavailable", false, false);
                    }
                }

                if (chartEventsAttached && ReferenceEquals(attachedChartControl, currentChart))
                    return;

                // Defensive dedupe before attach to avoid duplicate handlers on rapid re-enable/reload.
                DetachChartEventSubscriptions(currentChart);
                currentChart.PreviewMouseLeftButtonUp += ChartControl_PreviewMouseLeftButtonUp;
                currentChart.MouseLeftButtonUp += ChartControl_MouseLeftButtonUp;
                currentChart.MouseLeave += ChartControl_MouseLeave;
                currentChart.MouseMove += ChartControl_MouseMove;
                attachedChartControl = currentChart;
                chartEventsAttached = true;
                SetMilestone("AttachChartEvents");
                LogInfo("Chart events attached");
            });
        }

        // Detach chart mouse-up when disposing UI to avoid leaks.
        private void DetachChartEvents()
        {
            ChartControl source = attachedChartControl ?? ChartControl;
            if (source == null)
            {
                chartEventsAttached = false;
                attachedChartControl = null;
                MarkChartDetachObserved("DetachChartEvents.SourceNull", true, false);
                return;
            }

            Dispatcher dispatcher = source.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted)
            {
                MarkChartDetachObserved("DetachChartEvents.DispatcherUnavailable", false, false);
                chartEventsAttached = false;
                if (ReferenceEquals(attachedChartControl, source))
                    attachedChartControl = null;
                return;
            }

            try
            {
                Action detachAction = () =>
                {
                    try
                    {
                        bool hadTrackedSubscription = chartEventsAttached
                            && attachedChartControl != null
                            && ReferenceEquals(attachedChartControl, source);
                        // No pre-check guard here: unsubscription is idempotent and this avoids skip races.
                        DetachChartEventSubscriptions(source);
                        if (ReferenceEquals(attachedChartControl, source))
                        {
                            chartEventsAttached = false;
                            attachedChartControl = null;
                        }
                        if (hadTrackedSubscription)
                        {
                            SetMilestone("DetachChartEvents");
                            LogInfo("Chart events detached");
                        }
                    }
                    catch (Exception ex)
                    {
                        LogUiError("DetachChartEvents.Detach", ex);
                    }
                };

                if (dispatcher.CheckAccess())
                {
                    detachAction();
                }
                else
                {
                    dispatcher.InvokeAsync(detachAction, DispatcherPriority.Send);
                    LogInfo("Chart events detach scheduled");
                }
            }
            catch (Exception ex)
            {
                LogUiError("DetachChartEvents.Invoke", ex);
            }
        }

        private void ChartControl_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!chartEventsAttached)
                return;
            if (!(sender is ChartControl cc) || !ReferenceEquals(cc, attachedChartControl))
                return;

            SafeExecuteUI("ChartControl_PreviewMouseLeftButtonUp", () =>
            {
                MarkUserInteraction();
                dragFinalizePending = true;
                TriggerCustomEvent(_ => SafeExecuteTrade("PreviewMouseUp", FinalizeDrag), null);
            });
        }

        private void ChartControl_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!chartEventsAttached)
                return;
            if (!(sender is ChartControl cc) || !ReferenceEquals(cc, attachedChartControl))
                return;

            SafeExecuteUI("ChartControl_MouseLeftButtonUp", () =>
            {
                MarkUserInteraction();
                dragFinalizePending = true;
                TriggerCustomEvent(_ => SafeExecuteTrade("MouseUp", FinalizeDrag), null);
            });
        }

        private void ChartControl_MouseLeave(object sender, MouseEventArgs e)
        {
            if (!chartEventsAttached)
                return;
            if (!(sender is ChartControl cc) || !ReferenceEquals(cc, attachedChartControl))
                return;

            SafeExecuteUI("ChartControl_MouseLeave", () =>
            {
                MarkUserInteraction();
                dragFinalizePending = true;
                TriggerCustomEvent(_ => SafeExecuteTrade("MouseLeave", FinalizeDrag), null);
            });
        }

        private void ChartControl_MouseMove(object sender, MouseEventArgs e)
        {
            if (!chartEventsAttached)
                return;
            if (!(sender is ChartControl cc) || !ReferenceEquals(cc, attachedChartControl))
                return;

            SafeExecuteUI("ChartControl_MouseMove", () =>
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
                TriggerCustomEvent(_ => SafeExecuteTrade("MouseMoveDragPulse", HandleMouseDragPulse), null);
            });
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
                forceSizingDebugLog = true;

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
                        int did = HandleOrderFlowEvent(new OrderFlowEvent
                        {
                            Kind = OrderFlowEventKind.StopDragged,
                            Scope = "ChangeOrder-StopDragEnd",
                            Fingerprint = $"stopdrag|{stopOrder.OrderId}|{RoundForFp(stopPrice).ToString("G17", CultureInfo.InvariantCulture)}",
                            Order = stopOrder,
                            OrderQuantity = stopOrder.Quantity,
                            LimitPrice = stopOrder.LimitPrice,
                            StopOrderPrice = stopPrice
                        });
                        if (did > 0)
                            LogInfo($"SL modified -> {stopPrice:F2}");
                    }
                    if (IsOrderSafeForResize(targetOrder) && EnsureSelfCheckPassed())
                    {
                        HandleOrderFlowEvent(new OrderFlowEvent
                        {
                            Kind = OrderFlowEventKind.TargetDragged,
                            Scope = "ChangeOrder-TargetDragEnd",
                            Fingerprint = $"targetdrag|{targetOrder.OrderId}|{RoundForFp(targetPrice).ToString("G17", CultureInfo.InvariantCulture)}",
                            Order = targetOrder,
                            OrderQuantity = targetOrder.Quantity,
                            LimitPrice = targetPrice,
                            StopOrderPrice = targetOrder.StopPrice
                        });
                    }
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
            HandleCriticalException(context, ex);
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

        private void LogUiStateSnapshot(bool closeEnabled, bool beEnabled, bool trailEnabled, bool hasPosition, bool hasStop, bool stopActiveAny, string stopState)
        {
            if (LogLevelSetting != LogLevelOption.Debug)
                return;

            string signature = $"close={closeEnabled}|be={beEnabled}|trail={trailEnabled}|pos={hasPosition}|stopUi={hasStop}|stopActive={stopActiveAny}|stopState={stopState}";
            DateTime now = DateTime.Now;
            bool changed = !string.Equals(signature, lastUiStateSignature, StringComparison.Ordinal);
            bool heartbeat = (now - lastUiStateLogTime).TotalMilliseconds >= UiStateLogHeartbeatMs;
            if (!changed && !heartbeat)
                return;

            lastUiStateSignature = signature;
            lastUiStateLogTime = now;
            LogDebug($"[UI] State {signature}");
        }

        // Simple throttle to avoid chatty debug logs.
        private bool ShouldLogDebug()
        {
            if ((DateTime.Now - lastDebugLogTime).TotalSeconds < 1)
                return false;
            lastDebugLogTime = DateTime.Now;
            return true;
        }

        private bool ShouldLogSizingDebug(int qty, double stopTicks, double targetTicks, double entryRef, bool forceLog)
        {
            if (LogLevelSetting != LogLevelOption.Debug)
                return false;

            if (!forceLog && !VerboseSizingDebug)
                return false;

            DateTime now = DateTime.Now;
            if (!forceLog && (now - lastSizingDebugLogTime).TotalMilliseconds < SizingDebugThrottleMs)
                return false;

            double entryThreshold = Math.Max(sizing.TickSize(), 0.0000001);
            bool qtyChanged = qty != lastSizingDebugQty;
            bool stopChanged = double.IsNaN(lastSizingDebugStopTicks) || Math.Abs(stopTicks - lastSizingDebugStopTicks) >= 0.5;
            bool targetChanged = double.IsNaN(lastSizingDebugTargetTicks) || Math.Abs(targetTicks - lastSizingDebugTargetTicks) >= 0.5;
            bool entryChanged = double.IsNaN(lastSizingDebugEntryRef) || Math.Abs(entryRef - lastSizingDebugEntryRef) >= entryThreshold;

            if (!forceLog && !qtyChanged && !stopChanged && !targetChanged && !entryChanged)
                return false;

            lastSizingDebugLogTime = now;
            lastSizingDebugQty = qty;
            lastSizingDebugStopTicks = stopTicks;
            lastSizingDebugTargetTicks = targetTicks;
            lastSizingDebugEntryRef = entryRef;
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

        // BE/TRAIL should enable once broker has acknowledged protective stop (Accepted+).
        private bool IsStopOrderUiActive(Order order)
        {
            if (order == null)
                return false;

            return order.OrderState == OrderState.Accepted
                || order.OrderState == OrderState.Working
                || order.OrderState == OrderState.PartFilled;
        }

        private void MaybeRefreshUiForStopOrderUpdate(Order order)
        {
            if (order == null)
                return;

            bool isStop = order.Name == tags.StopSignal || order == stopOrder;
            if (!isStop)
                return;

            bool shouldRefresh = order.OrderState == OrderState.Accepted
                || order.OrderState == OrderState.Working
                || order.OrderState == OrderState.Cancelled
                || order.OrderState == OrderState.Rejected
                || order.OrderState == OrderState.Filled;
            if (!shouldRefresh)
                return;

            DateTime nowUtc = DateTime.UtcNow;
            if (lastStopUiRefreshUtc != DateTime.MinValue
                && (nowUtc - lastStopUiRefreshUtc).TotalMilliseconds < StopUiRefreshThrottleMs)
                return;

            lastStopUiRefreshUtc = nowUtc;
            UpdateUiState();
            if (LogLevelSetting == LogLevelOption.Debug)
            {
                bool hasPosition = Position.MarketPosition != MarketPosition.Flat;
                bool stopActiveUi = IsStopOrderUiActive(stopOrder);
                bool beEnabled = hasPosition && stopActiveUi;
                bool trailEnabled = hasPosition && stopActiveUi;
                string stopState = stopOrder == null ? "null" : stopOrder.OrderState.ToString();
                LogDebug($"[UI] StopUpdate hasPosition={hasPosition} stopState={stopState} stopActive={stopActiveUi} beEnabled={beEnabled} trailEnabled={trailEnabled}");
            }
        }

        // Broad "active" helper for generic order lifecycle checks (not BE/TRAIL UI gating).
        private bool IsOrderActive(Order order)
        {
            if (order == null)
                return false;

            switch (order.OrderState)
            {
                case OrderState.Submitted:
                case OrderState.Accepted:
                case OrderState.Working:
                case OrderState.PartFilled:
                case OrderState.ChangePending:
                case OrderState.ChangeSubmitted:
                    return true;
                default:
                    return false;
            }
        }

        // Cancel only when active; wraps in SafeExecute for consistent fatal handling.
        private void CancelActiveOrder(Order order, string context)
        {
            if (!IsOrderActive(order))
                return;

            RecordOrderAction($"{context} {order.Name}");
            SafeExecuteTrade(context, () => CancelOrder(order));
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

        #endregion
    }

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
