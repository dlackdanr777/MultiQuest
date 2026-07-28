using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using LibVLCSharp.Shared;

using Button = System.Windows.Controls.Button;
using MessageBox = System.Windows.MessageBox;
using Window = System.Windows.Window;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace MultiQuest_Management
{
    public partial class DeviceWindow : Window, INotifyPropertyChanged
    {
        private readonly MainWindow _mainWindow;
        private readonly LibVLC _rtspLibVlc;

        /*
         * 화면 상태 동기화, RTSP 재생성, 수동 새로고침이 동시에 MediaPlayer를
         * Dispose하지 않도록 직렬화합니다.
         *
         * 중요:
         * Window가 닫힐 때 이 SemaphoreSlim을 Dispose하지 않습니다.
         * 닫기 직전에 이미 진입한 비동기 작업이 finally에서 Release()할 수 있기
         * 때문입니다. SemaphoreSlim은 AvailableWaitHandle을 사용하지 않는 한 별도
         * 네이티브 핸들을 만들지 않으므로 Window 수명 종료 후 GC에 맡기는 편이
         * ObjectDisposedException보다 안전합니다.
         */
        private readonly SemaphoreSlim _syncLock = new(1, 1);
        private readonly CancellationTokenSource _lifetimeCts = new();

        private CancellationTokenSource _retryCts;
        private CancellationTokenSource _runningDebounceCts;

        // 0=open, 1=closing, 2=closed
        private int _closeState;
        private int _commandInProgress;

        private Device _currentDevice;
        public Device CurrentDevice
        {
            get => _currentDevice;
            private set
            {
                if (_currentDevice == value) return;
                _currentDevice = value;
                OnPropertyChanged(nameof(CurrentDevice));
            }
        }

        private MediaPlayer _mediaPlayer;
        public MediaPlayer MediaPlayer
        {
            get => _mediaPlayer;
            private set
            {
                if (_mediaPlayer == value) return;
                _mediaPlayer = value;
                OnPropertyChanged(nameof(MediaPlayer));
            }
        }

        private Media _media;
        private DispatcherTimer _statusTimer;

        private string _lastPlayedUrl;
        private bool _isRefreshing;
        private bool _suppressDeviceEvents;
        private volatile bool _forceSoftwareDecode;

        private int _errorCount;
        private DateTime _lastReconnectUtc = DateTime.MinValue;
        private DateTime _connectStartedUtc = DateTime.MinValue;

        private string _pendingRunningUrl;

        private const int StatusPollSeconds = 5;

        // 개인 화면은 큰 타일보다 화질/안정 균형을 조금 더 좋게 둡니다.
        private const int PersonalNetworkCachingMs = 900;
        private const int PersonalLiveCachingMs = 900;

        // VLC 연결 직후 초기 노이즈 구간입니다.
        private const int StartupNoiseWindowSeconds = 12;

        // RUNNING 전환 후 RTSP 서버가 실제 준비될 시간을 흡수합니다.
        private const int RunningDebounceMs = 1_500;

        // 자동 재연결 과도 실행 방지
        private const int MinReconnectIntervalMs = 2_000;

        private bool IsClosingOrClosed =>
            Volatile.Read(ref _closeState) != 0;

        public DeviceWindow(
            MainWindow mainWindow,
            Device device,
            LibVLC rtspLibVlc)
        {
            InitializeComponent();

            _mainWindow =
                mainWindow ??
                throw new ArgumentNullException(nameof(mainWindow));

            _rtspLibVlc =
                rtspLibVlc ??
                throw new ArgumentNullException(nameof(rtspLibVlc));

            CurrentDevice =
                device ??
                throw new ArgumentNullException(nameof(device));

            DataContext = this;

            Loaded += Window_Loaded;
            Closing += Window_Closing;
            Closed += Window_Closed;

            CurrentDevice.PropertyChanged +=
                CurrentDevice_PropertyChanged;
        }

        public void SetDevice(Device device)
        {
            if (device == null)
                throw new ArgumentNullException(nameof(device));

            if (IsClosingOrClosed)
                return;

            try
            {
                if (CurrentDevice != null)
                {
                    CurrentDevice.PropertyChanged -=
                        CurrentDevice_PropertyChanged;
                }
            }
            catch
            {
            }

            CurrentDevice = device;
            DataContext = this;

            CurrentDevice.PropertyChanged +=
                CurrentDevice_PropertyChanged;

            _ = RunSerializedAsync(
                () => SyncFromAgentAndApplyAsync("SetDevice"));
        }

        private async void Window_Loaded(
            object sender,
            RoutedEventArgs e)
        {
            if (IsClosingOrClosed)
                return;

            Debug.WriteLine(
                $"[DeviceWindow] Loaded. " +
                $"Device={CurrentDevice.Name}, " +
                $"Agent={CurrentDevice.AgentHost}:" +
                $"{CurrentDevice.AgentStatusPort}, " +
                $"state={CurrentDevice.StreamState}, " +
                $"url={CurrentDevice.RtspUrl}");

            StartStatusTimer();

            try
            {
                await RunSerializedAsync(
                    () => SyncFromAgentAndApplyAsync(
                        "Window_Loaded"));
            }
            catch (OperationCanceledException)
                when (IsClosingOrClosed)
            {
                // 닫기 중에는 정상 취소입니다.
            }
            catch (Exception ex)
            {
                // async-void 이벤트에서 예외가 Dispatcher까지 전파되지 않게 합니다.
                Debug.WriteLine(
                    $"[DeviceWindow] Window_Loaded ignored error: {ex}");
            }
        }

        private void Window_Closing(
            object sender,
            CancelEventArgs e)
        {
            if (Interlocked.CompareExchange(
                    ref _closeState,
                    1,
                    0) != 0)
            {
                return;
            }

            Debug.WriteLine(
                $"[DeviceWindow] Closing. device={CurrentDevice?.Name}");

            try
            {
                _lifetimeCts.Cancel();
            }
            catch
            {
            }

            StopStatusTimer();
            CancelRetry();
            CancelRunningDebounce();
        }

        private void Window_Closed(
            object sender,
            EventArgs e)
        {
            Interlocked.Exchange(ref _closeState, 2);

            try
            {
                CurrentDevice.PropertyChanged -=
                    CurrentDevice_PropertyChanged;
            }
            catch
            {
            }

            try
            {
                Loaded -= Window_Loaded;
                Closing -= Window_Closing;
                Closed -= Window_Closed;
            }
            catch
            {
            }

            StopStatusTimer();
            CancelRetry();
            CancelRunningDebounce();

            /*
             * Closed는 UI Dispatcher에서 발생하므로 동기 정리가 안전합니다.
             * _syncLock은 여기에서 Dispose하지 않습니다.
             */
            StopMirrorOnUiThread();

            Debug.WriteLine(
                $"[DeviceWindow] Closed safely. device={CurrentDevice?.Name}");
        }

        private void StartStatusTimer()
        {
            if (_statusTimer != null || IsClosingOrClosed)
                return;

            _statusTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(
                    StatusPollSeconds)
            };

            _statusTimer.Tick += StatusTimer_Tick;
            _statusTimer.Start();
        }

        private void StopStatusTimer()
        {
            var timer = _statusTimer;
            _statusTimer = null;

            if (timer == null)
                return;

            try
            {
                timer.Stop();
                timer.Tick -= StatusTimer_Tick;
            }
            catch
            {
            }
        }

        private async void StatusTimer_Tick(
            object sender,
            EventArgs e)
        {
            if (IsClosingOrClosed)
                return;

            try
            {
                await RunSerializedAsync(
                    () => SyncFromAgentAndApplyAsync(
                        "timer"));
            }
            catch (OperationCanceledException)
                when (IsClosingOrClosed)
            {
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[DeviceWindow] status timer error ignored: {ex}");
            }
        }

        private async void CurrentDevice_PropertyChanged(
            object sender,
            PropertyChangedEventArgs e)
        {
            if (IsClosingOrClosed ||
                _suppressDeviceEvents)
            {
                return;
            }

            if (e.PropertyName != nameof(Device.StreamState) &&
                e.PropertyName != nameof(Device.RtspUrl) &&
                e.PropertyName != nameof(Device.AgentHost) &&
                e.PropertyName != nameof(Device.AgentStatusPort))
            {
                return;
            }

            try
            {
                await RunSerializedAsync(
                    () => ApplyCurrentDeviceStateAsync(
                        $"PropertyChanged:{e.PropertyName}"));
            }
            catch (OperationCanceledException)
                when (IsClosingOrClosed)
            {
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[DeviceWindow] property-change error ignored: {ex}");
            }
        }

        private async Task RunSerializedAsync(
            Func<Task> action)
        {
            if (action == null ||
                IsClosingOrClosed)
            {
                return;
            }

            bool entered = false;

            try
            {
                entered = await _syncLock.WaitAsync(
                    millisecondsTimeout: 0,
                    cancellationToken: _lifetimeCts.Token);

                if (!entered ||
                    IsClosingOrClosed)
                {
                    return;
                }

                await action();
            }
            catch (OperationCanceledException)
                when (_lifetimeCts.IsCancellationRequested ||
                      IsClosingOrClosed)
            {
                // Window 닫기에서 발생한 정상 취소입니다.
            }
            catch (ObjectDisposedException)
                when (IsClosingOrClosed)
            {
                // 이전 버전에서 Semaphore/Dispatcher가 닫기 중 Dispose된 경우를 방어합니다.
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[DeviceWindow] serialized action failed: {ex}");
            }
            finally
            {
                if (entered)
                {
                    try
                    {
                        _syncLock.Release();
                    }
                    catch (ObjectDisposedException)
                    {
                        // 현재 수정본은 _syncLock을 Dispose하지 않지만,
                        // 닫기 경쟁에 대한 최종 방어입니다.
                    }
                    catch (SemaphoreFullException ex)
                    {
                        Debug.WriteLine(
                            $"[DeviceWindow] sync release ignored: {ex.Message}");
                    }
                }
            }
        }

        private async Task SyncFromAgentAndApplyAsync(
            string reason)
        {
            if (IsClosingOrClosed)
                return;

            if (string.IsNullOrWhiteSpace(
                    CurrentDevice.AgentHost))
            {
                await ApplyCurrentDeviceStateAsync(
                    $"{reason}:no-agent");
                return;
            }

            int port =
                CurrentDevice.AgentStatusPort > 0
                    ? CurrentDevice.AgentStatusPort
                    : 18080;

            QuestAgentInfo status = null;

            try
            {
                status = await AgentApi.GetStatusFastAsync(
                    CurrentDevice.AgentHost,
                    port,
                    _lifetimeCts.Token);
            }
            catch (OperationCanceledException)
                when (_lifetimeCts.IsCancellationRequested ||
                      IsClosingOrClosed)
            {
                return;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[DeviceWindow] status poll failed: {ex.Message}");
            }

            if (IsClosingOrClosed)
                return;

            if (status != null)
            {
                bool urlChanged = false;

                try
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (IsClosingOrClosed)
                            return;

                        _suppressDeviceEvents = true;

                        try
                        {
                            if (!string.IsNullOrWhiteSpace(
                                    status.RtspUrl) &&
                                !string.Equals(
                                    CurrentDevice.RtspUrl,
                                    status.RtspUrl,
                                    StringComparison.OrdinalIgnoreCase))
                            {
                                urlChanged = true;

                                Debug.WriteLine(
                                    $"[DeviceWindow] RTSP URL 변경: " +
                                    $"{CurrentDevice.RtspUrl} → " +
                                    $"{status.RtspUrl}");
                            }

                            if (!string.IsNullOrWhiteSpace(
                                    status.RtspUrl))
                            {
                                CurrentDevice.RtspUrl =
                                    status.RtspUrl;
                            }

                            if (!string.IsNullOrWhiteSpace(
                                    status.StreamState))
                            {
                                CurrentDevice.StreamState =
                                    status.StreamState;
                            }

                            if (status.Battery >= 0)
                            {
                                CurrentDevice.BatteryLevel =
                                    status.Battery;
                            }

                            CurrentDevice.IsCharging =
                                status.IsCharging;

                            if (!string.IsNullOrWhiteSpace(
                                    status.ChargingStatus))
                            {
                                CurrentDevice.ChargingStatus =
                                    status.ChargingStatus;
                            }

                            if (!string.IsNullOrWhiteSpace(
                                    status.DeviceName))
                            {
                                CurrentDevice.Name =
                                    status.DeviceName.Trim();
                            }
                        }
                        finally
                        {
                            _suppressDeviceEvents = false;
                        }
                    });
                }
                catch (TaskCanceledException)
                    when (IsClosingOrClosed)
                {
                    return;
                }
                catch (InvalidOperationException)
                    when (IsClosingOrClosed)
                {
                    return;
                }

                if (IsClosingOrClosed)
                    return;

                if (urlChanged &&
                    string.Equals(
                        status.StreamState,
                        "RUNNING",
                        StringComparison.OrdinalIgnoreCase))
                {
                    await StopMirrorAsync();

                    await Task.Delay(
                        700,
                        _lifetimeCts.Token);

                    if (IsClosingOrClosed)
                        return;

                    // 이미 _syncLock 내부이므로 RunSerializedAsync를 재호출하지 않습니다.
                    await StartRtspMirrorAsync(
                        forceRestart: true,
                        reason: "url-changed");

                    return;
                }
            }

            await ApplyCurrentDeviceStateAsync(reason);
        }

        private async Task ApplyCurrentDeviceStateAsync(
            string reason)
        {
            if (IsClosingOrClosed)
                return;

            string state =
                CurrentDevice.StreamState
                    ?.Trim()
                    .ToUpperInvariant();

            switch (state)
            {
                case "RUNNING":
                    if (string.IsNullOrWhiteSpace(
                            CurrentDevice.RtspUrl))
                    {
                        return;
                    }

                    if (Volatile.Read(ref _errorCount) > 3)
                    {
                        Interlocked.Exchange(ref _errorCount, 0);
                        _forceSoftwareDecode = false;
                    }

                    ScheduleRunningDebounce(
                        CurrentDevice.RtspUrl,
                        reason);
                    break;

                case "STARTING":
                case "WAITING_PERMISSION":
                    CancelRunningDebounce();
                    await StopMirrorAsync();
                    SetWindowStatus("화면공유 준비 중");
                    break;

                case "STOPPED":
                case "IDLE":
                    CancelRunningDebounce();
                    await StopMirrorAsync();
                    SetWindowStatus("화면공유 중지됨");
                    break;

                case "ERROR":
                case "FROZEN":
                    CancelRunningDebounce();
                    await StopMirrorAsync();
                    SetWindowStatus(
                        $"화면공유 상태: {state}");
                    break;

                default:
                    if (!string.IsNullOrWhiteSpace(
                            CurrentDevice.RtspUrl) &&
                        (MediaPlayer == null ||
                         !MediaPlayer.IsPlaying))
                    {
                        ScheduleRunningDebounce(
                            CurrentDevice.RtspUrl,
                            $"unknown_state:{reason}");
                    }
                    break;
            }
        }

        private async Task StartRtspMirrorAsync(
            bool forceRestart,
            string reason)
        {
            if (IsClosingOrClosed)
                return;

            string currentUrl = CurrentDevice.RtspUrl;

            if (string.IsNullOrWhiteSpace(currentUrl))
            {
                Debug.WriteLine(
                    "[DeviceWindow] Start skipped: RtspUrl empty");
                return;
            }

            if (!forceRestart &&
                string.Equals(
                    _lastPlayedUrl,
                    currentUrl,
                    StringComparison.OrdinalIgnoreCase) &&
                MediaPlayer != null &&
                MediaPlayer.IsPlaying)
            {
                Debug.WriteLine(
                    $"[DeviceWindow] Already playing. url={currentUrl}");
                return;
            }

            DateTime now = DateTime.UtcNow;

            if ((now - _lastReconnectUtc).TotalMilliseconds <
                MinReconnectIntervalMs)
            {
                Debug.WriteLine(
                    "[DeviceWindow] Reconnect skipped by cooldown");
                return;
            }

            _lastReconnectUtc = now;

            Debug.WriteLine(
                $"[DeviceWindow] Start RTSP. reason={reason}, " +
                $"url={currentUrl}, swDecode={_forceSoftwareDecode}");

            await StopMirrorAsync();

            await Task.Delay(
                250,
                _lifetimeCts.Token);

            if (IsClosingOrClosed)
                return;

            try
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    if (IsClosingOrClosed)
                        return;

                    try
                    {
                        var player = new MediaPlayer(
                            _rtspLibVlc)
                        {
                            EnableHardwareDecoding =
                                !_forceSoftwareDecode,
                            Mute = true
                        };

                        AttachMediaPlayerEvents(player);

                        var media = new Media(
                            _rtspLibVlc,
                            currentUrl,
                            FromType.FromLocation);

                        AddPersonalQualityOptions(media);

                        MediaPlayer = player;
                        _media = media;
                        RtspVideoView.MediaPlayer = player;

                        player.Play(media);

                        _lastPlayedUrl = currentUrl;
                        _connectStartedUtc = DateTime.UtcNow;

                        SetWindowStatus(
                            _forceSoftwareDecode
                                ? "개인 화면 재생 중 - SW 디코딩"
                                : "개인 화면 재생 중 - 고화질");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine(
                            $"[DeviceWindow] RTSP start failed: {ex}");

                        SetWindowStatus("RTSP 시작 실패");
                        ScheduleRetry("start_failed");
                    }
                });
            }
            catch (TaskCanceledException)
                when (IsClosingOrClosed)
            {
            }
            catch (InvalidOperationException)
                when (IsClosingOrClosed)
            {
            }
        }

        private void AttachMediaPlayerEvents(
            MediaPlayer player)
        {
            player.Playing += (_, __) =>
            {
                if (IsClosingOrClosed)
                    return;

                Interlocked.Exchange(ref _errorCount, 0);

                SafeDispatcherBeginInvoke(() =>
                {
                    SetWindowStatus(
                        _forceSoftwareDecode
                            ? "개인 화면 재생 중 - SW 디코딩"
                            : "개인 화면 재생 중 - 고화질");
                });
            };

            player.Buffering += (_, __) => { };
            player.Paused += (_, __) => { };
            player.Stopped += (_, __) => { };

            player.EncounteredError += (_, __) =>
            {
                if (IsClosingOrClosed ||
                    IsInStartupNoiseWindow())
                {
                    return;
                }

                ScheduleRetry("vlc_error");
            };

            player.EndReached += (_, __) =>
            {
                if (IsClosingOrClosed ||
                    IsInStartupNoiseWindow())
                {
                    return;
                }

                // 기존 코드의 중복 return 때문에 도달하지 않던 재연결 경로를 복구합니다.
                ScheduleRetry("end_reached");
            };
        }

        private void AddPersonalQualityOptions(
            Media media)
        {
            media.AddOption(":rtsp-tcp");
            media.AddOption(
                $":network-caching={PersonalNetworkCachingMs}");
            media.AddOption(
                $":live-caching={PersonalLiveCachingMs}");
            media.AddOption(":clock-jitter=0");
            media.AddOption(":no-audio");
            media.AddOption(":avcodec-threads=2");

            if (_forceSoftwareDecode)
            {
                media.AddOption(":avcodec-hw=none");
            }
        }

        private void ScheduleRunningDebounce(
            string url,
            string reason)
        {
            if (IsClosingOrClosed)
                return;

            CancelRunningDebounce();

            _pendingRunningUrl = url;

            var cts =
                CancellationTokenSource.CreateLinkedTokenSource(
                    _lifetimeCts.Token);

            Interlocked.Exchange(
                ref _runningDebounceCts,
                cts);

            _ = RunRunningDebounceAsync(
                cts,
                url,
                reason);

            Debug.WriteLine(
                $"[DeviceWindow] RUNNING 디바운스 시작. " +
                $"url={url}, delay={RunningDebounceMs}ms");
        }

        private async Task RunRunningDebounceAsync(
            CancellationTokenSource cts,
            string url,
            string reason)
        {
            try
            {
                await Task.Delay(
                    RunningDebounceMs,
                    cts.Token);

                if (IsClosingOrClosed ||
                    cts.IsCancellationRequested)
                {
                    return;
                }

                // 현재 예약이 아니면 오래된 작업입니다.
                if (!ReferenceEquals(
                        Interlocked.CompareExchange(
                            ref _runningDebounceCts,
                            null,
                            cts),
                        cts))
                {
                    return;
                }

                string state =
                    CurrentDevice.StreamState
                        ?.Trim()
                        .ToUpperInvariant();

                if (state != "RUNNING" ||
                    !string.Equals(
                        CurrentDevice.RtspUrl,
                        url,
                        StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(
                        _pendingRunningUrl,
                        url,
                        StringComparison.OrdinalIgnoreCase))
                {
                    Debug.WriteLine(
                        $"[DeviceWindow] RunningDebounce cancelled. " +
                        $"state={state} url={CurrentDevice.RtspUrl}");
                    return;
                }

                await RunSerializedAsync(() =>
                    StartRtspMirrorAsync(
                        forceRestart: false,
                        reason: $"debounced_RUNNING:{reason}"));
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[DeviceWindow] debounce error ignored: {ex}");
            }
            finally
            {
                Interlocked.CompareExchange(
                    ref _runningDebounceCts,
                    null,
                    cts);

                try
                {
                    cts.Dispose();
                }
                catch
                {
                }
            }
        }

        private void CancelRunningDebounce()
        {
            var cts = Interlocked.Exchange(
                ref _runningDebounceCts,
                null);

            _pendingRunningUrl = null;

            try
            {
                cts?.Cancel();
            }
            catch
            {
            }
        }

        private bool IsInStartupNoiseWindow()
        {
            if (_connectStartedUtc == DateTime.MinValue)
                return false;

            return (
                DateTime.UtcNow -
                _connectStartedUtc
                ).TotalSeconds <
                StartupNoiseWindowSeconds;
        }

        private void ScheduleRetry(
            string reason)
        {
            if (IsClosingOrClosed)
                return;

            CancelRetry();

            int errorCount =
                Interlocked.Increment(ref _errorCount);

            if (errorCount >= 2)
            {
                _forceSoftwareDecode = true;
            }

            if (errorCount > 3)
            {
                Debug.WriteLine(
                    $"[DeviceWindow] Retry stopped. " +
                    $"max error count reached. reason={reason}");

                SetWindowStatus(
                    "재연결 실패 - 수동 새로고침 필요");
                return;
            }

            int delaySec = errorCount switch
            {
                1 => 4,
                2 => 8,
                _ => 15
            };

            Debug.WriteLine(
                $"[DeviceWindow] Retry scheduled. " +
                $"reason={reason}, errorCount={errorCount}, " +
                $"delay={delaySec}s, swDecode={_forceSoftwareDecode}");

            var cts =
                CancellationTokenSource.CreateLinkedTokenSource(
                    _lifetimeCts.Token);

            Interlocked.Exchange(ref _retryCts, cts);

            _ = RunRetryAsync(
                cts,
                reason,
                delaySec);
        }

        private async Task RunRetryAsync(
            CancellationTokenSource cts,
            string reason,
            int delaySec)
        {
            try
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(delaySec),
                    cts.Token);

                if (IsClosingOrClosed ||
                    cts.IsCancellationRequested)
                {
                    return;
                }

                if (!ReferenceEquals(
                        Interlocked.CompareExchange(
                            ref _retryCts,
                            null,
                            cts),
                        cts))
                {
                    return;
                }

                await RunSerializedAsync(() =>
                    SyncFromAgentAndApplyAsync(
                        $"retry:{reason}"));
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[DeviceWindow] retry error ignored: {ex}");
            }
            finally
            {
                Interlocked.CompareExchange(
                    ref _retryCts,
                    null,
                    cts);

                try
                {
                    cts.Dispose();
                }
                catch
                {
                }
            }
        }

        private void CancelRetry()
        {
            var cts = Interlocked.Exchange(
                ref _retryCts,
                null);

            try
            {
                cts?.Cancel();
            }
            catch
            {
            }
        }

        private Task StopMirrorAsync()
        {
            if (Dispatcher.HasShutdownStarted ||
                Dispatcher.HasShutdownFinished)
            {
                return Task.CompletedTask;
            }

            if (Dispatcher.CheckAccess())
            {
                StopMirrorOnUiThread();
                return Task.CompletedTask;
            }

            try
            {
                return Dispatcher
                    .InvokeAsync(
                        StopMirrorOnUiThread,
                        DispatcherPriority.Send)
                    .Task;
            }
            catch (InvalidOperationException)
            {
                return Task.CompletedTask;
            }
            catch (TaskCanceledException)
            {
                return Task.CompletedTask;
            }
        }

        private void StopMirrorOnUiThread()
        {
            if (!Dispatcher.CheckAccess())
            {
                SafeDispatcherBeginInvoke(
                    StopMirrorOnUiThread);
                return;
            }

            CancelRetry();

            MediaPlayer player = MediaPlayer;
            Media media = _media;

            // 먼저 UI 및 필드에서 참조를 끊어 이후 비동기 작업이
            // Dispose 중인 객체를 다시 사용하지 못하게 합니다.
            MediaPlayer = null;
            _media = null;
            _lastPlayedUrl = null;
            _connectStartedUtc = DateTime.MinValue;

            try
            {
                if (RtspVideoView != null)
                {
                    RtspVideoView.MediaPlayer = null;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[DeviceWindow] VideoView detach failed: {ex.Message}");
            }

            try
            {
                player?.Stop();
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[DeviceWindow] MediaPlayer.Stop failed: {ex.Message}");
            }

            try
            {
                media?.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[DeviceWindow] Media.Dispose failed: {ex.Message}");
            }

            try
            {
                player?.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[DeviceWindow] MediaPlayer.Dispose failed: {ex.Message}");
            }
        }

        private void SetWindowStatus(
            string text)
        {
            if (IsClosingOrClosed)
                return;

            if (!Dispatcher.CheckAccess())
            {
                SafeDispatcherBeginInvoke(
                    () => SetWindowStatus(text));
                return;
            }

            try
            {
                Title =
                    $"{CurrentDevice?.Name ?? "Device"} - {text}";

                Debug.WriteLine(
                    $"[DeviceWindow] Status: {text}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[DeviceWindow] status UI ignored: {ex.Message}");
            }
        }

        private void SafeDispatcherBeginInvoke(
            Action action)
        {
            if (action == null ||
                IsClosingOrClosed ||
                Dispatcher.HasShutdownStarted ||
                Dispatcher.HasShutdownFinished)
            {
                return;
            }

            try
            {
                Dispatcher.BeginInvoke(
                    action,
                    DispatcherPriority.Background);
            }
            catch (InvalidOperationException)
            {
            }
            catch (TaskCanceledException)
            {
            }
        }

        private async void RefreshTile_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (_isRefreshing ||
                IsClosingOrClosed)
            {
                return;
            }

            _isRefreshing = true;

            try
            {
                Interlocked.Exchange(ref _errorCount, 0);

                await RunSerializedAsync(async () =>
                {
                    await SyncFromAgentAndApplyAsync(
                        "manual_refresh_before_restart");

                    if (IsClosingOrClosed)
                        return;

                    if (string.Equals(
                            CurrentDevice.StreamState,
                            "RUNNING",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        await StartRtspMirrorAsync(
                            forceRestart: true,
                            reason: "manual_refresh");
                    }
                    else
                    {
                        Debug.WriteLine(
                            $"[DeviceWindow] Manual refresh skipped playback. " +
                            $"state={CurrentDevice.StreamState}");
                    }
                });
            }
            catch (OperationCanceledException)
                when (IsClosingOrClosed)
            {
            }
            catch (Exception ex)
            {
                if (!IsClosingOrClosed)
                {
                    SafeShowError(
                        $"새로고침 실패:\n{ex.Message}");
                }
            }
            finally
            {
                _isRefreshing = false;
            }
        }

        private async void StopAppBtn_Click(
            object sender,
            RoutedEventArgs e)
        {
            PlayClickSound();

            if (!TryBeginCommand())
                return;

            SetCommandUiEnabled(false);

            try
            {
                Debug.WriteLine(
                    $"[DeviceWindow] 앱 중지 요청. device={CurrentDevice?.Name}");

                AgentCommandReply reply =
                    await _mainWindow.StopDeviceAppAsync(
                        CurrentDevice,
                        _lifetimeCts.Token,
                        showErrorUi: false);

                if (IsClosingOrClosed)
                    return;

                bool deliveryUncertain =
                    reply?.TimedOut == true ||
                    string.Equals(
                        reply?.Error,
                        "timeout",
                        StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(
                        reply?.Error,
                        "network_error",
                        StringComparison.OrdinalIgnoreCase);

                if (reply?.IsAcceptedSuccess == true)
                {
                    SetWindowStatus("앱 종료 시퀀스 전송 완료");
                }
                else if (deliveryUncertain)
                {
                    SetWindowStatus("앱 종료 응답 확인 불가");
                }
                else
                {
                    SetWindowStatus("앱 종료 요청 실패");

                    SafeShowError(
                        $"앱 종료 요청이 거부되었습니다.\n" +
                        $"상태: {reply?.Error ?? "unknown_error"}\n" +
                        $"내용: {reply?.Message ?? "응답 없음"}");
                }
            }
            catch (OperationCanceledException)
                when (IsClosingOrClosed ||
                      _lifetimeCts.IsCancellationRequested)
            {
                // 창 닫기 중 정상 취소: 예외 UI를 표시하지 않습니다.
                Debug.WriteLine(
                    "[DeviceWindow] stop command cancelled by window close.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[DeviceWindow] stop command error: {ex}");

                if (!IsClosingOrClosed)
                {
                    SafeShowError(
                        $"앱 종료 중 오류가 발생했습니다.\n" +
                        $"{ex.GetType().Name}: {ex.Message}");
                }
            }
            finally
            {
                EndCommand();
            }
        }

        private async void StartAppBtn_Click(
            object sender,
            RoutedEventArgs e)
        {
            PlayClickSound();

            if (sender is not Button button ||
                !int.TryParse(
                    button.Tag as string,
                    out int index))
            {
                return;
            }

            if (!TryBeginCommand())
                return;

            SetCommandUiEnabled(false);

            try
            {
                bool accepted =
                    await _mainWindow.StartAppAsync(
                        CurrentDevice,
                        index,
                        _lifetimeCts.Token,
                        showErrorUi: false);

                if (IsClosingOrClosed)
                    return;

                SetWindowStatus(
                    accepted
                        ? "앱 실행 명령 접수 완료"
                        : "앱 실행 명령 확인 필요");
            }
            catch (OperationCanceledException)
                when (IsClosingOrClosed ||
                      _lifetimeCts.IsCancellationRequested)
            {
                // 창 닫기 중 정상 취소: 예외 UI를 표시하지 않습니다.
                Debug.WriteLine(
                    "[DeviceWindow] start command cancelled by window close.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[DeviceWindow] start command error: {ex}");

                if (!IsClosingOrClosed)
                {
                    SafeShowError(
                        $"앱 실행 중 오류가 발생했습니다.\n" +
                        $"{ex.GetType().Name}: {ex.Message}");
                }
            }
            finally
            {
                EndCommand();
            }
        }

        private bool TryBeginCommand()
        {
            if (IsClosingOrClosed)
                return false;

            return Interlocked.CompareExchange(
                       ref _commandInProgress,
                       1,
                       0) == 0;
        }

        private void EndCommand()
        {
            Interlocked.Exchange(
                ref _commandInProgress,
                0);

            if (IsClosingOrClosed)
                return;

            SetCommandUiEnabled(true);
        }

        private void SetCommandUiEnabled(
            bool enabled)
        {
            if (IsClosingOrClosed ||
                Dispatcher.HasShutdownStarted ||
                Dispatcher.HasShutdownFinished)
            {
                return;
            }

            if (!Dispatcher.CheckAccess())
            {
                SafeDispatcherBeginInvoke(
                    () => SetCommandUiEnabled(enabled));
                return;
            }

            try
            {
                IsEnabled = enabled;
            }
            catch (InvalidOperationException)
            {
            }
        }

        private void SafeShowError(
            string message)
        {
            if (IsClosingOrClosed)
                return;

            if (!Dispatcher.CheckAccess())
            {
                SafeDispatcherBeginInvoke(
                    () => SafeShowError(message));
                return;
            }

            try
            {
                if (!IsClosingOrClosed &&
                    IsLoaded &&
                    IsVisible)
                {
                    MessageBox.Show(
                        this,
                        message,
                        "오류",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[DeviceWindow] error UI ignored: {ex.Message}");
            }
        }

        private static void PlayClickSound()
        {
            try
            {
                System.Media.SystemSounds.Beep.Play();
            }
            catch
            {
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged(
            string name)
        {
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(name));
        }
    }
}