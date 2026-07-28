//using System;
//using System.Diagnostics;
//using System.Threading;
//using System.Threading.Tasks;

//namespace MultiQuest_Management
//{
//    /// <summary>
//    /// 신형 DeviceWindow와 기존 MainWindow 사이의 비동기 API 호환 계층입니다.
//    ///
//    /// 이 파일은 MainWindow.cs를 수정하지 않고 같은 partial class에
//    /// StartAppAsync / StopDeviceAppAsync를 추가합니다.
//    /// </summary>
//    public partial class MainWindow
//    {
//        /// <summary>
//        /// DeviceWindow에서 await 및 창 수명 CancellationToken을 사용할 수 있는
//        /// 개별 앱 실행 API입니다.
//        /// </summary>
//        public async Task<bool> StartAppAsync(
//            Device device,
//            int index,
//            CancellationToken cancellationToken = default,
//            bool showErrorUi = false)
//        {
//            if (device == null)
//            {
//                if (showErrorUi)
//                {
//                    ShowMsg("기기를 확인하세요.");
//                }

//                return false;
//            }

//            if (index < 0 ||
//                index >= _pkgNames.Length)
//            {
//                if (showErrorUi)
//                {
//                    ShowMsg("패키지 인덱스가 올바르지 않습니다.");
//                }

//                return false;
//            }

//            if (string.IsNullOrWhiteSpace(
//                    device.AgentHost))
//            {
//                if (showErrorUi)
//                {
//                    ShowMsg(
//                        "이 기기는 Agent로 연결되어 있지 않습니다. " +
//                        "Agent 검색을 먼저 실행하세요.");
//                }

//                return false;
//            }

//            using var timeoutCts =
//                CancellationTokenSource
//                    .CreateLinkedTokenSource(
//                        cancellationToken);

//            timeoutCts.CancelAfter(
//                IndividualLaunchTotalTimeoutMs);

//            try
//            {
//                DeviceAppLaunchResult result =
//                    await LaunchCandidatesByAgentFastAsync(
//                        device,
//                        _pkgNames[index],
//                        extras: null,
//                        timeoutCts.Token);

//                if (result?.Success == true)
//                {
//                    device.MirrorError = null;
//                    return true;
//                }

//                string state =
//                    result?.State ??
//                    "DISPATCH_FAILED";

//                string message =
//                    result?.Message ??
//                    "Agent가 앱 실행 명령을 접수하지 않았습니다.";

//                device.MirrorError =
//                    $"앱 실행 실패: {state} {message}";

//                Debug.WriteLine(
//                    $"[StartAppAsync/Compat] " +
//                    $"device={device.Name} " +
//                    $"host={device.AgentHost} " +
//                    $"state={state} " +
//                    $"message={message}");

//                if (showErrorUi)
//                {
//                    ShowMsg(
//                        $"{device.Name} 앱 실행 명령 전송 실패\n" +
//                        $"상태: {state}\n" +
//                        $"내용: {message}");
//                }

//                return false;
//            }
//            catch (OperationCanceledException)
//                when (cancellationToken.IsCancellationRequested)
//            {
//                /*
//                 * DeviceWindow 닫기 때문에 취소된 경우입니다.
//                 * 닫힌 창에서 예외 UI를 표시하지 않도록 호출자에게 취소를 전달합니다.
//                 */
//                throw;
//            }
//            catch (OperationCanceledException)
//            {
//                device.MirrorError =
//                    $"앱 실행 명령 응답 시간 초과 " +
//                    $"({IndividualLaunchTotalTimeoutMs / 1000.0:F1}초)";

//                Debug.WriteLine(
//                    $"[StartAppAsync/Compat] timeout. " +
//                    $"device={device.Name} " +
//                    $"host={device.AgentHost}");

//                if (showErrorUi)
//                {
//                    ShowMsg(
//                        $"{device.Name} 앱 실행 명령이 " +
//                        $"{IndividualLaunchTotalTimeoutMs / 1000.0:F1}초 안에 " +
//                        "접수되지 않았습니다.");
//                }

//                return false;
//            }
//            catch (Exception exception)
//            {
//                device.MirrorError =
//                    $"앱 실행 요청 오류: " +
//                    $"{exception.GetType().Name}: " +
//                    $"{exception.Message}";

//                Debug.WriteLine(
//                    $"[StartAppAsync/Compat] " +
//                    $"device={device.Name} error={exception}");

//                if (showErrorUi)
//                {
//                    ShowMsg(
//                        "앱 실행 중 오류가 발생했습니다.\n" +
//                        $"{exception.GetType().Name}: " +
//                        $"{exception.Message}");
//                }

//                return false;
//            }
//        }

//        /// <summary>
//        /// DeviceWindow에서 await 및 창 수명 CancellationToken을 사용할 수 있는
//        /// 개별 앱 종료 API입니다.
//        /// </summary>
//        public async Task<AgentCommandReply> StopDeviceAppAsync(
//            Device device,
//            CancellationToken cancellationToken = default,
//            bool showErrorUi = false)
//        {
//            if (device == null ||
//                string.IsNullOrWhiteSpace(
//                    device.AgentHost))
//            {
//                var invalidReply =
//                    new AgentCommandReply
//                    {
//                        Ok = false,
//                        Accepted = false,
//                        Completed = false,
//                        Retryable = false,
//                        Error = "invalid_device",
//                        Message =
//                            "Agent 기기 정보가 없습니다."
//                    };

//                if (showErrorUi)
//                {
//                    ShowMsg(
//                        invalidReply.Message);
//                }

//                return invalidReply;
//            }

//            try
//            {
//                AgentCommandReply reply =
//                    await StopDeviceUnifiedDetailedAsync(
//                        device,
//                        cancellationToken);

//                bool deliveryUncertain =
//                    reply?.TimedOut == true ||
//                    string.Equals(
//                        reply?.Error,
//                        "timeout",
//                        StringComparison.OrdinalIgnoreCase) ||
//                    string.Equals(
//                        reply?.Error,
//                        "network_error",
//                        StringComparison.OrdinalIgnoreCase);

//                if (reply?.IsAcceptedSuccess == true ||
//                    deliveryUncertain)
//                {
//                    /*
//                     * HTTP 응답 직후 Quest 내부에서 0초/1초/2초 STOP 재전송이 진행됩니다.
//                     * 창이 닫히면 이 대기만 취소되고, Quest에 접수된 종료 시퀀스는 계속됩니다.
//                     */
//                    await Task.Delay(
//                        FleetStopSettleDelayMs,
//                        cancellationToken);

//                    if (deliveryUncertain)
//                    {
//                        Debug.WriteLine(
//                            $"[StopDeviceAppAsync/Compat] " +
//                            $"delivery uncertain. " +
//                            $"device={device.Name} " +
//                            $"host={device.AgentHost} " +
//                            $"error={reply?.Error} " +
//                            $"message={reply?.Message}");
//                    }

//                    return reply;
//                }

//                if (showErrorUi)
//                {
//                    ShowMsg(
//                        $"{device.Name} 앱 종료 요청이 거부되었습니다.\n" +
//                        $"상태: {reply?.Error ?? "unknown_error"}\n" +
//                        $"내용: " +
//                        $"{reply?.Message ?? "Agent 응답을 확인하지 못했습니다."}");
//                }

//                return reply;
//            }
//            catch (OperationCanceledException)
//                when (cancellationToken.IsCancellationRequested)
//            {
//                /*
//                 * DeviceWindow 닫기 또는 사용자 취소입니다.
//                 * 예외 UI 없이 호출자에게 취소를 전달합니다.
//                 */
//                throw;
//            }
//            catch (Exception exception)
//            {
//                Debug.WriteLine(
//                    $"[StopDeviceAppAsync/Compat] " +
//                    $"device={device.Name} error={exception}");

//                var failureReply =
//                    new AgentCommandReply
//                    {
//                        Ok = false,
//                        Accepted = false,
//                        Completed = false,
//                        Retryable = true,
//                        Error = "client_exception",
//                        Message =
//                            $"{exception.GetType().Name}: " +
//                            $"{exception.Message}"
//                    };

//                if (showErrorUi)
//                {
//                    ShowMsg(
//                        $"{device.Name} 앱 종료 중 오류가 발생했습니다.\n" +
//                        failureReply.Message);
//                }

//                return failureReply;
//            }
//        }
//    }
//}