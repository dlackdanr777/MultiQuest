using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

using VlcLib = LibVLCSharp.Shared.LibVLC;
using VlcMedia = LibVLCSharp.Shared.Media;
using VlcMediaPlayer = LibVLCSharp.Shared.MediaPlayer;
using VlcFromType = LibVLCSharp.Shared.FromType;

using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;

using ThreadingTimer = System.Threading.Timer;

namespace MultiQuest_Management
{
    public sealed class RtspTileViewModel : INotifyPropertyChanged, IDisposable
    {
        private readonly VlcLib _libVlc;
        private readonly RtspQualityManager _qualityManager;
        private readonly bool _disableHardwareDecoding;
        private readonly RtspOperationProfile _profile;

        private VlcMedia? _media;

        private string _status = "대기 중";
        private WpfBrush _statusBrush = WpfBrushes.LightGray;

        private bool _disposed;
        private RtspQualityManager.QualityLevel _currentQuality;

        private int _bufferingCount;
        private string _lastPlayedUrl = null;
        private string _qualityLevel = "-";
        private double _bufferingRate = 0.0;
        private int _networkCaching = 0;
        private bool _isPlaying = false;

        private ThreadingTimer? _healthTimer;

        private DateTime _startedAtUtc;
        private DateTime _lastVideoAliveUtc;
        private DateTime _lastTransportAliveUtc;

        private volatile bool _isFrozen = false;
        private volatile int _voutCount;

        private DateTime _errorAtUtc;
        private volatile int _errorCount;
        private int _timeChangedCount;

        // Stability 모드는 30초, 그 외 20초
        private int FirstVideoSignalTimeoutSeconds =>
            _profile == RtspOperationProfile.Stability ? 30 : 20;

        // Stability 모드는 8초, 그 외 5초
        private int ReconnectAfterErrorMinSeconds =>
            _profile == RtspOperationProfile.Stability ? 8 : 5;

        public bool IsFrozen => _isFrozen;

        public QuestAgentInfo Agent { get; }

        public VlcMediaPlayer MediaPlayer { get; }

        public string Title =>
            string.IsNullOrWhiteSpace(Agent.Model)
                ? Agent.Host
                : $"{Agent.Model}";

        public string Subtitle =>
            $"{Agent.Host}:{Agent.StatusPort} / Battery {Agent.Battery}%";

        public bool IsPlaying
        {
            get => _isPlaying;
            private set
            {
                if (_isPlaying == value) return;
                _isPlaying = value;
                OnPropertyChanged();
            }
        }

        public string Status
        {
            get => _status;
            private set
            {
                if (_status == value) return;
                _status = value;
                OnPropertyChanged();
            }
        }

        public WpfBrush StatusBrush
        {
            get => _statusBrush;
            private set
            {
                if (_statusBrush == value) return;
                _statusBrush = value;
                OnPropertyChanged();
            }
        }

        public string QualityLevel
        {
            get => _qualityLevel;
            private set
            {
                if (_qualityLevel == value) return;
                _qualityLevel = value;
                OnPropertyChanged();
            }
        }

        public double BufferingRate
        {
            get => _bufferingRate;
            private set
            {
                if (Math.Abs(_bufferingRate - value) < 0.01) return;
                _bufferingRate = value;
                OnPropertyChanged();
            }
        }

        public int NetworkCaching
        {
            get => _networkCaching;
            private set
            {
                if (_networkCaching == value) return;
                _networkCaching = value;
                OnPropertyChanged();
            }
        }

        public RtspTileViewModel(
            VlcLib libVlc,
            QuestAgentInfo agent,
            RtspQualityManager qualityManager,
            bool disableHardwareDecoding = false,
            RtspOperationProfile profile = RtspOperationProfile.Balanced)
        {
            _libVlc = libVlc;
            Agent = agent;
            _qualityManager = qualityManager;
            _disableHardwareDecoding = disableHardwareDecoding;
            _profile = profile;

            MediaPlayer = new VlcMediaPlayer(_libVlc)
            {
                EnableHardwareDecoding = !disableHardwareDecoding,
                Mute = true
            };

            MediaPlayer.Playing += (_, __) =>
            {
                _isFrozen = false;
                _errorAtUtc = default;

                TouchTransportAlive();
                TouchVideoAlive();

                StartHealthDetector();

                SetStatus("재생 중", WpfBrushes.LightGreen);
                IsPlaying = true;

                System.Diagnostics.Debug.WriteLine(
                    $"[VLC] {Agent.Host} Playing: {Agent.RtspUrl}");
            };

            MediaPlayer.TimeChanged += (_, __) =>
            {
                TouchVideoAlive();

                int cnt = System.Threading.Interlocked.Increment(ref _timeChangedCount);

                if (cnt <= 5)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[VLC] {Agent.Host} TimeChanged #{cnt}");
                }
            };

            MediaPlayer.Vout += (_, e) =>
            {
                _voutCount = e.Count;

                if (e.Count > 0)
                {
                    TouchVideoAlive();
                    _isFrozen = false;

                    if (!IsPlaying)
                        IsPlaying = true;

                    SetStatus("재생 중", WpfBrushes.LightGreen);
                }

                System.Diagnostics.Debug.WriteLine(
                    $"[VLC] {Agent.Host} Vout count={e.Count}");
            };

            MediaPlayer.Buffering += (_, e) =>
            {
                _bufferingCount++;
                TouchTransportAlive();

                _qualityManager.RecordBuffering(Agent.Host);

                if (!IsPlaying)
                    SetStatus("버퍼링", WpfBrushes.Khaki);

                if (_bufferingCount <= 5)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[VLC] {Agent.Host} Buffering #{_bufferingCount}: {e.Cache:F0}%");
                }
            };

            MediaPlayer.EncounteredError += (_, __) =>
            {
                _errorCount++;
                _errorAtUtc = DateTime.UtcNow;

                SetStatus("재생 오류", WpfBrushes.OrangeRed);
                IsPlaying = false;

                System.Diagnostics.Debug.WriteLine(
                    $"[VLC] {Agent.Host} EncounteredError #{_errorCount} " +
                    $"(timeChanges={_timeChangedCount}, vout={_voutCount})");
            };

            MediaPlayer.Stopped += (_, __) =>
            {
                StopHealthDetector();

                if (!_isFrozen)
                    SetStatus("중지됨", WpfBrushes.LightGray);

                IsPlaying = false;

                System.Diagnostics.Debug.WriteLine(
                    $"[VLC] {Agent.Host} Stopped " +
                    $"(frozen={_isFrozen}, timeChanges={_timeChangedCount}, vout={_voutCount})");
            };

            MediaPlayer.EndReached += (_, __) =>
            {
                StopHealthDetector();

                SetStatus("스트림 종료됨", WpfBrushes.Orange);
                IsPlaying = false;

                System.Diagnostics.Debug.WriteLine(
                    $"[VLC] {Agent.Host} EndReached " +
                    $"(timeChanges={_timeChangedCount}, vout={_voutCount})");
            };
        }

        public bool IsProbablyStalled()
        {
            if (_disposed || _isFrozen) return false;
            if (_startedAtUtc == default) return false;

            var now = DateTime.UtcNow;
            var elapsed = (now - _startedAtUtc).TotalSeconds;

            if (_errorAtUtc != default)
            {
                var waitSec = Math.Max(ReconnectAfterErrorMinSeconds, _errorCount switch
                {
                    1 => 5,
                    2 => 8,
                    _ => 12
                });

                return (now - _errorAtUtc).TotalSeconds >= waitSec;
            }

            if (elapsed < FirstVideoSignalTimeoutSeconds)
                return false;

            if (MediaPlayer.IsPlaying && _voutCount > 0)
                return false;

            if (MediaPlayer.IsPlaying && _lastTransportAliveUtc != default)
            {
                var transportAge = (now - _lastTransportAliveUtc).TotalSeconds;
                if (transportAge < 30)
                    return false;
            }

            if (_voutCount <= 0 && _lastVideoAliveUtc == default)
                return true;

            if (!MediaPlayer.IsPlaying && _voutCount <= 0)
                return true;

            return false;
        }

        public void Start(int activeStreamCount)
        {
            if (_disposed) return;

            if (string.IsNullOrWhiteSpace(Agent.RtspUrl))
            {
                SetStatus("RTSP URL 없음", WpfBrushes.OrangeRed);
                return;
            }

            if (_lastPlayedUrl == Agent.RtspUrl &&
                MediaPlayer.IsPlaying &&
                !_isFrozen)
            {
                return;
            }

            try
            {
                Stop();

                _isFrozen = false;
                _startedAtUtc = DateTime.UtcNow;
                _lastVideoAliveUtc = default;
                _lastTransportAliveUtc = default;
                _errorAtUtc = default;
                _errorCount = 0;
                _voutCount = 0;
                _timeChangedCount = 0;
                _bufferingCount = 0;

                _currentQuality = _qualityManager.RegisterStream(
                    Agent.Host,
                    activeStreamCount);

                int effectiveCachingMs = GetNetworkCachingValueByProfile(_currentQuality);
                string qualityDesc =
                    $"{GetQualityDisplayName(_currentQuality)} ({effectiveCachingMs}ms 버퍼)";

                SetStatus($"연결 중 ({qualityDesc})", WpfBrushes.Khaki);

                _media?.Dispose();
                _media = new VlcMedia(_libVlc, Agent.RtspUrl, VlcFromType.FromLocation);

                var options = RtspQualityManager.GetVlcOptions(_currentQuality);
                foreach (var option in options)
                {
                    if (IsCacheRelatedOption(option))
                        continue;

                    _media.AddOption(option);
                }

                AddStabilizedVlcOptions(_media, effectiveCachingMs);

                UpdateQualityInfo();

                System.Diagnostics.Debug.WriteLine(
                    $"[VLC] {Agent.Host} Start: url={Agent.RtspUrl} " +
                    $"quality={qualityDesc} hwDecoding={!_disableHardwareDecoding}");

                MediaPlayer.Play(_media);
                _lastPlayedUrl = Agent.RtspUrl;

                StartHealthDetector();
            }
            catch (Exception ex)
            {
                SetStatus($"재생 시작 실패: {ex.Message}", WpfBrushes.OrangeRed);

                System.Diagnostics.Debug.WriteLine(
                    $"[VLC] {Agent.Host} Start failed: {ex}");
            }
        }

        public void Restart(int activeStreamCount)
        {
            if (_disposed) return;

            if (_lastPlayedUrl != Agent.RtspUrl)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[RtspTileViewModel] URL 변경 감지: {_lastPlayedUrl} -> {Agent.RtspUrl}");

                Start(activeStreamCount);
            }
        }

        public void Stop()
        {
            try
            {
                StopHealthDetector();

                _qualityManager.UnregisterStream(Agent.Host);

                try { MediaPlayer.Stop(); } catch { }

                _lastPlayedUrl = null;
                IsPlaying = false;
            }
            catch
            {
                // ignored
            }
        }

        private void StartHealthDetector()
        {
            _healthTimer?.Dispose();

            _healthTimer = new ThreadingTimer(
                CheckHealth,
                null,
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(5));
        }

        private void StopHealthDetector()
        {
            _healthTimer?.Dispose();
            _healthTimer = null;
        }

        private void CheckHealth(object? _)
        {
            if (_disposed || _isFrozen)
                return;

            if (_startedAtUtc == default)
                return;

            var now = DateTime.UtcNow;
            var elapsed = (now - _startedAtUtc).TotalSeconds;

            if (_errorAtUtc != default)
            {
                var waitSec = Math.Max(ReconnectAfterErrorMinSeconds, _errorCount switch
                {
                    1 => 5,
                    2 => 8,
                    _ => 12
                });

                if ((now - _errorAtUtc).TotalSeconds >= waitSec)
                {
                    MarkFrozenForReconnect(
                        $"재생 오류 후 {waitSec}초 경과");
                }

                return;
            }

            if (MediaPlayer.IsPlaying && _voutCount > 0)
            {
                if (!IsPlaying)
                    IsPlaying = true;

                return;
            }

            if (elapsed >= FirstVideoSignalTimeoutSeconds &&
                _voutCount <= 0 &&
                _lastVideoAliveUtc == default)
            {
                MarkFrozenForReconnect(
                    $"시작 후 {elapsed:F1}초 동안 영상 출력 없음");
            }
        }

        private void MarkFrozenForReconnect(string reason)
        {
            if (_isFrozen || _disposed)
                return;

            _isFrozen = true;
            StopHealthDetector();

            System.Diagnostics.Debug.WriteLine(
                $"[RtspTile] {Agent.Host} WPF 타일 재연결 필요: {reason} " +
                $"(timeChanges={_timeChangedCount}, vout={_voutCount})");

            SetStatus("RTSP 재연결 필요", WpfBrushes.OrangeRed);
            IsPlaying = false;

            FrozenDetected?.Invoke(this, EventArgs.Empty);
        }

        private void TouchTransportAlive()
        {
            _lastTransportAliveUtc = DateTime.UtcNow;
        }

        private void TouchVideoAlive()
        {
            bool wasAlive = _lastVideoAliveUtc != default;

            _lastVideoAliveUtc = DateTime.UtcNow;
            _lastTransportAliveUtc = _lastVideoAliveUtc;

            if (!wasAlive)
                VideoAliveRestored?.Invoke(this, EventArgs.Empty);
        }

        private void SetStatus(string status, WpfBrush brush)
        {
            var app = System.Windows.Application.Current;
            if (app is null) return;

            app.Dispatcher.InvokeAsync(() =>
            {
                Status = status;
                StatusBrush = brush;
            });
        }

        public void UpdateQualityInfo()
        {
            var app = System.Windows.Application.Current;
            if (app is null) return;

            app.Dispatcher.InvokeAsync(() =>
            {
                var streamInfo = _qualityManager.GetStreamInfo(Agent.Host);

                if (streamInfo != null)
                {
                    QualityLevel = GetQualityDisplayName(streamInfo.CurrentQuality);
                    BufferingRate = streamInfo.BufferingRate * 100;
                    NetworkCaching = GetNetworkCachingValueByProfile(streamInfo.CurrentQuality);
                }
                else
                {
                    QualityLevel = "-";
                    BufferingRate = 0.0;
                    NetworkCaching = 0;
                }
            });
        }

        private static bool IsCacheRelatedOption(string option)
        {
            if (string.IsNullOrWhiteSpace(option))
                return false;

            string s = option.Trim().TrimStart(':').ToLowerInvariant();

            return s.StartsWith("network-caching") ||
                   s.StartsWith("live-caching") ||
                   s.StartsWith("clock-jitter") ||
                   s.StartsWith("drop-late-frames") ||
                   s.StartsWith("skip-frames") ||
                   s.StartsWith("rtsp-tcp");
        }

        private void AddStabilizedVlcOptions(VlcMedia media, int cachingMs)
        {
            media.AddOption(":rtsp-tcp");
            media.AddOption($":network-caching={cachingMs}");
            media.AddOption($":live-caching={cachingMs}");
            media.AddOption(":clock-jitter=0");
            media.AddOption(":drop-late-frames");
            media.AddOption(":skip-frames");
            media.AddOption(":no-audio");

            if (_disableHardwareDecoding)
            {
                media.AddOption(":avcodec-hw=none");
            }
        }

        private static string GetQualityDisplayName(RtspQualityManager.QualityLevel quality)
        {
            return quality switch
            {
                RtspQualityManager.QualityLevel.Ultra => "Ultra",
                RtspQualityManager.QualityLevel.High => "High",
                RtspQualityManager.QualityLevel.Medium => "Medium",
                RtspQualityManager.QualityLevel.Low => "Low",
                RtspQualityManager.QualityLevel.Minimal => "Minimal",
                _ => "-"
            };
        }

        private int GetNetworkCachingValueByProfile(RtspQualityManager.QualityLevel quality)
        {
            return _profile switch
            {
                RtspOperationProfile.Stability => quality switch
                {
                    RtspQualityManager.QualityLevel.Ultra   => 1500,
                    RtspQualityManager.QualityLevel.High    => 2000,
                    RtspQualityManager.QualityLevel.Medium  => 2500,
                    RtspQualityManager.QualityLevel.Low     => 3000,
                    RtspQualityManager.QualityLevel.Minimal => 4000,
                    _ => 2500
                },
                RtspOperationProfile.Quality => quality switch
                {
                    RtspQualityManager.QualityLevel.Ultra   => 700,
                    RtspQualityManager.QualityLevel.High    => 900,
                    RtspQualityManager.QualityLevel.Medium  => 1200,
                    RtspQualityManager.QualityLevel.Low     => 1500,
                    RtspQualityManager.QualityLevel.Minimal => 2000,
                    _ => 1000
                },
                _ => quality switch  // Balanced
                {
                    RtspQualityManager.QualityLevel.Ultra   => 1000,
                    RtspQualityManager.QualityLevel.High    => 1000,
                    RtspQualityManager.QualityLevel.Medium  => 1200,
                    RtspQualityManager.QualityLevel.Low     => 1500,
                    RtspQualityManager.QualityLevel.Minimal => 2000,
                    _ => 1000
                }
            };
        }

        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;

            IsPlaying = false;

            StopHealthDetector();

            try { _qualityManager.UnregisterStream(Agent.Host); } catch { }
            try { MediaPlayer.Stop(); } catch { }
            try { _media?.Dispose(); } catch { }
            try { MediaPlayer.Dispose(); } catch { }
        }

        public event EventHandler? VideoAliveRestored;

        public event EventHandler? FrozenDetected;

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? prop = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }
}