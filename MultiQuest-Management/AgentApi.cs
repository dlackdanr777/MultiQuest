using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MultiQuest_Management
{
    public sealed class AgentCommandReply
    {
        public bool Ok { get; set; }
        public bool Accepted { get; set; }
        public bool Completed { get; set; }
        public bool Retryable { get; set; }
        public bool Ignored { get; set; }
        public bool ActivityShown { get; set; }
        public bool AlreadyRunning { get; set; }
        public bool AlreadyInProgress { get; set; }
        public bool KeepAliveEnabled { get; set; }

        public int RetryAfterMs { get; set; }
        public int StreamGeneration { get; set; }

        public string OperationId { get; set; }
        public string State { get; set; }
        public string Error { get; set; }
        public string Message { get; set; }
        public string Reason { get; set; }
        public string PackageName { get; set; }

        public HttpStatusCode? StatusCode { get; set; }
        public bool TimedOut { get; set; }
        public string RawResponse { get; set; }

        public bool IsAcceptedSuccess =>
            Ok &&
            !Ignored &&
            (Accepted || Completed);

        /// <summary>
        /// 요청이 Agent에 도착했을 가능성은 있지만 응답 확인이 불가능한 상태입니다.
        /// timeout과 일반적인 network_error는 확정 실패로 단정하지 않습니다.
        /// </summary>
        public bool IsDeliveryUncertain =>
            !IsAcceptedSuccess &&
            (
                TimedOut ||
                string.Equals(
                    Error,
                    "timeout",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    Error,
                    "network_error",
                    StringComparison.OrdinalIgnoreCase)
            );

        /// <summary>
        /// Agent의 명시적 거부, 잘못된 요청, 유효하지 않은 응답 등
        /// 접수 실패로 확정할 수 있는 상태입니다.
        /// </summary>
        public bool IsExplicitFailure =>
            !IsAcceptedSuccess &&
            !IsDeliveryUncertain;
    }

    public sealed class AgentOperationEnvelope
    {
        public bool Ok { get; set; }
        public AgentOperationInfo Operation { get; set; }
    }

    public sealed class AgentOperationInfo
    {
        public bool Exists { get; set; }
        public bool Accepted { get; set; }
        public bool Completed { get; set; }
        public bool Failed { get; set; }
        public bool Cancelled { get; set; }
        public bool Retryable { get; set; }

        public string OperationId { get; set; }
        public string Type { get; set; }
        public string Target { get; set; }
        public string Reason { get; set; }
        public string State { get; set; }
        public string Message { get; set; }

        public long AcceptedAtMs { get; set; }
        public long UpdatedAtMs { get; set; }
        public long CompletedAtMs { get; set; }

        public JsonElement Details { get; set; }

        public bool IsTerminal =>
            Completed ||
            Failed ||
            Cancelled ||
            string.Equals(State, "COMPLETED", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(State, "FAILED", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(State, "CANCELLED", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Quest Agent HTTP API 클라이언트입니다.
    ///
    /// 핵심 원칙:
    /// - 하나의 HttpClient/연결 풀을 재사용합니다.
    /// - 요청별 타임아웃과 CancellationToken을 사용합니다.
    /// - HTTP 2xx만으로 성공 처리하지 않고 JSON의 ok/accepted/completed를 확인합니다.
    /// - Activity/MediaProjection 명령은 operationId로 실제 완료를 확인할 수 있습니다.
    /// </summary>
    public static class AgentApi
    {
        private const int DefaultPort = 18080;

        private static readonly TimeSpan StatusTimeout =
            TimeSpan.FromSeconds(5);
        private static readonly TimeSpan FastStatusTimeout =
            TimeSpan.FromSeconds(2);
        private static readonly TimeSpan CommandTimeout =
            TimeSpan.FromSeconds(7);

        // 전체 실행 5초 모드에서 각 후보 패키지 요청에 사용하는 제한 시간입니다.
        // 후보가 2개여도 약 3.4초 안에 접수 여부를 판정할 수 있습니다.
        private static readonly TimeSpan FastLaunchCommandTimeout =
            TimeSpan.FromMilliseconds(2_300);
        private static readonly TimeSpan StopAllTimeout =
            TimeSpan.FromSeconds(8);
        private static readonly TimeSpan FastStopAllTimeout =
            TimeSpan.FromMilliseconds(2_500);

        private const int StopAllBackgroundRetryDelayMs = 220;
        /*
         * 6~16대 동시 전송 환경에서는 1.5초가 너무 공격적이어서
         * Agent가 정상이어도 WPF가 먼저 취소하는 경우가 있었습니다.
         *
         * 동일 requestId 재전송은 Agent에서 중복 operation으로 합쳐지므로
         * HTTP 확인 요청을 한 번 더 시도해도 콘텐츠 명령이 중복 적용되지 않습니다.
         */
        private static readonly TimeSpan AppCommandHttpTimeout =
            TimeSpan.FromMilliseconds(2_300);

        private const int AppCommandHttpAttemptCount = 2;
        private const int AppCommandHttpRetryDelayMs = 180;

#if NET5_0_OR_GREATER
        private static readonly HttpMessageHandler Handler =
            new SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromMilliseconds(1_500),
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                MaxConnectionsPerServer = 32,
                AutomaticDecompression =
                    DecompressionMethods.GZip |
                    DecompressionMethods.Deflate
            };
#else
        private static readonly HttpMessageHandler Handler =
            new HttpClientHandler
            {
                AutomaticDecompression =
                    DecompressionMethods.GZip |
                    DecompressionMethods.Deflate
            };
#endif

        private static readonly HttpClient Http =
            new HttpClient(Handler)
            {
                Timeout = Timeout.InfiniteTimeSpan
            };

        private static readonly JsonSerializerOptions JsonOptions =
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };

#if !NET5_0_OR_GREATER
        static AgentApi()
        {
            ServicePointManager.DefaultConnectionLimit = Math.Max(
                ServicePointManager.DefaultConnectionLimit,
                64);
        }
#endif

        public static Task<QuestAgentInfo> GetStatusAsync(
            string host,
            int port = DefaultPort,
            CancellationToken cancellationToken = default)
        {
            return GetStatusCoreAsync(
                host,
                port,
                StatusTimeout,
                writeLog: true,
                cancellationToken);
        }

        public static Task<QuestAgentInfo> GetStatusFastAsync(
            string host,
            int port = DefaultPort,
            CancellationToken cancellationToken = default)
        {
            return GetStatusCoreAsync(
                host,
                port,
                FastStatusTimeout,
                writeLog: false,
                cancellationToken);
        }

        private static async Task<QuestAgentInfo> GetStatusCoreAsync(
            string host,
            int port,
            TimeSpan timeout,
            bool writeLog,
            CancellationToken cancellationToken)
        {
            if (!TryBuildUri(host, port, "/status", out Uri uri))
                return null;

            using var timeoutCts =
                CreateTimeoutToken(cancellationToken, timeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);

            try
            {
                using HttpResponseMessage response = await Http.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeoutCts.Token).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                    return null;

                string json = await response.Content
                    .ReadAsStringAsync()
                    .ConfigureAwait(false);

                QuestAgentInfo info = JsonSerializer.Deserialize<QuestAgentInfo>(
                    json,
                    JsonOptions);

                if (info != null)
                {
                    info.DeviceName = string.IsNullOrWhiteSpace(info.DeviceName)
                        ? null
                        : info.DeviceName.Trim();
                }

                if (writeLog)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[AgentApi] Status {host}:{NormalizePort(port)} " +
                        $"name='{info?.DeviceName}' model='{info?.Model}'");
                }

                return info;
            }
            catch (OperationCanceledException)
            {
                if (writeLog && !cancellationToken.IsCancellationRequested)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[AgentApi] Status timeout {host}:{NormalizePort(port)}");
                }
                return null;
            }
            catch (Exception ex)
            {
                if (writeLog)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[AgentApi] GetStatus failed: {ex.GetType().Name}: {ex.Message}");
                }
                return null;
            }
        }

        public static Task<AgentCommandReply> SetDeviceNameDetailedAsync(
            string host,
            int port,
            string deviceName,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(deviceName))
                return Task.FromResult(
                    InvalidRequest("deviceName is required"));

            return SendCommandAsync(
                host,
                port,
                "/command/setDeviceName",
                new
                {
                    deviceName = deviceName.Trim()
                },
                CommandTimeout,
                cancellationToken);
        }

        public static async Task<bool> SetDeviceNameAsync(
            string host,
            int port,
            string deviceName,
            CancellationToken cancellationToken = default)
        {
            AgentCommandReply reply =
                await SetDeviceNameDetailedAsync(
                    host,
                    port,
                    deviceName,
                    cancellationToken)
                .ConfigureAwait(false);

            return reply.IsAcceptedSuccess;
        }

        public static async Task<AgentCommandReply> LaunchAppDetailedAsync(
            string host,
            int port,
            string packageName,
            string activityName = "com.unity3d.player.UnityPlayerActivity",
            object extras = null,
            bool forceRestart = false,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(packageName))
                return InvalidRequest("packageName is required");

            return await SendCommandAsync(
                host,
                port,
                "/command/launch",
                new
                {
                    packageName,
                    activityName,
                    forceRestart,
                    stopOthers = true,
                    stopRetryCount = 1,
                    stopRetryIntervalMs = 100,

                    // Quest 시스템 UI/Agent task 정리 경쟁을 막는 짧은 전면 포커스 안정화입니다.
                    focusGuard = true,

                    extras = extras ?? new { }
                },
                CommandTimeout,
                cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// 16대 전체 실행 전용 빠른 launch 요청입니다.
        ///
        /// Android Agent에는 fastDispatch=true를 전달하여 HTTP 응답을
        /// 실제 APP_STATE_ACTIVE 완료 전에 즉시 202 Accepted로 반환하게 합니다.
        /// 이전 StoryWing 앱 종료 반복과 forceRestart를 생략하여 지연을 줄입니다.
        /// </summary>
        public static async Task<AgentCommandReply>
            LaunchAppFastDetailedAsync(
                string host,
                int port,
                string packageName,
                string activityName =
                    "com.unity3d.player.UnityPlayerActivity",
                object extras = null,
                CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(packageName))
            {
                return InvalidRequest(
                    "packageName is required");
            }

            return await SendCommandAsync(
                host,
                port,
                "/command/launch",
                new
                {
                    packageName,
                    activityName,

                    // 5초 전체 실행 모드:
                    // 앱 프로세스를 강제로 재시작하지 않고 기존 Activity를 재사용합니다.
                    forceRestart = false,

                    // 다른 StoryWing 앱 종료 반복을 하지 않고 대상 Activity를 즉시 전면 실행합니다.
                    stopOthers = false,
                    stopRetryCount = 0,
                    stopRetryIntervalMs = 0,

                    // Agent가 메인 스레드 launch를 큐에 넣은 뒤 즉시 202를 반환합니다.
                    fastDispatch = true,

                    // 앱 실행 직후 Agent task 정리 때문에 Quest Universal Menu가
                    // 간헐적으로 전면에 남는 경쟁 상태를 Android 내부에서 보정합니다.
                    focusGuard = true,

                    extras = extras ?? new { }
                },
                FastLaunchCommandTimeout,
                cancellationToken)
                .ConfigureAwait(false);
        }

        public static async Task<bool> LaunchAppFastAsync(
            string host,
            int port,
            string packageName,
            string activityName =
                "com.unity3d.player.UnityPlayerActivity",
            object extras = null,
            CancellationToken cancellationToken = default)
        {
            AgentCommandReply reply =
                await LaunchAppFastDetailedAsync(
                    host,
                    port,
                    packageName,
                    activityName,
                    extras,
                    cancellationToken)
                .ConfigureAwait(false);

            return
                reply.IsAcceptedSuccess ||
                reply.IsDeliveryUncertain;
        }

        public static async Task<bool> LaunchAppAsync(
            string host,
            int port,
            string packageName,
            string activityName = "com.unity3d.player.UnityPlayerActivity",
            object extras = null,
            bool forceRestart = false,
            CancellationToken cancellationToken = default)
        {
            AgentCommandReply reply = await LaunchAppDetailedAsync(
                host,
                port,
                packageName,
                activityName,
                extras,
                forceRestart,
                cancellationToken).ConfigureAwait(false);

            return reply.IsAcceptedSuccess;
        }

        public static async Task<AgentOperationInfo> LaunchAppAndWaitAsync(
            string host,
            int port,
            string packageName,
            string activityName = "com.unity3d.player.UnityPlayerActivity",
            object extras = null,
            bool forceRestart = false,
            TimeSpan? completionTimeout = null,
            CancellationToken cancellationToken = default)
        {
            AgentCommandReply reply = await LaunchAppDetailedAsync(
                host,
                port,
                packageName,
                activityName,
                extras,
                forceRestart,
                cancellationToken).ConfigureAwait(false);

            return await WaitForAcceptedCommandAsync(
                host,
                port,
                reply,
                completionTimeout ?? TimeSpan.FromSeconds(35),
                cancellationToken).ConfigureAwait(false);
        }

        public static async Task<AgentCommandReply> StopAppDetailedAsync(
            string host,
            int port,
            string packageName,
            bool goHome = false,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(packageName))
                return InvalidRequest("packageName is required");

            return await SendCommandAsync(
                host,
                port,
                "/command/stop",
                new
                {
                    packageName,
                    goHome = false,
                    disableUi = true,
                    retryCount = 3,
                    retryIntervalMs = 1_000,
                    goHomeDelayMs = 0
                },
                CommandTimeout,
                cancellationToken).ConfigureAwait(false);
        }

        public static async Task<bool> StopAppAsync(
            string host,
            int port,
            string packageName,
            bool goHome = false,
            CancellationToken cancellationToken = default)
        {
            AgentCommandReply reply = await StopAppDetailedAsync(
                host,
                port,
                packageName,
                goHome,
                cancellationToken).ConfigureAwait(false);

            return reply.IsAcceptedSuccess;
        }

        public static Task<AgentCommandReply> RestartCaptureDetailedAsync(
            string host,
            int port = DefaultPort,
            CancellationToken cancellationToken = default)
        {
            return SendCommandAsync(
                host,
                port,
                "/command/restartCapture",
                new { },
                CommandTimeout,
                cancellationToken);
        }

        public static async Task<bool> RestartCaptureAsync(
            string host,
            int port = DefaultPort,
            CancellationToken cancellationToken = default)
        {
            AgentCommandReply reply = await RestartCaptureDetailedAsync(
                host,
                port,
                cancellationToken).ConfigureAwait(false);

            return reply.IsAcceptedSuccess;
        }

        public static async Task<AgentOperationInfo> RestartCaptureAndWaitAsync(
            string host,
            int port = DefaultPort,
            TimeSpan? completionTimeout = null,
            CancellationToken cancellationToken = default)
        {
            AgentCommandReply reply = await RestartCaptureDetailedAsync(
                host,
                port,
                cancellationToken).ConfigureAwait(false);

            return await WaitForAcceptedCommandAsync(
                host,
                port,
                reply,
                completionTimeout ?? TimeSpan.FromSeconds(35),
                cancellationToken).ConfigureAwait(false);
        }

        public static Task<AgentCommandReply> StartCaptureUiDetailedAsync(
            string host,
            int port = DefaultPort,
            CancellationToken cancellationToken = default)
        {
            return SendCommandAsync(
                host,
                port,
                "/command/startCaptureUi",
                new { },
                CommandTimeout,
                cancellationToken);
        }

        public static async Task<bool> StartCaptureUiAsync(
            string host,
            int port = DefaultPort,
            CancellationToken cancellationToken = default)
        {
            AgentCommandReply reply = await StartCaptureUiDetailedAsync(
                host,
                port,
                cancellationToken).ConfigureAwait(false);

            return reply.IsAcceptedSuccess;
        }

        public static async Task<AgentOperationInfo> StartCaptureUiAndWaitAsync(
            string host,
            int port = DefaultPort,
            TimeSpan? completionTimeout = null,
            CancellationToken cancellationToken = default)
        {
            AgentCommandReply reply = await StartCaptureUiDetailedAsync(
                host,
                port,
                cancellationToken).ConfigureAwait(false);

            return await WaitForAcceptedCommandAsync(
                host,
                port,
                reply,
                completionTimeout ?? TimeSpan.FromSeconds(35),
                cancellationToken).ConfigureAwait(false);
        }

        public static async Task<bool> GoHomeAsync(
            string host,
            int port = DefaultPort,
            CancellationToken cancellationToken = default)
        {
            AgentCommandReply reply = await SendCommandAsync(
                host,
                port,
                "/command/home",
                new { },
                CommandTimeout,
                cancellationToken).ConfigureAwait(false);

            return reply.IsAcceptedSuccess;
        }

        public static async Task<AgentCommandReply>
            SendReliableAppCommandDetailedAsync(
                string host,
                int port,
                string packageName,
                string command,
                Dictionary<string, object> args = null,
                string requestId = null,
                int retryCount = 4,
                int retryIntervalMs = 700,
                CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(packageName) ||
                string.IsNullOrWhiteSpace(command))
            {
                return InvalidRequest(
                    "packageName and command are required");
            }

            string normalizedRequestId =
                string.IsNullOrWhiteSpace(requestId)
                    ? Guid.NewGuid().ToString("N")
                    : requestId.Trim();

            var requestBody = new
            {
                packageName,
                command,
                payload = args ??
                    new Dictionary<string, object>(),
                requestId = normalizedRequestId,
                retryCount = Math.Max(
                    1,
                    Math.Min(
                        9,
                        retryCount)),
                retryIntervalMs = Math.Max(
                    50,
                    Math.Min(
                        2_000,
                        retryIntervalMs))
            };

            AgentCommandReply lastReply = null;

            for (int attempt = 1;
                 attempt <= AppCommandHttpAttemptCount;
                 attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                lastReply =
                    await SendCommandAsync(
                        host,
                        port,
                        "/command/appCommand",
                        requestBody,
                        AppCommandHttpTimeout,
                        cancellationToken)
                    .ConfigureAwait(false);

                System.Diagnostics.Debug.WriteLine(
                    $"[AgentApi/AppCommand] " +
                    $"host={host}:{NormalizePort(port)} " +
                    $"package={packageName} " +
                    $"attempt={attempt}/{AppCommandHttpAttemptCount} " +
                    $"accepted={lastReply?.IsAcceptedSuccess} " +
                    $"uncertain={lastReply?.IsDeliveryUncertain} " +
                    $"retryable={lastReply?.Retryable} " +
                    $"error={lastReply?.Error} " +
                    $"requestId={normalizedRequestId}");

                if (lastReply?.IsAcceptedSuccess == true)
                {
                    return lastReply;
                }

                /*
                 * package_not_installed, bad_request 등 명시적 비재시도 오류는
                 * 같은 패키지로 다시 보내도 결과가 바뀌지 않습니다.
                 */
                if (lastReply?.IsExplicitFailure == true &&
                    lastReply.Retryable != true)
                {
                    return lastReply;
                }

                if (attempt <
                    AppCommandHttpAttemptCount)
                {
                    await Task.Delay(
                            AppCommandHttpRetryDelayMs,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            return lastReply ??
                new AgentCommandReply
                {
                    Ok = false,
                    Accepted = false,
                    Completed = false,
                    Retryable = true,
                    Error = "no_response",
                    Message =
                        "Agent appCommand response was not received."
                };
        }

        public static async Task<AgentOperationInfo>
            SendReliableAppCommandAndWaitAsync(
                string host,
                int port,
                string packageName,
                string command,
                Dictionary<string, object> args = null,
                string requestId = null,
                int retryCount = 4,
                int retryIntervalMs = 700,
                TimeSpan? completionTimeout = null,
                CancellationToken cancellationToken = default)
        {
            AgentCommandReply reply =
                await SendReliableAppCommandDetailedAsync(
                    host,
                    port,
                    packageName,
                    command,
                    args,
                    requestId,
                    retryCount,
                    retryIntervalMs,
                    cancellationToken)
                .ConfigureAwait(false);

            return await WaitForAcceptedCommandAsync(
                host,
                port,
                reply,
                completionTimeout ?? TimeSpan.FromSeconds(7),
                cancellationToken)
                .ConfigureAwait(false);
        }

        public static async Task<bool> SendCommandAsync(
            string host,
            int port,
            string packageName,
            string command,
            Dictionary<string, object> args = null,
            CancellationToken cancellationToken = default)
        {
            AgentCommandReply reply =
                await SendReliableAppCommandDetailedAsync(
                    host,
                    port,
                    packageName,
                    command,
                    args,
                    retryCount: 1,
                    retryIntervalMs: 700,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return reply.IsAcceptedSuccess;
        }

        public static Task<AgentCommandReply> WakeScreenDetailedAsync(
            string host,
            int port = DefaultPort,
            bool keepAwake = true,
            CancellationToken cancellationToken = default)
        {
            return SendCommandAsync(
                host,
                port,
                "/command/wake",
                new { keepAwake },
                CommandTimeout,
                cancellationToken);
        }

        public static async Task<bool> WakeScreenAsync(
            string host,
            int port = DefaultPort,
            bool keepAwake = true,
            CancellationToken cancellationToken = default)
        {
            AgentCommandReply reply = await WakeScreenDetailedAsync(
                host,
                port,
                keepAwake,
                cancellationToken).ConfigureAwait(false);

            return reply.IsAcceptedSuccess;
        }

        public static async Task<AgentOperationInfo> WakeScreenAndWaitAsync(
            string host,
            int port = DefaultPort,
            bool keepAwake = true,
            TimeSpan? completionTimeout = null,
            CancellationToken cancellationToken = default)
        {
            AgentCommandReply reply = await WakeScreenDetailedAsync(
                host,
                port,
                keepAwake,
                cancellationToken).ConfigureAwait(false);

            return await WaitForAcceptedCommandAsync(
                host,
                port,
                reply,
                completionTimeout ?? TimeSpan.FromSeconds(5),
                cancellationToken).ConfigureAwait(false);
        }

        public static async Task<bool> KeepAwakeAsync(
            string host,
            int port = DefaultPort,
            bool enabled = true,
            CancellationToken cancellationToken = default)
        {
            AgentCommandReply reply = await SendCommandAsync(
                host,
                port,
                "/command/keepAwake",
                new { enabled },
                CommandTimeout,
                cancellationToken).ConfigureAwait(false);

            return reply.IsAcceptedSuccess;
        }

        public static async Task<bool> ShowKeepAwakeAsync(
            string host,
            int port = DefaultPort,
            CancellationToken cancellationToken = default)
        {
            AgentCommandReply reply = await SendCommandAsync(
                host,
                port,
                "/command/showKeepAwake",
                new { },
                CommandTimeout,
                cancellationToken).ConfigureAwait(false);

            return reply.IsAcceptedSuccess;
        }

        public static async Task<bool> HideKeepAwakeAsync(
            string host,
            int port = DefaultPort,
            CancellationToken cancellationToken = default)
        {
            AgentCommandReply reply = await SendCommandAsync(
                host,
                port,
                "/command/hideKeepAwake",
                new { },
                CommandTimeout,
                cancellationToken).ConfigureAwait(false);

            return reply.IsAcceptedSuccess;
        }

        public static async Task<bool> StopAllStoryWingAsync(
            string host,
            int port = DefaultPort,
            bool goHome = true,
            IEnumerable<string> fallbackPackages = null,
            CancellationToken cancellationToken = default)
        {
            AgentCommandReply reply = await SendCommandAsync(
                host,
                port,
                "/command/stopAllStoryWing",
                new
                {
                    goHome = false,
                    disableUi = true,
                    retryCount = 3,
                    retryIntervalMs = 1_000,
                    goHomeDelayMs = 0,
                    fallbackAllPackages = true,
                    priorityPackages = new[]
                    {
                        "com.StoryWing.XR_Coding",
                        "com.StoryWing.Storywing_Class",
                        "com.StoryWing.StorywingClass"
                    }
                },
                StopAllTimeout,
                cancellationToken).ConfigureAwait(false);

            if (reply.IsAcceptedSuccess)
                return true;

            bool unsupported =
                reply.StatusCode == HttpStatusCode.NotFound ||
                reply.StatusCode == HttpStatusCode.MethodNotAllowed ||
                reply.StatusCode == HttpStatusCode.NotImplemented;

            if (!unsupported)
                return false;

            List<string> packages = fallbackPackages?
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<string>();

            if (packages.Count == 0)
                return false;

            bool stopsOk = await StopFallbackPackagesAsync(
                host,
                port,
                packages,
                cancellationToken).ConfigureAwait(false);

            bool homeOk = !goHome || await GoHomeAsync(
                host,
                port,
                cancellationToken).ConfigureAwait(false);

            return stopsOk && homeOk;
        }

        /// <summary>
        /// 개별/전체 종료에서 공통으로 사용하는 상세 응답 경로입니다.
        /// Agent는 HTTP 요청을 한 번 받고 내부에서 0초/1초/2초에 STOP을
        /// 재전송합니다. bool로 평탄화하기 전에 timeout과 명시적 거부를
        /// 구분할 수 있습니다.
        /// </summary>
        public static async Task<AgentCommandReply>
            StopAllStoryWingFastDetailedAsync(
                string host,
                int port = DefaultPort,
                bool goHome = true,
                int retryCount = 3,
                int retryIntervalMs = 1_000,
                int goHomeDelayMs = 0,
                CancellationToken cancellationToken = default)
        {
            var requestBody = new
            {
                goHome = false,
                disableUi = true,
                retryCount,
                retryIntervalMs,
                goHomeDelayMs = 0,
                fallbackAllPackages = true,
                priorityPackages = new[]
                {
                    "com.StoryWing.XR_Coding",
                    "com.StoryWing.Storywing_Class",
                    "com.StoryWing.StorywingClass"
                },
                fast = true
            };

            AgentCommandReply reply =
                await SendCommandAsync(
                    host,
                    port,
                    "/command/stopAllStoryWing",
                    requestBody,
                    FastStopAllTimeout,
                    cancellationToken)
                .ConfigureAwait(false);

            if (
                reply?.IsDeliveryUncertain == true &&
                !cancellationToken.IsCancellationRequested
            )
            {
                /*
                 * 첫 요청은 Quest에 도착했지만 응답만 유실됐을 수 있습니다.
                 * DeviceWindow/UI를 더 오래 막지 않고 동일 STOP 요청을
                 * 백그라운드에서 한 번 더 확인합니다.
                 *
                 * STOP은 멱등적이며 Agent의 새 시퀀스가 기존 시퀀스를 대체하므로
                 * 레거시 앱 종료 안정성을 높이면서 사용자 UI 지연을 만들지 않습니다.
                 */
                _ = RetryStopAllInBackgroundAsync(
                    host,
                    port,
                    requestBody);
            }

            return reply;
        }

        private static async Task RetryStopAllInBackgroundAsync(
            string host,
            int port,
            object requestBody)
        {
            try
            {
                await Task.Delay(
                        StopAllBackgroundRetryDelayMs)
                    .ConfigureAwait(false);

                AgentCommandReply retryReply =
                    await SendCommandAsync(
                        host,
                        port,
                        "/command/stopAllStoryWing",
                        requestBody,
                        FastStopAllTimeout,
                        CancellationToken.None)
                    .ConfigureAwait(false);

                System.Diagnostics.Debug.WriteLine(
                    $"[AgentApi/StopAllRetry] " +
                    $"host={host}:{NormalizePort(port)} " +
                    $"accepted={retryReply?.IsAcceptedSuccess} " +
                    $"uncertain={retryReply?.IsDeliveryUncertain} " +
                    $"error={retryReply?.Error} " +
                    $"message={retryReply?.Message}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[AgentApi/StopAllRetry] " +
                    $"host={host}:{NormalizePort(port)} " +
                    $"error={ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// 기존 bool 호출부 호환용입니다.
        /// </summary>
        public static async Task<bool> StopAllStoryWingFastAsync(
            string host,
            int port = DefaultPort,
            bool goHome = true,
            int retryCount = 3,
            int retryIntervalMs = 1_000,
            int goHomeDelayMs = 0,
            CancellationToken cancellationToken = default)
        {
            AgentCommandReply reply =
                await StopAllStoryWingFastDetailedAsync(
                    host,
                    port,
                    goHome,
                    retryCount,
                    retryIntervalMs,
                    goHomeDelayMs,
                    cancellationToken)
                .ConfigureAwait(false);

            return
                reply.IsAcceptedSuccess ||
                reply.IsDeliveryUncertain;
        }

        /// <summary>
        /// operation 상태를 한 번만 조회합니다.
        /// 빠른 전체 실행 후 백그라운드 저빈도 검증에 사용합니다.
        /// </summary>
        public static async Task<AgentOperationInfo> GetOperationAsync(
            string host,
            int port,
            string operationId,
            TimeSpan? requestTimeout = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(operationId))
            {
                return FailureOperation(
                    null,
                    "INVALID_OPERATION_ID",
                    "operationId is required",
                    retryable: false);
            }

            if (!TryBuildUri(
                    host,
                    port,
                    $"/operations/{Uri.EscapeDataString(operationId)}",
                    out Uri uri))
            {
                return FailureOperation(
                    operationId,
                    "INVALID_ENDPOINT",
                    "Invalid host or port",
                    retryable: false);
            }

            using var timeoutCts =
                CreateTimeoutToken(
                    cancellationToken,
                    requestTimeout ??
                        TimeSpan.FromSeconds(2));

            using var request =
                new HttpRequestMessage(
                    HttpMethod.Get,
                    uri);

            try
            {
                using HttpResponseMessage response =
                    await Http.SendAsync(
                        request,
                        HttpCompletionOption
                            .ResponseHeadersRead,
                        timeoutCts.Token)
                    .ConfigureAwait(false);

                string json =
                    await response.Content
                        .ReadAsStringAsync()
                    .ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    AgentOperationEnvelope envelope =
                        JsonSerializer
                            .Deserialize<AgentOperationEnvelope>(
                                json,
                                JsonOptions);

                    return envelope?.Operation ??
                        FailureOperation(
                            operationId,
                            "PROTOCOL_ERROR",
                            "Agent returned no operation object",
                            retryable: false);
                }

                if (response.StatusCode ==
                    HttpStatusCode.NotFound)
                {
                    return FailureOperation(
                        operationId,
                        "NOT_FOUND",
                        "Operation was not found; " +
                        "the Agent process may have restarted",
                        retryable: true);
                }

                return FailureOperation(
                    operationId,
                    $"HTTP_{(int)response.StatusCode}",
                    json,
                    retryable:
                        (int)response.StatusCode >= 500);
            }
            catch (OperationCanceledException)
                when (
                    cancellationToken
                        .IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return FailureOperation(
                    operationId,
                    "TIMEOUT",
                    "Operation status request timed out",
                    retryable: true);
            }
            catch (HttpRequestException ex)
            {
                return FailureOperation(
                    operationId,
                    "NETWORK_ERROR",
                    ex.Message,
                    retryable: true);
            }
            catch (JsonException ex)
            {
                return FailureOperation(
                    operationId,
                    "PROTOCOL_ERROR",
                    ex.Message,
                    retryable: false);
            }
            catch (Exception ex)
            {
                return FailureOperation(
                    operationId,
                    "CLIENT_EXCEPTION",
                    ex.Message,
                    retryable: false);
            }
        }

        public static async Task<AgentOperationInfo> WaitForOperationAsync(
            string host,
            int port,
            string operationId,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(operationId))
            {
                return FailureOperation(
                    null,
                    "INVALID_OPERATION_ID",
                    "operationId is required",
                    retryable: false);
            }

            if (!TryBuildUri(
                host,
                port,
                $"/operations/{Uri.EscapeDataString(operationId)}",
                out Uri uri))
            {
                return FailureOperation(
                    operationId,
                    "INVALID_ENDPOINT",
                    "Invalid host or port",
                    retryable: false);
            }

            using var timeoutCts =
                CreateTimeoutToken(cancellationToken, timeout);

            while (!timeoutCts.IsCancellationRequested)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);

                try
                {
                    using HttpResponseMessage response = await Http.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        timeoutCts.Token).ConfigureAwait(false);

                    string json = await response.Content
                        .ReadAsStringAsync()
                        .ConfigureAwait(false);

                    if (response.IsSuccessStatusCode)
                    {
                        AgentOperationEnvelope envelope =
                            JsonSerializer.Deserialize<AgentOperationEnvelope>(
                                json,
                                JsonOptions);

                        AgentOperationInfo operation = envelope?.Operation;
                        if (operation?.IsTerminal == true)
                            return operation;
                    }
                    else if (response.StatusCode == HttpStatusCode.NotFound)
                    {
                        return FailureOperation(
                            operationId,
                            "NOT_FOUND",
                            "Operation was not found; the Agent process may have restarted",
                            retryable: true);
                    }
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (HttpRequestException)
                {
                    // 일시적인 Wi-Fi 손실은 제한 시간 안에서 다시 조회합니다.
                }
                catch (JsonException ex)
                {
                    return FailureOperation(
                        operationId,
                        "PROTOCOL_ERROR",
                        ex.Message,
                        retryable: false);
                }

                try
                {
                    await Task.Delay(300, timeoutCts.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            return FailureOperation(
                operationId,
                "TIMEOUT",
                $"Operation did not complete within {timeout.TotalSeconds:0.#} seconds",
                retryable: true);
        }

        private static async Task<AgentOperationInfo> WaitForAcceptedCommandAsync(
            string host,
            int port,
            AgentCommandReply reply,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            if (reply == null)
            {
                return FailureOperation(
                    operationId: null,
                    state: "CLIENT_ERROR",
                    message: "Agent command returned no response",
                    retryable: true);
            }

            if (!reply.IsAcceptedSuccess)
            {
                return FailureOperation(
                    operationId: reply.OperationId,
                    state: reply.TimedOut ? "TIMEOUT" : "REJECTED",
                    message: reply.Message ?? reply.Error ?? "Agent command rejected",
                    retryable: reply.Retryable);
            }

            if (reply.Completed)
            {
                return new AgentOperationInfo
                {
                    Exists = true,
                    Accepted = true,
                    Completed = true,
                    State = "COMPLETED",
                    OperationId = reply.OperationId,
                    Message = reply.Message
                };
            }

            if (string.IsNullOrWhiteSpace(reply.OperationId))
            {
                return FailureOperation(
                    null,
                    "PROTOCOL_ERROR",
                    "Agent accepted the command without an operationId",
                    retryable: false);
            }

            return await WaitForOperationAsync(
                host,
                port,
                reply.OperationId,
                timeout,
                cancellationToken).ConfigureAwait(false);
        }

        private static async Task<bool> StopFallbackPackagesAsync(
            string host,
            int port,
            IReadOnlyCollection<string> packages,
            CancellationToken cancellationToken)
        {
            using var gate = new SemaphoreSlim(4, 4);

            Task<bool>[] tasks = packages.Select(async packageName =>
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    return await StopAppAsync(
                        host,
                        port,
                        packageName,
                        goHome: false,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    gate.Release();
                }
            }).ToArray();

            bool[] results = await Task.WhenAll(tasks).ConfigureAwait(false);
            return results.All(x => x);
        }

        private static async Task<AgentCommandReply> SendCommandAsync(
            string host,
            int port,
            string path,
            object body,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            if (!TryBuildUri(host, port, path, out Uri uri))
                return InvalidRequest("Invalid host or port");

            using var timeoutCts =
                CreateTimeoutToken(cancellationToken, timeout);
            using var request = new HttpRequestMessage(HttpMethod.Post, uri);

            string json = JsonSerializer.Serialize(body ?? new { }, JsonOptions);
            request.Content = new StringContent(
                json,
                Encoding.UTF8,
                "application/json");

            try
            {
                using HttpResponseMessage response = await Http.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeoutCts.Token).ConfigureAwait(false);

                string responseText = await response.Content
                    .ReadAsStringAsync()
                    .ConfigureAwait(false);

                AgentCommandReply reply = ParseCommandReply(
                    responseText,
                    response.StatusCode);

                System.Diagnostics.Debug.WriteLine(
                    $"[AgentApi] POST {host}:{NormalizePort(port)}{path} " +
                    $"-> {(int)response.StatusCode} {responseText}");

                return reply;
            }
            catch (OperationCanceledException)
                when (!cancellationToken.IsCancellationRequested)
            {
                return new AgentCommandReply
                {
                    Ok = false,
                    Accepted = false,
                    Completed = false,
                    Retryable = true,
                    TimedOut = true,
                    Error = "timeout",
                    Message = $"Request timed out after {timeout.TotalMilliseconds:0} ms"
                };
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException ex)
            {
                return new AgentCommandReply
                {
                    Ok = false,
                    Accepted = false,
                    Completed = false,
                    Retryable = true,
                    Error = "network_error",
                    Message = ex.Message
                };
            }
            catch (Exception ex)
            {
                return new AgentCommandReply
                {
                    Ok = false,
                    Accepted = false,
                    Completed = false,
                    Retryable = false,
                    Error = "client_exception",
                    Message = ex.Message
                };
            }
        }

        private static AgentCommandReply ParseCommandReply(
            string responseText,
            HttpStatusCode statusCode)
        {
            AgentCommandReply reply;

            if (statusCode == HttpStatusCode.NoContent)
            {
                reply = new AgentCommandReply
                {
                    Ok = true,
                    Accepted = true,
                    Completed = true
                };
            }
            else
            {
                try
                {
                    reply = JsonSerializer.Deserialize<AgentCommandReply>(
                        responseText,
                        JsonOptions);
                }
                catch (JsonException)
                {
                    reply = null;
                }

                if (reply == null)
                {
                    reply = new AgentCommandReply
                    {
                        Ok = false,
                        Accepted = false,
                        Completed = false,
                        Retryable = false,
                        Error = "invalid_response",
                        Message = "Agent response was not valid JSON"
                    };
                }
            }

            reply.StatusCode = statusCode;
            reply.RawResponse = responseText;

            if (!IsSuccessStatusCode(statusCode))
            {
                reply.Ok = false;
            }

            return reply;
        }

        private static bool TryBuildUri(
            string host,
            int port,
            string path,
            out Uri uri)
        {
            uri = null;
            if (string.IsNullOrWhiteSpace(host))
                return false;

            int normalizedPort = NormalizePort(port);
            if (normalizedPort < 1 || normalizedPort > 65535)
                return false;

            try
            {
                string trimmedHost = host.Trim();
                if (trimmedHost.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    trimmedHost.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    var existing = new Uri(trimmedHost);
                    trimmedHost = existing.Host;
                }

                var builder = new UriBuilder(
                    Uri.UriSchemeHttp,
                    trimmedHost,
                    normalizedPort,
                    path.StartsWith("/", StringComparison.Ordinal)
                        ? path
                        : "/" + path);

                uri = builder.Uri;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static int NormalizePort(int port) =>
            port > 0 ? port : DefaultPort;

        private static CancellationTokenSource CreateTimeoutToken(
            CancellationToken cancellationToken,
            TimeSpan timeout)
        {
            CancellationTokenSource cts =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
            cts.CancelAfter(timeout);
            return cts;
        }

        private static AgentOperationInfo FailureOperation(
            string operationId,
            string state,
            string message,
            bool retryable)
        {
            return new AgentOperationInfo
            {
                Exists = !string.IsNullOrWhiteSpace(operationId),
                Accepted = false,
                Completed = false,
                Failed = true,
                Retryable = retryable,
                OperationId = operationId,
                State = state,
                Message = message
            };
        }

        private static AgentCommandReply InvalidRequest(string message)
        {
            return new AgentCommandReply
            {
                Ok = false,
                Accepted = false,
                Completed = false,
                Retryable = false,
                Error = "invalid_request",
                Message = message
            };
        }

        private static bool IsSuccessStatusCode(HttpStatusCode statusCode)
        {
            int code = (int)statusCode;
            return code >= 200 && code <= 299;
        }
    }
}