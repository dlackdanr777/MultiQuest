# HTTP API로 ADB 정보 대체 완료

## 개요
ADB로 받던 배터리, 충전 상태, 시리얼 넘버 등을 **HTTP API**로 받도록 변경했습니다.

---

## WPF 변경 사항

### 1. QuestAgentInfo 클래스 확장

**추가된 필드:**
```csharp
public sealed class QuestAgentInfo
{
    // 기존 필드
    public string Host { get; set; }
    public int Battery { get; set; }
    public string StreamState { get; set; }

    // ? 새로 추가된 필드
    public bool IsCharging { get; set; }
    public string ChargingStatus { get; set; }  // "Charging", "Not charging", "Full"
    public int? Temperature { get; set; }       // 배터리 온도 (0.1도 단위)
    public string Serial { get; set; }          // 시리얼 넘버
    public string AndroidVersion { get; set; }  // Android 버전
    public string BuildNumber { get; set; }     // 빌드 넘버
}
```

---

### 2. Device 클래스 확장

**추가된 필드:**
```csharp
public class Device : INotifyPropertyChanged
{
    // 기존 필드
    public int BatteryLevel { get; set; }

    // ? 새로 추가된 필드
    public bool IsCharging { get; set; }
    public string ChargingStatus { get; set; }

    // ? BatteryText 업데이트 (충전 표시 추가)
    public string BatteryText
    {
        get
        {
            if (BatteryLevel < 0) return "N/A";
            string text = $"{BatteryLevel}%";
            if (IsCharging) text += " ?";  // 충전 중 표시
            return text;
        }
    }
}
```

---

### 3. UpdateBatteryStatus 메서드 개선

**변경 전:**
```csharp
// Agent API에서 배터리만 가져옴
var status = await AgentApi.GetStatusAsync(device.AgentHost, device.AgentStatusPort);
if (status != null && status.Battery >= 0)
    level = status.Battery;
```

**변경 후:**
```csharp
// Agent API에서 배터리 + 충전 상태 + 시리얼 가져옴
var status = await AgentApi.GetStatusAsync(device.AgentHost, device.AgentStatusPort);
if (status != null)
{
    if (status.Battery >= 0)
        level = status.Battery;
    isCharging = status.IsCharging;
    chargingStatus = status.ChargingStatus;
    serial = status.Serial;
}

// UI 업데이트
d.BatteryLevel = level;
d.IsCharging = isCharging;
d.ChargingStatus = chargingStatus;
if (!string.IsNullOrEmpty(serial)) d.Serial = serial;
```

---

### 4. ApplyRtspAgentsToDeviceTiles 업데이트

**추가 정보 반영:**
```csharp
device.BatteryLevel = agent.Battery;
device.IsCharging = agent.IsCharging;
device.ChargingStatus = agent.ChargingStatus;

// 시리얼이 없으면 Agent에서 가져오기
if (string.IsNullOrEmpty(device.Serial) && !string.IsNullOrEmpty(agent.Serial))
{
    device.Serial = agent.Serial;
}
```

---

## Android Agent 수정 가이드

### 1. /status API 응답 확장

**기존 응답:**
```json
{
  "streamState": "streaming",
  "rtspUrl": "rtsp://192.168.0.243:8554/live",
  "battery": 87,
  "model": "Quest 3",
  "version": "0.2.0"
}
```

**새로운 응답:**
```json
{
  "streamState": "streaming",
  "rtspUrl": "rtsp://192.168.0.243:8554/live",
  "battery": 87,
  "isCharging": true,
  "chargingStatus": "Charging",
  "temperature": 280,
  "serial": "1WMHH81234567890",
  "androidVersion": "12",
  "buildNumber": "68.0.0.68.268",
  "model": "Quest 3",
  "version": "0.2.0"
}
```

---

### 2. 배터리 정보 수집

**Kotlin 코드:**
```kotlin
// AgentStatus.kt 또는 해당 파일에서

fun getBatteryInfo(context: Context): BatteryInfo {
    val batteryIntent = context.registerReceiver(
        null,
        IntentFilter(Intent.ACTION_BATTERY_CHANGED)
    )

    val level = batteryIntent?.getIntExtra(BatteryManager.EXTRA_LEVEL, -1) ?: -1
    val scale = batteryIntent?.getIntExtra(BatteryManager.EXTRA_SCALE, -1) ?: -1
    val battery = if (level >= 0 && scale > 0) (level * 100 / scale) else -1

    val status = batteryIntent?.getIntExtra(BatteryManager.EXTRA_STATUS, -1) ?: -1
    val isCharging = status == BatteryManager.BATTERY_STATUS_CHARGING ||
                     status == BatteryManager.BATTERY_STATUS_FULL

    val chargingStatus = when (status) {
        BatteryManager.BATTERY_STATUS_CHARGING -> "Charging"
        BatteryManager.BATTERY_STATUS_FULL -> "Full"
        BatteryManager.BATTERY_STATUS_DISCHARGING -> "Discharging"
        BatteryManager.BATTERY_STATUS_NOT_CHARGING -> "Not charging"
        else -> "Unknown"
    }

    val temperature = batteryIntent?.getIntExtra(BatteryManager.EXTRA_TEMPERATURE, -1) ?: -1

    return BatteryInfo(
        level = battery,
        isCharging = isCharging,
        status = chargingStatus,
        temperature = temperature
    )
}

data class BatteryInfo(
    val level: Int,
    val isCharging: Boolean,
    val status: String,
    val temperature: Int
)
```

---

### 3. 시리얼 넘버 가져오기

**Kotlin 코드:**
```kotlin
import android.os.Build

fun getSerial(): String {
    return try {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            Build.getSerial()
        } else {
            @Suppress("DEPRECATION")
            Build.SERIAL
        }
    } catch (e: SecurityException) {
        "Unknown"
    }
}
```

**AndroidManifest.xml 권한 추가:**
```xml
<uses-permission android:name="android.permission.READ_PHONE_STATE" />
```

---

### 4. Android 버전 정보

**Kotlin 코드:**
```kotlin
fun getAndroidVersion(): String {
    return Build.VERSION.RELEASE  // 예: "12"
}

fun getBuildNumber(): String {
    return Build.DISPLAY  // 예: "68.0.0.68.268"
}
```

---

### 5. /status API 구현 예시

**AgentStatus.kt:**
```kotlin
object AgentStatus {
    fun statusJson(context: Context): JSONObject {
        val battery = getBatteryInfo(context)

        return JSONObject().apply {
            put("streamState", CaptureStateStore.state)
            put("rtspUrl", CaptureStateStore.rtspUrl)
            put("battery", battery.level)
            put("isCharging", battery.isCharging)
            put("chargingStatus", battery.status)
            put("temperature", battery.temperature)
            put("serial", getSerial())
            put("androidVersion", getAndroidVersion())
            put("buildNumber", getBuildNumber())
            put("model", Build.MODEL)
            put("version", BuildConfig.VERSION_NAME)
        }
    }
}
```

---

### 6. HTTP 서버 응답

**AgentHttpServer.kt:**
```kotlin
when (request.path) {
    "/status" -> {
        val json = AgentStatus.statusJson(context)
        response.setContentType("application/json")
        response.write(json.toString())
        response.send()
    }
}
```

---

## 동작 흐름

### 1. 배터리 체크 타이머 (10초마다)

```
Timer 발동 (10초)
    ↓
Devices 순회
    ↓
각 Device마다:
  Agent 있음? → Agent API 호출
    ↓
  GET http://QuestIP:18080/status
    ↓
  응답:
  {
    "battery": 87,
    "isCharging": true,
    "chargingStatus": "Charging",
    "serial": "1WMHH81234567890"
  }
    ↓
  Device 업데이트:
    - BatteryLevel = 87
    - IsCharging = true
    - ChargingStatus = "Charging"
    - Serial = "1WMHH81234567890"
    ↓
  UI 업데이트:
    - BatteryText = "87% ?"
```

---

### 2. Agent 검색 시

```
Agent 검색 버튼 클릭
    ↓
Agent 발견
    ↓
/status 응답에서 추가 정보 추출:
  - IsCharging
  - ChargingStatus
  - Serial
    ↓
Device 생성 또는 업데이트
    ↓
타일 표시:
┌──────────────────────────┐
│ Quest 3                   │
│ 192.168.0.243             │
│ 배터리: 87% ?            │  ← 충전 표시
│ 시리얼: 1WMHH8123456     │  ← 시리얼
└──────────────────────────┘
```

---

## UI 표시 예시

### 배터리 표시 변화

**변경 전:**
```
87%
```

**변경 후 (충전 중):**
```
87% ?
```

**변경 후 (완충):**
```
100% ?
```

**변경 후 (방전 중):**
```
87%
```

---

## 장점

### 1. ADB 의존성 제거 ?
- ADB 연결 불필요
- WiFi만으로 모든 정보 수집
- 더 안정적인 통신

### 2. 추가 정보 제공 ?
- 충전 상태 실시간 확인
- 시리얼 넘버 자동 수집
- 배터리 온도 모니터링

### 3. 성능 향상 ?
- HTTP 요청이 ADB보다 빠름
- 병렬 처리 용이
- 네트워크 부하 적음

### 4. 확장성 ?
- 향후 추가 정보 쉽게 확장
- JSON 구조로 유연한 대응

---

## ADB 백업 유지

**현재 구현:**
```csharp
// 1순위: Agent API
if (!string.IsNullOrWhiteSpace(device.AgentHost))
{
    var status = await AgentApi.GetStatusAsync(...);
    // Agent에서 정보 가져오기
}

// 2순위: ADB 백업 (Agent 없거나 실패 시)
if (level < 0 && device.Status == "Connected")
{
    level = GetBatteryLevel(device.Ip);  // ADB 사용
}
```

**이유:**
- Agent가 없는 구형 기기 지원
- Agent API 실패 시 대비
- 점진적 전환 가능

---

## 테스트 시나리오

### 테스트 1: 충전 중 표시
```
1. Quest를 충전기에 연결
2. Agent 앱 실행
3. WPF에서 Agent 검색
4. 배터리 표시 확인: "87% ?"
```

### 테스트 2: 충전 완료 표시
```
1. Quest 완충 (100%)
2. 배터리 표시 확인: "100% ?"
3. 충전기 제거
4. 10초 후 배터리 표시 확인: "100%"
```

### 테스트 3: 시리얼 넘버 수집
```
1. Agent 검색
2. Device 타일에 시리얼 표시 확인
3. Settings에서 이름 설정
4. 시리얼 대신 이름 표시 확인
```

### 테스트 4: ADB 백업 동작
```
1. Agent 없는 기기 ADB 연결
2. 배터리 체크 타이머 대기
3. ADB로 배터리 정보 가져오는지 확인
4. 배터리 표시: "87%" (충전 표시 없음)
```

---

## API 응답 예시

### 정상 응답
```json
{
  "streamState": "streaming",
  "rtspUrl": "rtsp://192.168.0.243:8554/live",
  "battery": 87,
  "isCharging": true,
  "chargingStatus": "Charging",
  "temperature": 280,
  "serial": "1WMHH81234567890",
  "androidVersion": "12",
  "buildNumber": "68.0.0.68.268",
  "model": "Quest 3",
  "version": "0.2.0"
}
```

### 최소 응답 (하위 호환성)
```json
{
  "streamState": "stopped",
  "rtspUrl": "",
  "battery": 87,
  "model": "Quest 3",
  "version": "0.2.0"
}
```

**WPF 처리:**
- `isCharging` 없으면 `false` 기본값
- `serial` 없으면 기존 값 유지
- 하위 호환성 보장

---

## 트러블슈팅

### 충전 표시가 안 나타남
**원인:** Android에서 `isCharging` 필드 안 보냄

**확인:**
```bash
curl http://QuestIP:18080/status | jq .isCharging
```

**해결:** Android 코드에 `isCharging` 추가

---

### 시리얼이 "Unknown"
**원인:** `READ_PHONE_STATE` 권한 없음

**해결:**
```xml
<uses-permission android:name="android.permission.READ_PHONE_STATE" />
```

앱 재설치 후 권한 허용

---

### 배터리 정보 안 업데이트
**원인:** 배터리 체크 타이머 중지

**확인:**
```csharp
// MainWindow.xaml.cs
_batteryCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
_batteryCheckTimer.Tick += UpdateBatteryStatus;
_batteryCheckTimer.Start();
```

**해결:** 타이머 재시작

---

## 요약

### WPF 변경
1. ? `QuestAgentInfo` 확장 (충전 상태, 시리얼 등)
2. ? `Device` 클래스 확장 (충전 표시)
3. ? `UpdateBatteryStatus` 개선 (추가 정보 반영)
4. ? `BatteryText` 업데이트 (? 표시)

### Android 수정 필요
1. ? `/status` API 응답 확장
2. ? 배터리 정보 수집 (`isCharging`, `chargingStatus`)
3. ? 시리얼 넘버 수집 (`getSerial()`)
4. ? 권한 추가 (`READ_PHONE_STATE`)

### 결과
- **ADB 의존성 감소** ?
- **추가 정보 제공** ?
- **성능 향상** ?
- **확장성 확보** ?

---

**? 빌드 성공!**
**? HTTP API로 ADB 정보 대체 완료!**
**? 충전 상태 실시간 표시!**
