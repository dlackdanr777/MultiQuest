# RTSP URL 기반 무조건 재생 시도 구현

## 문제 분석

### 현재 상황
```
Agent 발견: ?
RTSP URL 있음: ?
streamState: "stopped" ??
8554 포트 체크: 실패 ??
→ WPF가 재생 시도조차 안 함 ?
```

### 근본 원인
**두 겹의 문제:**
1. **Android Agent:** UI는 "화면 송출중"인데 `/status`는 `streamState: "stopped"`
2. **WPF:** streamState 또는 포트 체크 실패 시 재생 자체를 시작 안 함

---

## 해결 방법

### 핵심 아이디어
**RTSP URL만 있으면 무조건 재생 시도 → LibVLC가 직접 연결 판단**

```csharp
// 변경 전: 엄격한 사전 필터링
if (isStreaming || rtspPortOpen)  // ← 둘 다 false면 차단
{
    rtspAgents.Add(agent);
}

// 변경 후: RTSP URL만 있으면 시도
if (!string.IsNullOrWhiteSpace(agent.RtspUrl))
{
    rtspAgents.Add(agent);  // ← 무조건 추가
}
```

**장점:**
- ? Android 상태 불일치 우회
- ? 포트 체크 타이밍 이슈 우회
- ? LibVLC가 더 정확하게 판단
- ? 실제로 재생 가능하면 작동함

---

## 구현 상세

### 1. 핵심 로직 변경

**변경 전:**
```csharp
bool isStreaming = agent.StreamState == "streaming";
bool rtspPortOpen = await IsTcpOpenAsync(host, 8554, 700);

if (isStreaming || rtspPortOpen)  // ← 통과 조건
{
    rtspAgents.Add(agent);
}
else
{
    // 여기서 차단됨!
}
```

**변경 후:**
```csharp
if (!TryGetRtspEndpoint(agent.RtspUrl, out var rtspHost, out var rtspPort))
{
    warnings.Add($"{agent.Model} - RTSP URL 파싱 실패");
    continue;
}

bool isStreaming = agent.StreamState == "streaming";
bool rtspPortOpen = await IsTcpOpenAsync(rtspHost, rtspPort, 1200);

// RTSP URL이 있으면 무조건 추가
rtspAgents.Add(agent);  // ← 핵심 변경!

// 상태 불일치는 경고만
if (!isStreaming || !rtspPortOpen)
{
    warnings.Add($"{agent.Model} / {rtspHost}:{rtspPort}\n" +
                 $"streamState={agent.StreamState}, portOpen={rtspPortOpen}");
}
```

---

### 2. RTSP URL 파싱 개선

**새로운 TryGetRtspEndpoint() 메서드:**
```csharp
private static bool TryGetRtspEndpoint(string rtspUrl, out string host, out int port)
{
    host = null;
    port = 0;

    try
    {
        if (!Uri.TryCreate(rtspUrl, UriKind.Absolute, out var uri))
            return false;

        if (!string.Equals(uri.Scheme, "rtsp", StringComparison.OrdinalIgnoreCase))
            return false;

        host = uri.Host;
        port = uri.Port > 0 ? uri.Port : 8554;

        return !string.IsNullOrWhiteSpace(host) && port > 0;
    }
    catch
    {
        return false;
    }
}
```

**특징:**
- `Uri` 클래스 사용으로 정확한 파싱
- RTSP 프로토콜 검증
- 포트 없으면 기본값 8554
- 예외 처리

**사용 예:**
```csharp
// rtsp://192.168.0.243:8554/live
TryGetRtspEndpoint(url, out var host, out var port)
// host: "192.168.0.243"
// port: 8554

// rtsp://192.168.0.243/live (포트 없음)
TryGetRtspEndpoint(url, out var host, out var port)
// host: "192.168.0.243"
// port: 8554 (기본값)
```

---

### 3. TCP 포트 체크 타임아웃 증가

**변경:**
```csharp
// 700ms → 1200ms
private static async Task<bool> IsTcpOpenAsync(
    string host, 
    int port, 
    int timeoutMs = 1200)  // ← 증가
```

**이유:**
- Quest WiFi 불안정
- 네트워크 지연
- 더 정확한 판단

---

## 동작 시나리오

### 시나리오 1: streamState=stopped, 포트 닫힘 (현재 상황)

**변경 전:**
```
Agent 검색
    ↓
Agent 발견: 192.168.0.243
  - RtspUrl: rtsp://192.168.0.243:8554/live ?
  - StreamState: "stopped" ??
    ↓
TCP 포트 체크: 닫힘 ??
    ↓
? rtspAgents에 추가 안 함
    ↓
? "재생 가능한 RTSP 스트림을 찾지 못했습니다"
```

**변경 후:**
```
Agent 검색
    ↓
Agent 발견: 192.168.0.243
  - RtspUrl: rtsp://192.168.0.243:8554/live ?
    ↓
RTSP URL 파싱: 성공 ?
    ↓
? rtspAgents에 추가 (무조건!)
    ↓
StreamState 체크: stopped ??
포트 체크: 닫힘 ??
    ↓
warnings에 추가
    ↓
타일 생성 및 LibVLC 재생 시도 ?
    ↓
경고 메시지:
┌──────────────────────────────────────┐
│ RTSP 상태 경고                        │
│                                      │
│ RTSP URL이 있어 재생을 시도합니다.    │
│ 다만 Agent 상태와 실제 RTSP 포트      │
│ 상태가 불일치합니다.                  │
│                                      │
│ Quest 3 / 192.168.0.243:8554         │
│ streamState=stopped, portOpen=False  │
│                                      │
│            [확인]                    │
└──────────────────────────────────────┘
    ↓
LibVLC 재생 시도...
    ↓
Case A: 실제 RTSP 서버 살아있음
  → 재생 성공! ?
    ↓
Case B: 실제 RTSP 서버 죽음
  → LibVLC에서 "연결 중" 또는 "재생 오류" 표시 ??
```

**결과:** 
- **실제로 송출 중이면 재생 됨** ?
- 안 되면 LibVLC가 자연스럽게 오류 표시

---

### 시나리오 2: streamState=streaming, 포트 열림 (정상)

```
Agent 검색
    ↓
Agent 발견
    ↓
RTSP URL 파싱: 성공 ?
    ↓
rtspAgents에 추가 ?
    ↓
StreamState: streaming ?
포트: 열림 ?
    ↓
warnings 없음
    ↓
타일 생성 및 재생 시작 ?
    ↓
메시지:
┌──────────────────────────────────────┐
│ RTSP 미러링                           │
│                                      │
│ RTSP 미러링 타일 적용 완료: 1개       │
│                                      │
│            [확인]                    │
└──────────────────────────────────────┘
```

**결과:** 정상 재생 ?

---

### 시나리오 3: RTSP URL 없음

```
Agent 검색
    ↓
Agent 발견
    ↓
RtspUrl: null ??
    ↓
? rtspAgents에 추가 안 함 (올바른 동작)
    ↓
메시지:
┌──────────────────────────────────────┐
│ Agent는 찾았지만                      │
│ RTSP URL이 있는 기기가 없습니다.      │
│                                      │
│            [확인]                    │
└──────────────────────────────────────┘
```

**결과:** RTSP 기능 없는 Agent 필터링

---

## 핵심 변경점 요약

| 항목 | 변경 전 | 변경 후 |
|------|---------|---------|
| **필터링 조건** | `isStreaming OR portOpen` | `RTSP URL 있으면 무조건` |
| **차단 시점** | 재생 시도 전 | 없음 (LibVLC가 판단) |
| **경고 메시지** | "재생 가능한 스트림 없음" | "상태 불일치지만 시도함" |
| **포트 타임아웃** | 700ms | 1200ms |
| **URL 파싱** | 수동 문자열 처리 | `Uri` 클래스 사용 |

---

## 장점

### 1. 실용적 접근 ?
- Android 상태 동기화 버그 우회
- 실제 재생 가능하면 작동
- LibVLC가 더 정확하게 판단

### 2. 사용자 경험 개선 ?
- 과도한 사전 필터링 제거
- 실제 시도해보고 판단
- 명확한 경고 메시지

### 3. 디버깅 용이 ?
- 경고 메시지에 상태 상세 표시
- `streamState`와 `portOpen` 값 확인 가능
- 문제 원인 파악 쉬움

### 4. 유연성 ?
- 네트워크 타이밍 이슈 우회
- 포트 체크 실패해도 시도
- LibVLC의 재시도 메커니즘 활용

---

## 경고 메시지 예시

### 상태 불일치 경고
```
RTSP 상태 경고

RTSP URL이 있어 재생을 시도합니다.
다만 Agent 상태와 실제 RTSP 포트 상태가 불일치합니다.

Quest 3 / 192.168.0.243:8554
streamState=stopped, portOpen=False

Quest 2 / 192.168.0.244:8554
streamState=stopped, portOpen=True
```

**의미:**
- 첫 번째: Android 상태도 stopped, 포트도 닫힘 → 재생 실패 가능성 높음
- 두 번째: Android 상태는 stopped지만 포트 열림 → 재생 성공 가능성 있음

---

## 진단 명령어

### 1. RTSP 포트 확인
```powershell
Test-NetConnection 192.168.0.243 -Port 8554
```

**정상:**
```
TcpTestSucceeded : True
```

**비정상:**
```
TcpTestSucceeded : False
```

---

### 2. Agent 상태 확인
```bash
curl http://192.168.0.243:18080/status
```

**정상:**
```json
{
  "streamState": "streaming",
  "rtspUrl": "rtsp://192.168.0.243:8554/live"
}
```

**비정상:**
```json
{
  "streamState": "stopped",
  "rtspUrl": "rtsp://192.168.0.243:8554/live"
}
```

---

### 3. VLC 직접 테스트
```bash
"C:\Program Files\VideoLAN\VLC\vlc.exe" --rtsp-tcp rtsp://192.168.0.243:8554/live
```

**정상:** 화면 재생됨 ?  
**비정상:** 연결 오류 ??

---

### 4. Android 로그 확인
```bash
adb logcat -c
adb logcat -v time -s MQ-CAPTURE MQ-RTSP MQ-HTTP
```

**정상 로그:**
```
MQ-RTSP: RTSP server started on port=8554
MQ-CAPTURE: Capture state=streaming url=rtsp://192.168.0.243:8554/live
MQ-RTSP: H264 format ready. sps=... pps=...
MQ-CAPTURE: first encoded frame, state=streaming
```

---

## Android 수정 가이드

**WPF는 이제 작동하지만, Android 상태 동기화도 필수입니다.**

### 1. Manifest 확인
```xml
<!-- ? 이렇게 되어있으면 안 됨 -->
<service
    android:name=".capture.CaptureService"
    android:process=":capture" />

<!-- ? 이렇게 되어야 함 -->
<service
    android:name=".capture.CaptureService"
    android:foregroundServiceType="mediaProjection" />
```

---

### 2. CaptureService.startCapture()
```kotlin
virtualDisplay = mediaProjection.createVirtualDisplay(
    "MultiQuestCapture",
    width, height,
    resources.displayMetrics.densityDpi,
    DisplayManager.VIRTUAL_DISPLAY_FLAG_AUTO_MIRROR,
    inputSurface,
    null,
    Handler(Looper.getMainLooper())
)

// ? 상태 업데이트
CaptureStateStore.state = "streaming"
CaptureStateStore.rtspUrl = "rtsp://$ip:$RTSP_PORT/live"
Log.i("MQ-CAPTURE", "Capture state=streaming url=${CaptureStateStore.rtspUrl}")

scope.launch {
    drainEncoder(codec)
}
```

---

### 3. drainEncoder() 첫 프레임
```kotlin
private var encodedCount = 0

private fun drainEncoder(codec: MediaCodec) {
    val bufferInfo = MediaCodec.BufferInfo()

    try {
        while (scope.isActive) {
            val index = codec.dequeueOutputBuffer(bufferInfo, 10_000)

            when {
                index >= 0 -> {
                    val outBuffer = codec.getOutputBuffer(index)

                    if (outBuffer != null && bufferInfo.size > 0 &&
                        (bufferInfo.flags and MediaCodec.BUFFER_FLAG_CODEC_CONFIG) == 0
                    ) {
                        encodedCount++

                        // ? 첫 프레임에서 재보정
                        if (encodedCount == 1) {
                            CaptureStateStore.state = "streaming"
                            Log.i("MQ-CAPTURE", "first encoded frame, state=streaming")
                        }

                        // RTSP 전송...
                    }

                    codec.releaseOutputBuffer(index, false)
                }
            }
        }
    } catch (e: Throwable) {
        CaptureStateStore.state = "encoder_crashed"
        Log.e("MQ-CAPTURE", "drainEncoder crashed", e)
    }
}
```

---

## 테스트 시나리오

### 테스트 1: 현재 상황 (stopped + 포트 닫힘)
```
1. Quest Agent 앱 "화면 송출 시작" 클릭
2. UI: "화면 송출중" 표시
3. PC에서 Test-NetConnection → False (포트 닫힘)
4. PC에서 curl /status → streamState: "stopped"
5. WPF Agent 검색 클릭
6. 경고 메시지 표시 (상태 불일치)
7. 타일 생성됨
8. LibVLC 재생 시도
9. 결과:
   - 실제 RTSP 살아있으면 → 재생 성공 ?
   - 실제 RTSP 죽어있으면 → "연결 중" 또는 "재생 오류" 표시 ??
```

---

### 테스트 2: 정상 상태
```
1. Android 코드 수정 후 재빌드
2. Quest Agent 앱 "화면 송출 시작"
3. adb logcat 확인: state=streaming 로그 확인
4. PC에서 curl /status → streamState: "streaming"
5. PC에서 Test-NetConnection → True
6. WPF Agent 검색
7. 경고 없이 재생 시작 ?
```

---

## 트러블슈팅

### 경고 메시지 계속 뜸
**증상:** "RTSP 상태 경고" 반복

**원인:** Android 상태 동기화 안 됨

**해결:** Android 수정 가이드 참조

---

### LibVLC에서 "재생 오류"
**증상:** 타일은 생성되지만 재생 안 됨

**원인:** 실제 RTSP 서버 죽음

**확인:**
```bash
Test-NetConnection QuestIP -Port 8554
# TcpTestSucceeded : False
```

**해결:**
1. Quest Agent 앱 재시작
2. "화면 송출 시작" 버튼 다시 클릭
3. 화면 캡처 권한 허용

---

### 포트 체크 항상 실패
**증상:** `portOpen=False` 계속

**원인:** WiFi 불안정, 방화벽

**해결:**
1. Quest와 PC 같은 공유기 확인
2. WiFi 5GHz 사용
3. PC 방화벽 8554 포트 허용

---

## 비교표

| 항목 | 이전 버전 | 현재 버전 |
|------|----------|----------|
| 필터링 | 엄격 | 관대 |
| streamState 필수? | ? | ? |
| 포트 체크 필수? | ? | ? |
| RTSP URL만 필수? | ? | ? |
| 재생 시도 | 조건부 | 무조건 |
| 경고 표시 | 차단 | 시도 + 경고 |
| 포트 타임아웃 | 700ms | 1200ms |

---

## 요약

### 핵심 변경
1. ? `TryGetRtspEndpoint()` 추가 - 정확한 RTSP URL 파싱
2. ? 무조건 재생 시도 - RTSP URL만 있으면 타일 생성
3. ? 상태 불일치 경고 - 차단 대신 경고 메시지
4. ? 타임아웃 증가 - 700ms → 1200ms

### 결과
- **실용적 접근** ?
- **Android 버그 우회** ?
- **사용자 경험 개선** ?
- **디버깅 용이** ?

### 다음 단계
- Android `CaptureStateStore` 동기화 수정
- `/status` API 정확도 개선
- UI와 상태 일관성 확보

---

**? 빌드 성공!**
**? RTSP URL만 있으면 무조건 재생 시도!**
**? LibVLC가 실제 연결 판단!**
