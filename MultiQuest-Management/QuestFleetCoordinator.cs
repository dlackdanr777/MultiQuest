using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MultiQuest_Management
{
    public sealed class QuestEndpoint
    {
        public string Name { get; set; }
        public string Host { get; set; }
        public int Port { get; set; } = 18080;
    }

    public sealed class FleetDeviceResult
    {
        public QuestEndpoint Device { get; set; }
        public bool Success { get; set; }
        public string Stage { get; set; }
        public string Message { get; set; }
        public AgentOperationInfo Operation { get; set; }
    }

    /// <summary>
    /// 최대 16대 운용 시 Activity/MediaProjection 요청이 한 번에 몰리지 않도록
    /// 단계별 동시성을 제한하는 예시 코디네이터입니다.
    /// </summary>
    public sealed class QuestFleetCoordinator
    {
        private readonly SemaphoreSlim _wakeGate;
        private readonly SemaphoreSlim _launchGate;
        private readonly SemaphoreSlim _captureGate;

        public QuestFleetCoordinator(
            int maxConcurrentWake = 4,
            int maxConcurrentLaunch = 4,
            int maxConcurrentCapture = 3)
        {
            _wakeGate = new SemaphoreSlim(
                Math.Max(1, maxConcurrentWake),
                Math.Max(1, maxConcurrentWake));
            _launchGate = new SemaphoreSlim(
                Math.Max(1, maxConcurrentLaunch),
                Math.Max(1, maxConcurrentLaunch));
            _captureGate = new SemaphoreSlim(
                Math.Max(1, maxConcurrentCapture),
                Math.Max(1, maxConcurrentCapture));
        }

        public async Task<IReadOnlyList<FleetDeviceResult>>
            LaunchAndStartCaptureAsync(
                IEnumerable<QuestEndpoint> devices,
                string packageName,
                string activityName =
                    "com.unity3d.player.UnityPlayerActivity",
                object extras = null,
                bool forceRestart = false,
                CancellationToken cancellationToken = default)
        {
            QuestEndpoint[] targets = devices?
                .Where(d =>
                    d != null &&
                    !string.IsNullOrWhiteSpace(d.Host))
                .Take(16)
                .ToArray() ?? Array.Empty<QuestEndpoint>();

            Task<FleetDeviceResult>[] tasks = targets
                .Select((device, index) => RunDeviceFlowAsync(
                    device,
                    index,
                    packageName,
                    activityName,
                    extras,
                    forceRestart,
                    cancellationToken))
                .ToArray();

            return await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        public async Task<IReadOnlyList<FleetDeviceResult>>
            StopAllAsync(
                IEnumerable<QuestEndpoint> devices,
                bool goHome = true,
                CancellationToken cancellationToken = default)
        {
            QuestEndpoint[] targets = devices?
                .Where(d =>
                    d != null &&
                    !string.IsNullOrWhiteSpace(d.Host))
                .Take(16)
                .ToArray() ?? Array.Empty<QuestEndpoint>();

            using var gate = new SemaphoreSlim(8, 8);

            Task<FleetDeviceResult>[] tasks = targets.Select(async device =>
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    bool ok = await AgentApi.StopAllStoryWingFastAsync(
                        device.Host,
                        device.Port,
                        goHome,
                        cancellationToken: cancellationToken)
                        .ConfigureAwait(false);

                    return new FleetDeviceResult
                    {
                        Device = device,
                        Success = ok,
                        Stage = "stopAll",
                        Message = ok
                            ? "종료 명령 완료"
                            : "종료 명령 실패"
                    };
                }
                finally
                {
                    gate.Release();
                }
            }).ToArray();

            return await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        private async Task<FleetDeviceResult> RunDeviceFlowAsync(
            QuestEndpoint device,
            int index,
            string packageName,
            string activityName,
            object extras,
            bool forceRestart,
            CancellationToken cancellationToken)
        {
            try
            {
                await _wakeGate.WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
                try
                {
                    AgentOperationInfo wake =
                        await AgentApi.WakeScreenAndWaitAsync(
                            device.Host,
                            device.Port,
                            keepAwake: true,
                            completionTimeout: TimeSpan.FromSeconds(5),
                            cancellationToken: cancellationToken)
                            .ConfigureAwait(false);

                    // 이미 interactive인 경우 operationId 없이 즉시 완료될 수 있습니다.
                    if (wake == null)
                    {
                        bool accepted = await AgentApi.WakeScreenAsync(
                            device.Host,
                            device.Port,
                            keepAwake: true,
                            cancellationToken: cancellationToken)
                            .ConfigureAwait(false);

                        if (!accepted)
                        {
                            return Failure(
                                device,
                                "wake",
                                "화면 깨우기 요청 실패");
                        }
                    }
                    else if (!wake.Completed)
                    {
                        return Failure(
                            device,
                            "wake",
                            wake.Message ?? "화면 깨우기 미완료",
                            wake);
                    }
                }
                finally
                {
                    _wakeGate.Release();
                }

                // 16대가 동일 밀리초에 Activity를 열지 않도록 고정 지터를 둡니다.
                await Task.Delay(
                    200 + (index % 4) * 150,
                    cancellationToken).ConfigureAwait(false);

                AgentOperationInfo launchOperation;
                await _launchGate.WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
                try
                {
                    launchOperation = await AgentApi.LaunchAppAndWaitAsync(
                        device.Host,
                        device.Port,
                        packageName,
                        activityName,
                        extras,
                        forceRestart,
                        completionTimeout: TimeSpan.FromSeconds(15),
                        cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                }
                finally
                {
                    _launchGate.Release();
                }

                if (launchOperation?.Completed != true)
                {
                    return Failure(
                        device,
                        "launch",
                        launchOperation?.Message ??
                            "APP_STATE_ACTIVE 확인 시간 초과",
                        launchOperation);
                }

                await Task.Delay(
                    300 + (index % 3) * 200,
                    cancellationToken).ConfigureAwait(false);

                AgentOperationInfo captureOperation;
                await _captureGate.WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
                try
                {
                    captureOperation =
                        await AgentApi.StartCaptureUiAndWaitAsync(
                            device.Host,
                            device.Port,
                            completionTimeout: TimeSpan.FromSeconds(35),
                            cancellationToken: cancellationToken)
                            .ConfigureAwait(false);
                }
                finally
                {
                    _captureGate.Release();
                }

                if (captureOperation?.Completed != true)
                {
                    return Failure(
                        device,
                        "capture",
                        captureOperation?.Message ??
                            "첫 인코딩 프레임 확인 시간 초과",
                        captureOperation);
                }

                return new FleetDeviceResult
                {
                    Device = device,
                    Success = true,
                    Stage = "completed",
                    Message = "앱 실행 및 송출 시작 완료",
                    Operation = captureOperation
                };
            }
            catch (OperationCanceledException)
            {
                return Failure(device, "cancelled", "작업 취소됨");
            }
            catch (Exception ex)
            {
                return Failure(
                    device,
                    "exception",
                    $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        private static FleetDeviceResult Failure(
            QuestEndpoint device,
            string stage,
            string message,
            AgentOperationInfo operation = null)
        {
            return new FleetDeviceResult
            {
                Device = device,
                Success = false,
                Stage = stage,
                Message = message,
                Operation = operation
            };
        }
    }
}
