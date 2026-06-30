using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MultiQuest_Management
{
    /// <summary>
    /// 다중 RTSP 스트림의 성능을 모니터링하고 동적으로 품질을 조절합니다.
    /// </summary>
    public sealed class RtspQualityManager : IDisposable
    {
        private readonly ConcurrentDictionary<string, StreamMetrics> _metrics = new();
        private readonly System.Threading.Timer _monitorTimer;
        private bool _disposed;

        // 품질 레벨 정의
        public enum QualityLevel
        {
            Ultra,   // 최고 품질: 버퍼 250ms, 프레임 드롭 없음
            High,    // 고품질: 버퍼 500ms, 선택적 프레임 드롭
            Medium,  // 중품질: 버퍼 1000ms, 프레임 드롭 활성화
            Low,     // 저품질: 버퍼 2000ms, 적극적 프레임 드롭
            Minimal  // 최소 품질: 버퍼 3000ms, 최대 프레임 드롭
        }

        // 시스템 성능 임계값
        private const int MaxConcurrentStreams_Ultra = 4;    // Ultra: 4대
        private const int MaxConcurrentStreams_High = 8;     // High: 8대
        private const int MaxConcurrentStreams_Medium = 12;  // Medium: 12대
        private const int MaxConcurrentStreams_Low = 16;     // Low: 16대

        // 버퍼링 임계값
        private const double BufferingThreshold_Upgrade = 0.02;   // 2% 이하면 업그레이드
        private const double BufferingThreshold_Downgrade = 0.10; // 10% 이상이면 다운그레이드

        public RtspQualityManager()
        {
            // 5초마다 성능 모니터링
            _monitorTimer = new System.Threading.Timer(MonitorPerformance, null, 5000, 5000);
        }

        /// <summary>
        /// 스트림 시작 시 호출
        /// </summary>
        public QualityLevel RegisterStream(string streamId, int activeStreamCount)
        {
            var metrics = new StreamMetrics
            {
                StreamId = streamId,
                StartTime = DateTime.UtcNow,
                CurrentQuality = DetermineInitialQuality(activeStreamCount)
            };

            _metrics[streamId] = metrics;
            return metrics.CurrentQuality;
        }

        /// <summary>
        /// 버퍼링 이벤트 기록
        /// </summary>
        public void RecordBuffering(string streamId)
        {
            if (_metrics.TryGetValue(streamId, out var metrics))
            {
                Interlocked.Increment(ref metrics.BufferingCount);
                metrics.LastBufferingTime = DateTime.UtcNow;
            }
        }

        /// <summary>
        /// 프레임 드롭 이벤트 기록
        /// </summary>
        public void RecordFrameDrop(string streamId, int droppedFrames)
        {
            if (_metrics.TryGetValue(streamId, out var metrics))
            {
                Interlocked.Add(ref metrics.DroppedFrames, droppedFrames);
            }
        }

        /// <summary>
        /// 스트림 종료 시 호출
        /// </summary>
        public void UnregisterStream(string streamId)
        {
            _metrics.TryRemove(streamId, out _);
        }

        /// <summary>
        /// 현재 품질 레벨 가져오기
        /// </summary>
        public QualityLevel GetCurrentQuality(string streamId)
        {
            return _metrics.TryGetValue(streamId, out var metrics) 
                ? metrics.CurrentQuality 
                : QualityLevel.Medium;
        }

        /// <summary>
        /// 스트림 정보 가져오기 (UI 바인딩용)
        /// </summary>
        public StreamInfo GetStreamInfo(string streamId)
        {
            if (!_metrics.TryGetValue(streamId, out var metrics))
                return null;

            var elapsed = (DateTime.UtcNow - metrics.StartTime).TotalSeconds;
            var bufferingRate = elapsed > 0 ? metrics.BufferingCount / elapsed : 0;

            return new StreamInfo
            {
                StreamId = streamId,
                CurrentQuality = metrics.CurrentQuality,
                BufferingRate = bufferingRate,
                BufferingCount = metrics.BufferingCount,
                DroppedFrames = metrics.DroppedFrames,
                Uptime = elapsed
            };
        }

        /// <summary>
        /// 스트림 정보 (읽기 전용)
        /// </summary>
        public class StreamInfo
        {
            public string StreamId { get; set; }
            public QualityLevel CurrentQuality { get; set; }
            public double BufferingRate { get; set; }
            public int BufferingCount { get; set; }
            public int DroppedFrames { get; set; }
            public double Uptime { get; set; }
        }

        /// <summary>
        /// 품질 레벨에 따른 VLC 옵션 반환
        /// </summary>
        public static string[] GetVlcOptions(QualityLevel quality)
        {
            return quality switch
            {
                QualityLevel.Ultra => new[]
                {
                    ":rtsp-tcp",
                    ":network-caching=1000",
                    ":live-caching=1000",
                    ":clock-jitter=0",
                    ":drop-late-frames",
                    ":skip-frames"
                },

                QualityLevel.High => new[]
                {
                    ":rtsp-tcp",
                    ":network-caching=1000",
                    ":live-caching=1000",
                    ":clock-jitter=0",
                    ":drop-late-frames",
                    ":skip-frames"
                },

                QualityLevel.Medium => new[]
                {
                    ":rtsp-tcp",
                    ":network-caching=1200",
                    ":live-caching=1200",
                    ":clock-jitter=0",
                    ":drop-late-frames",
                    ":skip-frames"
                },

                QualityLevel.Low => new[]
                {
                    ":rtsp-tcp",
                    ":network-caching=1500",
                    ":live-caching=1500",
                    ":clock-jitter=0",
                    ":drop-late-frames",
                    ":skip-frames",
                    ":avcodec-fast"
                },

                QualityLevel.Minimal => new[]
                {
                    ":rtsp-tcp",
                    ":network-caching=2000",
                    ":live-caching=2000",
                    ":clock-jitter=0",
                    ":drop-late-frames",
                    ":skip-frames",
                    ":avcodec-fast"
                },

                _ => GetVlcOptions(QualityLevel.Medium)
            };
        }

        /// <summary>
        /// 품질 레벨 설명
        /// </summary>
        public static string GetQualityDescription(QualityLevel quality)
        {
            return quality switch
            {
                QualityLevel.Ultra => "최고 품질 (250ms 버퍼)",
                QualityLevel.High => "고품질 (500ms 버퍼)",
                QualityLevel.Medium => "중품질 (1000ms 버퍼)",
                QualityLevel.Low => "저품질 (2000ms 버퍼)",
                QualityLevel.Minimal => "최소 품질 (3000ms 버퍼)",
                _ => "중품질"
            };
        }

        /// <summary>
        /// 초기 품질 결정 (동시 스트림 수 기반)
        /// </summary>
        private QualityLevel DetermineInitialQuality(int activeStreamCount)
        {
            if (activeStreamCount <= MaxConcurrentStreams_Ultra)
                return QualityLevel.Ultra;

            if (activeStreamCount <= MaxConcurrentStreams_High)
                return QualityLevel.High;

            if (activeStreamCount <= MaxConcurrentStreams_Medium)
                return QualityLevel.Medium;

            if (activeStreamCount <= MaxConcurrentStreams_Low)
                return QualityLevel.Low;

            return QualityLevel.Minimal;
        }

        /// <summary>
        /// 주기적으로 성능 모니터링 및 품질 조정
        /// </summary>
        private void MonitorPerformance(object state)
        {
            if (_disposed) return;

            try
            {
                int activeCount = _metrics.Count;

                foreach (var kvp in _metrics.ToArray())
                {
                    var metrics = kvp.Value;
                    var elapsed = (DateTime.UtcNow - metrics.StartTime).TotalSeconds;

                    if (elapsed < 10) continue; // 최소 10초 동안 데이터 수집

                    // 버퍼링 비율 계산
                    double bufferingRate = metrics.BufferingCount / elapsed;

                    // 품질 조정 결정
                    var newQuality = DecideQualityAdjustment(
                        metrics.CurrentQuality,
                        bufferingRate,
                        activeCount);

                    if (newQuality != metrics.CurrentQuality)
                    {
                        metrics.CurrentQuality = newQuality;
                        metrics.QualityChangedTime = DateTime.UtcNow;

                        Debug.WriteLine(
                            $"[RTSP Quality] {metrics.StreamId}: " +
                            $"{metrics.CurrentQuality} → {newQuality} " +
                            $"(버퍼링률: {bufferingRate:F3}/s, 활성: {activeCount})");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RTSP Quality] 모니터링 오류: {ex.Message}");
            }
        }

        /// <summary>
        /// 품질 조정 결정
        /// </summary>
        private QualityLevel DecideQualityAdjustment(
            QualityLevel current,
            double bufferingRate,
            int activeStreamCount)
        {
            // 버퍼링이 많으면 품질 다운
            if (bufferingRate > BufferingThreshold_Downgrade)
            {
                return current switch
                {
                    QualityLevel.Ultra => QualityLevel.High,
                    QualityLevel.High => QualityLevel.Medium,
                    QualityLevel.Medium => QualityLevel.Low,
                    QualityLevel.Low => QualityLevel.Minimal,
                    _ => current
                };
            }

            // 버퍼링이 거의 없고 여유 있으면 품질 업
            if (bufferingRate < BufferingThreshold_Upgrade)
            {
                // 동시 스트림 수에 따른 최대 품질 제한
                var maxAllowedQuality = DetermineInitialQuality(activeStreamCount);

                var upgraded = current switch
                {
                    QualityLevel.Minimal => QualityLevel.Low,
                    QualityLevel.Low => QualityLevel.Medium,
                    QualityLevel.Medium => QualityLevel.High,
                    QualityLevel.High => QualityLevel.Ultra,
                    _ => current
                };

                // 허용된 최대 품질을 넘지 않도록
                return upgraded > maxAllowedQuality ? maxAllowedQuality : upgraded;
            }

            return current;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _monitorTimer?.Dispose();
            _metrics.Clear();
        }

        /// <summary>
        /// 스트림별 성능 메트릭
        /// </summary>
        private class StreamMetrics
        {
            public string StreamId { get; set; }
            public DateTime StartTime { get; set; }
            public QualityLevel CurrentQuality { get; set; }
            public int BufferingCount;
            public int DroppedFrames;
            public DateTime LastBufferingTime { get; set; }
            public DateTime QualityChangedTime { get; set; }
        }
    }
}
