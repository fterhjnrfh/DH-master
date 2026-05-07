using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DH.Client.App.Controls;
using DH.Client.App.Data;
using DH.Client.App.Services;
using System.IO;

namespace DH.Client.App.Views;

public partial class TdmsViewerView : UserControl
{
    public static readonly StyledProperty<string?> SelectedFileProperty =
        AvaloniaProperty.Register<TdmsViewerView, string?>(nameof(SelectedFile));
    public static readonly StyledProperty<int> SelectedDeviceIdProperty =
        AvaloniaProperty.Register<TdmsViewerView, int>(nameof(SelectedDeviceId), 0);
    public static readonly StyledProperty<OnlineChannelManager?> OnlineChannelManagerProperty =
        AvaloniaProperty.Register<TdmsViewerView, OnlineChannelManager?>(nameof(OnlineChannelManager));

    public string? SelectedFile
    {
        get => GetValue(SelectedFileProperty);
        set => SetValue(SelectedFileProperty, value);
    }

    public int SelectedDeviceId
    {
        get => GetValue(SelectedDeviceIdProperty);
        set => SetValue(SelectedDeviceIdProperty, value);
    }

    public OnlineChannelManager? OnlineChannelManager
    {
        get => GetValue(OnlineChannelManagerProperty);
        set => SetValue(OnlineChannelManagerProperty, value);
    }

    public TdmsViewerView()
    {
        InitializeComponent();
        var vm = new ViewModels.TdmsViewerViewModel();
        DataContext = vm;

        var skView = this.FindControl<SkiaMultiChannelView>("TdmsCurveView");
        var replayOverview = this.FindControl<SkiaMultiChannelView>("ReplayOverviewView");
        var replayOverviewOverlay = this.FindControl<ReplayNavigatorOverlay>("ReplayOverviewOverlay");
        if (skView is not null)
        {
            skView.GetDiagnosticTag = () => "tdms-replay-main";
            var replayWindowDebounce = new Avalonia.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(160)
            };
            double pendingReplayWindowStart = 0.0;
            double pendingReplayWindowEnd = 0.0;
            bool suppressReplayWindowRequest = false;
            bool suppressNavigatorRequest = false;
            float lastReplayZoomX = 1.0f;
            int lastReplayViewLeft = 0;
            int lastReplayViewCount = 0;
            Point? replayCursorPressPoint = null;
            double GetReplaySecondsFromPoint(Point point)
            {
                double width = Math.Max(1.0, skView.Bounds.Width);
                double fraction = Math.Clamp(point.X / width, 0.0, 1.0);
                return vm.ReplayWindowStartSeconds
                    + (vm.ReplayWindowEndSeconds - vm.ReplayWindowStartSeconds) * fraction;
            }

            replayWindowDebounce.Tick += async (_, _) =>
            {
                replayWindowDebounce.Stop();
                if (suppressReplayWindowRequest)
                {
                    return;
                }

                suppressReplayWindowRequest = true;
                try
                {
                    bool loaded = await vm.LoadPersistedReplayWindowAsync(
                        pendingReplayWindowStart,
                        pendingReplayWindowEnd);
                    if (loaded)
                    {
                        skView.ResetXViewPreservingY();
                    }
                }
                finally
                {
                    suppressReplayWindowRequest = false;
                }
            };

            var replayNavigatorDebounce = new Avalonia.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(350)
            };
            double pendingNavigatorCenter = 0.0;
            bool navigatorSeekInFlight = false;
            bool navigatorSeekQueuedWhileInFlight = false;
            replayNavigatorDebounce.Tick += async (_, _) =>
            {
                replayNavigatorDebounce.Stop();
                if (suppressNavigatorRequest || !vm.HasPersistedReplay)
                {
                    return;
                }

                if (navigatorSeekInFlight)
                {
                    navigatorSeekQueuedWhileInFlight = true;
                    return;
                }

                navigatorSeekInFlight = true;
                suppressReplayWindowRequest = true;
                try
                {
                    bool loaded = await vm.SeekPersistedReplayCenterAsync(pendingNavigatorCenter);
                    if (loaded)
                    {
                        skView.ResetXViewPreservingY();
                    }
                }
                finally
                {
                    suppressReplayWindowRequest = false;
                    navigatorSeekInFlight = false;
                    if (navigatorSeekQueuedWhileInFlight && vm.HasPersistedReplay)
                    {
                        navigatorSeekQueuedWhileInFlight = false;
                        replayNavigatorDebounce.Stop();
                        replayNavigatorDebounce.Start();
                    }
                }
            };

            if (replayOverview is not null)
            {
                replayOverview.GetDiagnosticTag = () => "tdms-replay-overview";
                replayOverview.UseTimeAxis = false;
                replayOverview.UseDataXValues = true;
                replayOverview.FormatXLabel = FormatSecondsLabel;
                replayOverview.ScrollMode = false;
                replayOverview.ShowLegend = false;
                replayOverview.DesiredXTicks = 6;
                replayOverview.DesiredYTicks = 2;
                replayOverview.UseExtremaAggregation = false;
                replayOverview.UseEnvelopeRepresentativePoints = true;
                replayOverview.DataProvider = () => Array.Empty<DH.Contracts.Models.CurvePoint>();
                replayOverview.MultiChannelDataProvider = () => vm.ReplayOverviewData;
                replayOverview.ChannelColorsMap = vm.ChannelColorsMap;

            }

            // TDMS查看器使用离线数据的真实X值（秒），同时保留拖动和缩放交互。
            skView.UseTimeAxis = false;
            skView.UseDataXValues = true;
            skView.FormatXLabel = FormatSecondsLabel;
            skView.ScrollMode = false;   // 关闭滚动窗口，启用全范围与交互视口
            skView.DesiredXTicks = 10;  // 增加X轴刻度数，更精细
            skView.DesiredYTicks = 8;   // 增加Y轴刻度数
            skView.ShowLegend = true;   // 显示图例
            skView.UseExtremaAggregation = true; // 保留数据尖峰
            
            skView.DataProvider = () => vm.CurrentCurveData;
            skView.MultiChannelDataProvider = () => vm.CurrentMultiChannelData;
            skView.ChannelColorsMap = vm.ChannelColorsMap;
            vm.CurveDataUpdated += () => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                suppressNavigatorRequest = true;
                skView.ChannelColorsMap = vm.ChannelColorsMap;
                skView.UseExtremaAggregation = !vm.HasPersistedReplay;
                skView.UseEnvelopeRepresentativePoints = vm.HasPersistedReplay;
                skView.ShowSingleCursor = vm.HasSingleCursor;
                skView.SingleCursorXValue = vm.SingleCursorSeconds;
                skView.SingleCursorChannelValues = vm.ReplayCursorChannelValues;
                skView.InvalidateVisual();
                if (replayOverview is not null)
                {
                    replayOverview.ChannelColorsMap = vm.ChannelColorsMap;
                    replayOverview.UseExtremaAggregation = false;
                    replayOverview.UseEnvelopeRepresentativePoints = true;
                    replayOverview.InvalidateVisual();
                }
                suppressNavigatorRequest = false;
            });

            skView.AddHandler(InputElement.PointerMovedEvent, (_, e) =>
            {
                if (!vm.HasPersistedReplay)
                {
                    skView.ShowHoverCursor = false;
                    return;
                }

                skView.ShowHoverCursor = true;
                double hoverSeconds = GetReplaySecondsFromPoint(e.GetPosition(skView));
                skView.HoverCursorXValue = hoverSeconds;
                skView.HoverCursorChannelValues = vm.BuildCursorChannelValues(hoverSeconds);
                skView.InvalidateVisual();
            }, RoutingStrategies.Tunnel, handledEventsToo: true);

            skView.PointerExited += (_, _) =>
            {
                skView.ShowHoverCursor = false;
                skView.HoverCursorChannelValues = null;
                skView.InvalidateVisual();
            };

            skView.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
            {
                if (!vm.HasPersistedReplay || !e.GetCurrentPoint(skView).Properties.IsLeftButtonPressed)
                {
                    replayCursorPressPoint = null;
                    return;
                }

                replayCursorPressPoint = e.GetPosition(skView);
            }, RoutingStrategies.Tunnel, handledEventsToo: true);

            skView.AddHandler(InputElement.PointerReleasedEvent, (_, e) =>
            {
                if (!vm.HasPersistedReplay || replayCursorPressPoint is not { } startPoint)
                {
                    replayCursorPressPoint = null;
                    return;
                }

                Point endPoint = e.GetPosition(skView);
                replayCursorPressPoint = null;
                double dx = endPoint.X - startPoint.X;
                double dy = endPoint.Y - startPoint.Y;
                if ((dx * dx) + (dy * dy) > 25.0)
                {
                    return;
                }

                double cursorSeconds = GetReplaySecondsFromPoint(endPoint);
                vm.SetSingleCursorSeconds(cursorSeconds);
                skView.ShowSingleCursor = vm.HasSingleCursor;
                skView.SingleCursorXValue = vm.SingleCursorSeconds;
                skView.SingleCursorChannelValues = vm.ReplayCursorChannelValues;
                skView.InvalidateVisual();
                e.Handled = true;
            }, RoutingStrategies.Bubble, handledEventsToo: true);

            if (replayOverviewOverlay is not null)
            {
                bool overviewDragging = false;
                void QueueOverviewSeek(Point point)
                {
                    double width = Math.Max(1.0, replayOverviewOverlay.Bounds.Width);
                    double fraction = Math.Clamp(point.X / width, 0.0, 1.0);
                    pendingNavigatorCenter = vm.ReplayTotalDurationSeconds * fraction;
                    replayNavigatorDebounce.Stop();
                    replayNavigatorDebounce.Start();
                }

                async void ApplyOverviewSeekNow(Point point)
                {
                    double width = Math.Max(1.0, replayOverviewOverlay.Bounds.Width);
                    double fraction = Math.Clamp(point.X / width, 0.0, 1.0);
                    pendingNavigatorCenter = vm.ReplayTotalDurationSeconds * fraction;
                    replayNavigatorDebounce.Stop();

                    if (suppressNavigatorRequest || !vm.HasPersistedReplay)
                    {
                        return;
                    }

                    if (navigatorSeekInFlight)
                    {
                        navigatorSeekQueuedWhileInFlight = true;
                        return;
                    }

                    navigatorSeekInFlight = true;
                    suppressReplayWindowRequest = true;
                    try
                    {
                        bool loaded = await vm.SeekPersistedReplayCenterAsync(pendingNavigatorCenter);
                        if (loaded)
                        {
                            skView.ResetXViewPreservingY();
                        }
                    }
                    finally
                    {
                        suppressReplayWindowRequest = false;
                        navigatorSeekInFlight = false;
                        if (navigatorSeekQueuedWhileInFlight && vm.HasPersistedReplay)
                        {
                            navigatorSeekQueuedWhileInFlight = false;
                            replayNavigatorDebounce.Stop();
                            replayNavigatorDebounce.Start();
                        }
                    }
                }

                replayOverviewOverlay.PointerPressed += (_, e) =>
                {
                    if (!vm.HasPersistedReplay)
                    {
                        return;
                    }

                    overviewDragging = true;
                    e.Pointer.Capture(replayOverviewOverlay);
                    QueueOverviewSeek(e.GetPosition(replayOverviewOverlay));
                    e.Handled = true;
                };

                replayOverviewOverlay.PointerMoved += (_, e) =>
                {
                    if (!overviewDragging || !vm.HasPersistedReplay)
                    {
                        return;
                    }

                    QueueOverviewSeek(e.GetPosition(replayOverviewOverlay));
                    e.Handled = true;
                };

                replayOverviewOverlay.PointerReleased += (_, e) =>
                {
                    if (!overviewDragging)
                    {
                        return;
                    }

                    overviewDragging = false;
                    e.Pointer.Capture(null);
                    ApplyOverviewSeekNow(e.GetPosition(replayOverviewOverlay));
                    e.Handled = true;
                };

                replayOverviewOverlay.AddHandler(InputElement.PointerWheelChangedEvent, async (_, e) =>
                {
                    if (!vm.HasPersistedReplay)
                    {
                        return;
                    }

                    double width = Math.Max(1.0, replayOverviewOverlay.Bounds.Width);
                    double anchor = Math.Clamp(e.GetPosition(replayOverviewOverlay).X / width, 0.0, 1.0);
                    e.Handled = true;
                    suppressReplayWindowRequest = true;
                    try
                    {
                        bool loaded = await vm.ZoomPersistedReplayWindowAsync(e.Delta.Y, anchor);
                        if (loaded)
                        {
                            skView.ResetXViewPreservingY();
                        }
                    }
                    finally
                    {
                        suppressReplayWindowRequest = false;
                    }
                }, RoutingStrategies.Tunnel);
            }

            skView.AddHandler(InputElement.PointerWheelChangedEvent, async (_, e) =>
            {
                if (!vm.HasPersistedReplay)
                {
                    return;
                }

                var mods = e.KeyModifiers;
                if (mods.HasFlag(KeyModifiers.Control) || mods.HasFlag(KeyModifiers.Shift))
                {
                    return;
                }

                double width = Math.Max(1.0, skView.Bounds.Width);
                double anchor = Math.Clamp(e.GetPosition(skView).X / width, 0.0, 1.0);
                e.Handled = true;
                suppressReplayWindowRequest = true;
                try
                {
                    bool loaded = await vm.ZoomPersistedReplayWindowAsync(e.Delta.Y, anchor);
                    if (loaded)
                    {
                        skView.ResetXViewPreservingY();
                    }
                }
                finally
                {
                    suppressReplayWindowRequest = false;
                }
            }, RoutingStrategies.Tunnel);

            // 视图状态记录：交互变化时写入到 VM
            skView.ViewStateChanged += (state) =>
            {
                vm.LastView = new ViewModels.TdmsViewerViewModel.ViewState
                {
                    ZoomX = state.ZoomX,
                    ZoomY = state.ZoomY,
                    ViewLeft = state.ViewLeft,
                    ViewCount = state.ViewCount
                };

                bool replayXChanged =
                    Math.Abs(lastReplayZoomX - state.ZoomX) > 0.0001f ||
                    lastReplayViewLeft != state.ViewLeft ||
                    lastReplayViewCount != state.ViewCount;
                lastReplayZoomX = state.ZoomX;
                lastReplayViewLeft = state.ViewLeft;
                lastReplayViewCount = state.ViewCount;

                if (replayXChanged
                    && !suppressReplayWindowRequest
                    && vm.TryGetPersistedReplayWindow(vm.LastView, out double windowStart, out double windowEnd))
                {
                    pendingReplayWindowStart = windowStart;
                    pendingReplayWindowEnd = windowEnd;
                    replayWindowDebounce.Stop();
                    replayWindowDebounce.Start();
                }
            };

            // 如存在上次视图状态，初始化应用
            if (vm.LastView is not null)
            {
                skView.SetViewState(new SkiaMultiChannelView.ViewState
                {
                    ZoomX = vm.LastView.ZoomX,
                    ZoomY = vm.LastView.ZoomY,
                    ViewLeft = vm.LastView.ViewLeft,
                    ViewCount = vm.LastView.ViewCount
                });
            }
        }

        this.PropertyChanged += (s, e) =>
        {
            if (e.Property == SelectedFileProperty)
                vm.SelectedFile = e.NewValue as string;
            else if (e.Property == SelectedDeviceIdProperty)
                vm.DeviceFilterId = (int)(e.NewValue ?? 0);
            else if (e.Property == OnlineChannelManagerProperty)
                vm.OnlineChannelManager = e.NewValue as OnlineChannelManager;
        };
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private static string FormatSecondsLabel(double seconds)
    {
        double value = Math.Abs(seconds);
        if (value >= 1000)
        {
            return $"{seconds:0} s";
        }

        if (value >= 100)
        {
            return $"{seconds:0.0} s";
        }

        if (value >= 10)
        {
            return $"{seconds:0.00} s";
        }

        return $"{seconds:0.000} s";
    }

    private void OnResetViewClicked(object? sender, RoutedEventArgs e)
    {
        var skView = this.FindControl<SkiaMultiChannelView>("TdmsCurveView");
        skView?.ResetView();
    }

    // 跳转至末端按钮事件处理（平滑滚动并显示进度）
    private void OnJumpToEndClicked(object? sender, RoutedEventArgs e)
    {
        var skView = this.FindControl<SkiaMultiChannelView>("TdmsCurveView");
        if (skView is null) return;
        if (DataContext is not ViewModels.TdmsViewerViewModel vm) return;

        // 绑定进度事件，仅绑定一次以避免重复累加
        skView.JumpingStateChanged -= OnJumpingStateChanged;
        skView.JumpProgressChanged -= OnJumpProgressChanged;
        skView.JumpingStateChanged += OnJumpingStateChanged;
        skView.JumpProgressChanged += OnJumpProgressChanged;

        vm.IsJumping = true;
        vm.JumpProgress = 0;
        skView.JumpToEndSmooth(TimeSpan.FromMilliseconds(600));

        void OnJumpingStateChanged(bool running)
        {
            vm.IsJumping = running;
            if (!running) vm.JumpProgress = 100;
        }
        void OnJumpProgressChanged(double p)
        {
            vm.JumpProgress = (int)Math.Clamp(Math.Round(p), 0, 100);
        }
    }

    // 会话目录选择按钮事件处理
    private async void OnPickSessionClicked(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null)
        {
            return;
        }

        var result = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择 TDMS 会话目录",
            AllowMultiple = false
        });

        string? path = result.Count > 0 ? result[0].Path.LocalPath : null;
        if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
        {
            SelectedFile = path;
        }
    }

    // 单文件选择按钮事件处理。保留给普通单文件 TDMS/TDM；直存会话优先使用“选择会话”。
    private async void OnPickFileClicked(object? sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog()
        {
            Title = "选择 TDMS/TDM 文件",
            AllowMultiple = false,
            Filters = new System.Collections.Generic.List<FileDialogFilter>
            {
                new FileDialogFilter { Name = "TDMS/TDM 文件", Extensions = new System.Collections.Generic.List<string> { "tdms", "tdm" } },
                new FileDialogFilter { Name = "所有文件", Extensions = new System.Collections.Generic.List<string> { "*" } }
            }
        };
        try
        {
            var initialDir = TryGetDataDir();
            if (!string.IsNullOrWhiteSpace(initialDir) && Directory.Exists(initialDir))
                dlg.Directory = initialDir;
        }
        catch { }

        var win = this.VisualRoot as Window;
        if (win is null) return;

        var result = await dlg.ShowAsync(win);
        var fp = result?.Length > 0 ? result[0] : null;
        if (!string.IsNullOrWhiteSpace(fp) && File.Exists(fp))
        {
            SelectedFile = fp; // 触发属性转发到 ViewModel
        }
    }

    // 尝试解析 data 目录（优先工作区路径）
    private static string? TryGetDataDir()
    {
        var candidates = new[]
        {
            AppDataPaths.DataRoot,
            AppDataPaths.ResolveStoragePath("data"),
            Path.Combine(AppContext.BaseDirectory, "data"),
            Path.Combine(Directory.GetCurrentDirectory(), "data")
        };
        foreach (var p in candidates)
        {
            try
            {
                if (Directory.Exists(p)) return p;
            }
            catch { }
        }
        return null;
    }
}
