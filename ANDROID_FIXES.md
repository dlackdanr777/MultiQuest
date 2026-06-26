# Android Agent 수정 가이드

## 1. HTTP 포트를 18080으로 고정 (SimpleStatusHttpServer.kt)

```kotlin
class SimpleStatusHttpServer(
    private val scope: CoroutineScope,
    private val applicationContext: Context
) {
    private var serverSocket: ServerSocket? = null
    private var port: Int = 0

    fun start(): Int {
        val preferredPort = 18080

        val socket = ServerSocket()
        socket.reuseAddress = true

        try {
            socket.bind(InetSocketAddress("0.0.0.0", preferredPort))
        } catch (e: Exception) {
            // 혹시 이미 사용 중이면 랜덤 포트로 폴백
            socket.bind(InetSocketAddress("0.0.0.0", 0))
        }

        serverSocket = socket
        port = socket.localPort

        scope.launch(Dispatchers.IO) {
            acceptLoop(socket)
        }

        Log.i("MQ-HTTP", "HTTP server started on port=$port")
        return port
    }

    // ... 나머지 코드는 동일
}
```

**변경 사항:**
- `preferredPort = 18080` 고정
- mDNS 실패 시에도 PC에서 `http://192.168.0.xxx:18080/status` 로 직접 접근 가능
- 포트가 사용 중이면 자동으로 랜덤 포트로 폴백

---

## 2. NSD 재시작 디바운스 처리 (AgentService.kt)

```kotlin
class AgentService : Service() {
    // ... 기존 코드

    private var nsdRestartJob: kotlinx.coroutines.Job? = null
    private val nsdRestartLock = Any()

    private fun scheduleNsdRestart(reason: String) {
        synchronized(nsdRestartLock) {
            // 기존 예약 취소
            nsdRestartJob?.cancel()

            // 새로운 예약 생성
            nsdRestartJob = scope.launch {
                // 1.5초 대기 (여러 네트워크 이벤트를 한 번에 처리)
                delay(1500L)

                try {
                    Log.i("MQ-Agent", "NSD restart scheduled reason=$reason")

                    // 기존 등록 해제
                    nsdAdvertiser.unregister()

                    // unregisterService는 비동기라서 바로 register하면 꼬일 수 있음
                    delay(500L)

                    // 재등록
                    nsdAdvertiser.register(
                        statusPort = statusPort,
                        rtspPort = RTSP_PORT
                    )

                    val ip = AgentStatus.currentIpv4(applicationContext) ?: "unknown"
                    updateNotification("네트워크 갱신됨 $ip:$statusPort")
                    Log.i("MQ-Agent", "NSD restarted complete: $reason ip=$ip")
                } catch (e: Exception) {
                    Log.e("MQ-Agent", "NSD restart failed: $reason", e)
                }
            }
        }
    }

    // ... 기존 네트워크 콜백에서 호출
    private val networkCallback = object : ConnectivityManager.NetworkCallback() {
        override fun onAvailable(network: Network) {
            Log.i("MQ-Agent", "network_callback: wifi_available")
            scheduleNsdRestart("wifi_available")
        }

        override fun onCapabilitiesChanged(network: Network, capabilities: NetworkCapabilities) {
            if (capabilities.hasTransport(NetworkCapabilities.TRANSPORT_WIFI)) {
                Log.i("MQ-Agent", "network_callback: wifi_capabilities_changed")
                scheduleNsdRestart("wifi_capabilities_changed")
            }
        }

        override fun onLinkPropertiesChanged(network: Network, linkProperties: LinkProperties) {
            Log.i("MQ-Agent", "network_callback: link_properties_changed")
            scheduleNsdRestart("link_properties_changed")
        }

        override fun onLost(network: Network) {
            Log.i("MQ-Agent", "network_callback: wifi_lost")
            scheduleNsdRestart("wifi_lost")
        }
    }
}
```

**변경 사항:**
- 여러 네트워크 이벤트가 동시에 발생해도 1.5초 후 한 번만 NSD 재등록
- `nsdRestartJob?.cancel()`로 기존 예약 취소
- Unregistered 여러 번 → Registered 한 번으로 안정화

---

## 3. CaptureService 크래시 방지 (CaptureService.kt)

```kotlin
private fun drainEncoder(codec: MediaCodec) {
    val bufferInfo = MediaCodec.BufferInfo()

    try {
        while (scope.isActive) {
            val index = try {
                codec.dequeueOutputBuffer(bufferInfo, 10_000)
            } catch (e: IllegalStateException) {
                Log.w("MQ-CAPTURE", "dequeueOutputBuffer cancelled/stopped", e)
                break
            } catch (e: Exception) {
                Log.e("MQ-CAPTURE", "dequeueOutputBuffer failed", e)
                break
            }

            when {
                index == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED -> {
                    Log.i("MQ-CAPTURE", "output format changed: ${codec.outputFormat}")
                    rtspServer?.setFormat(codec.outputFormat)
                }

                index >= 0 -> {
                    val outBuffer = try {
                        codec.getOutputBuffer(index)
                    } catch (e: Exception) {
                        Log.w("MQ-CAPTURE", "getOutputBuffer failed", e)
                        null
                    }

                    if (
                        outBuffer != null &&
                        bufferInfo.size > 0 &&
                        (bufferInfo.flags and MediaCodec.BUFFER_FLAG_CODEC_CONFIG) == 0
                    ) {
                        outBuffer.position(bufferInfo.offset)
                        outBuffer.limit(bufferInfo.offset + bufferInfo.size)

                        val bytes = ByteArray(bufferInfo.size)
                        outBuffer.get(bytes)

                        val timestamp90k = bufferInfo.presentationTimeUs * 90L / 1000L
                        rtspServer?.pushAccessUnit(bytes, timestamp90k)
                    }

                    try {
                        codec.releaseOutputBuffer(index, false)
                    } catch (e: Exception) {
                        Log.w("MQ-CAPTURE", "releaseOutputBuffer failed", e)
                    }
                }
            }
        }
    } catch (e: Throwable) {
        // 최상위 예외 처리: 어떤 에러가 나도 앱 전체를 죽이지 않음
        Log.e("MQ-CAPTURE", "drainEncoder crashed but swallowed", e)
    } finally {
        Log.i("MQ-CAPTURE", "drainEncoder ended")
    }
}
```

**변경 사항:**
- 모든 MediaCodec 호출을 try-catch로 감쌈
- `dequeueOutputBuffer`, `getOutputBuffer`, `releaseOutputBuffer` 각각 예외 처리
- 최상위 `try-catch (e: Throwable)` 추가로 앱 프로세스 전체 크래시 방지
- 크래시가 나도 로그만 남기고 계속 실행

---

## 4. 검증 방법

### Android 로그 확인
```bash
adb logcat -s MQ-Agent MQ-HTTP MQ-CAPTURE
```

**성공 로그 예시:**
```
MQ-HTTP: HTTP server started on port=18080
MQ-Agent: NSD registered: _multiquest-agent._tcp.local. port=18080
MQ-Agent: network_callback: wifi_available
MQ-Agent: network_callback: wifi_capabilities_changed
MQ-Agent: network_callback: link_properties_changed
MQ-Agent: NSD restart scheduled reason=link_properties_changed
MQ-Agent: NSD Unregistered
MQ-Agent: NSD Registered: _multiquest-agent._tcp.local.
MQ-Agent: NSD restarted complete: link_properties_changed ip=192.168.0.243
```

### PC에서 직접 테스트
Quest의 IP 주소를 알고 있다면:
```bash
curl http://192.168.0.243:18080/status
```

**예상 응답:**
```json
{
  "deviceId": "...",
  "model": "Quest 2",
  "ip": "192.168.0.243",
  "statusPort": 18080,
  "rtspPort": 8554,
  "rtspUrl": "rtsp://192.168.0.243:8554/live",
  "battery": 85,
  "streamState": "streaming",
  "agentVersion": "1.0.0"
}
```

### WPF에서 Agent 검색 테스트
1. Agent 검색 버튼 클릭
2. 3초 대기
3. 발견된 Agent 목록 표시

---

## 우선순위별 적용 순서

### ? 즉시 적용 (가장 중요)
1. **HTTP 포트 고정 (18080)** - mDNS 실패해도 수동으로 연결 가능
2. **CaptureService 크래시 방지** - 앱이 죽지 않게 함

### ?? 안정성 개선
3. **NSD 재시작 디바운스** - 네트워크 이벤트 처리 안정화

### ? WPF는 이미 완료
- `AgentMdnsDiscovery` 추가 완료
- `QuestAgentInfo` 모델 업데이트 완료
- `AgentSearchButton_Click` 구현 완료

---

## 테스트 체크리스트

- [ ] Android Agent 실행 후 `adb logcat`에서 "HTTP server started on port=18080" 확인
- [ ] PC에서 `curl http://<quest-ip>:18080/status` 성공
- [ ] WPF Agent 검색 버튼으로 Agent 발견 확인
- [ ] 네트워크 변경 시 (WiFi 재연결) Agent가 죽지 않고 재등록되는지 확인
- [ ] 장시간 실행 시 CaptureService 크래시 없이 안정적인지 확인

