# ADB 의존성 제거 - Agent API 전환 완료

## 개요
WPF 애플리케이션의 핵심 기능(배터리 조회, 앱 실행, 앱 종료)을 ADB에서 **Quest Agent HTTP API**로 전환했습니다.
- ? Agent API 우선 실행, ADB는 백업으로 유지
- ? 배터리 조회 Agent API 전환 완료
- ? 앱 실행 Agent API 전환 완료
- ? 앱 종료 Agent API 전환 완료 (협력 종료 방식)
- ? 빌드 성공 확인 완료

---

## 변경 사항

### 1. 새로운 파일: AgentApi.cs

Quest Agent와 HTTP 통신하는 전용 API 클라이언트를 생성했습니다.

#### 주요 메서드:

```csharp
// Agent 상태 조회 (배터리, 스트림 상태 등)
public static async Task<QuestAgentInfo> GetStatusAsync(string host, int port = 18080)

// 앱 실행 (ADB 대체)
public static async Task<bool> LaunchAppAsync(
    string host,
    int port,
    string packageName,
    string activityName = "com.unity3d.player.UnityPlayerActivity",
    Dictionary<string, int> extras = null)

// 앱 종료 요청 (협력 종료 방식)
public static async Task<bool> StopAppAsync(
    string host,
    int port,
    string packageName)

// RTSP 스트림 재시작
public static async Task<bool> RestartCaptureAsync(string host, int port = 18080)

// 홈 화면으로 이동
public static async Task<bool> GoHomeAsync(string host, int port = 18080)
```

---

### 2. MainWindow.xaml.cs 변경

#### 2.1 배터리 조회 (UpdateBatteryStatus)

**변경 전:**
```csharp
// ADB만 사용
level = GetBatteryLevel(device.Ip);  // adb -s {ip} shell dumpsys battery
```

**변경 후:**
```csharp
// 1순위: Agent API
var status = await AgentApi.GetStatusAsync(device.AgentHost, device.AgentStatusPort);
if (status != null && status.Battery >= 0)
    level = status.Battery;

// 2순위: ADB 백업
if (level < 0 && device.Status == "Connected")
    level = GetBatteryLevel(device.Ip);
```

**효과:**
- Agent가 있는 기기는 더 이상 ADB 배터리 조회를 하지 않음
- Agent 없는 기기만 ADB 사용
- `AgentOnly` 상태 기기도 배터리 조회 가능

---

#### 2.2 앱 실행 (StartApp)

**변경 전:**
```csharp
// ADB만 사용
RunCmd($"adb -s {device.Ip} shell am start -n {pkg}/{activity}", 2000);
```

**변경 후:**
```csharp
// 1순위: Agent API
bool launched = await AgentApi.LaunchAppAsync(
    device.AgentHost,
    device.AgentStatusPort,
    pkg,
    activity);

// 2순위: ADB 백업
if (!success && device.Status == "Connected")
{
    RunCmd($"adb -s {device.Ip} shell am start -n {pkg}/{activity}", 2000);
}
```

**효과:**
- Agent 기기는 HTTP 명령으로 앱 실행
- 백그라운드 Activity 실행 제한에 대응 가능
- 실행 성공 여부 반환으로 더 나은 오류 처리

---

#### 2.3 앱 종료 (StopDeviceApp, AllDeviceStopAppBtn_Click)

**변경 전:**
```csharp
// ADB force-stop만 사용
RunCmd($"adb -s {device.Ip} shell am force-stop {pkg}", 1000);
```

**변경 후:**
```csharp
// 1순위: Agent API (협력 종료)
await AgentApi.StopAppAsync(
    device.AgentHost,
    device.AgentStatusPort,
    pkg);

// 2순위: ADB force-stop 백업
if (!success && device.Status == "Connected")
{
    RunCmd($"adb -s {device.Ip} shell am force-stop {pkg}", 1000);
}
```

**효과:**
- Agent 기기는 협력 종료 방식 사용
- 앱이 정상적으로 종료 절차를 밟을 수 있음
- 강제 종료는 ADB 백업으로만 제공

---

## 동작 방식

### 우선순위 시스템

모든 제어 명령은 이제 다음 순서로 실행됩니다:

```
1순위: Agent API (device.AgentHost가 있는 경우)
   ↓ 실패 또는 Agent 없음
2순위: ADB (device.Status == "Connected")
   ↓ 실패
오류 메시지 표시
```

### Agent 기기 판별

```csharp
// Agent가 있는 기기
if (!string.IsNullOrWhiteSpace(device.AgentHost))
{
    // Agent API 사용
}

// ADB 연결 기기
if (device.Status == "Connected")
{
    // ADB 사용
}

// Agent-only 기기
if (device.IsAgentOnly)  // Status == "AgentOnly"
{
    // Agent API만 사용 가능
    // ADB 명령 실행하지 않음
}
```

---

## Android Agent 측 구현 필요사항

WPF가 이제 다음 엔드포인트를 호출하므로, Android Agent에 구현해야 합니다:

### 1. GET /status
이미 구현되어 있음. Battery 필드가 중요합니다.

```json
{
  "deviceId": "1WMHH1234567",
  "model": "Quest 3",
  "ip": "192.168.0.243",
  "battery": 85,
  "streamState": "streaming",
  "rtspUrl": "rtsp://192.168.0.243:8554/live",
  "rtspPort": 8554,
  "agentVersion": "1.0.0"
}
```

---

### 2. POST /command/launch ? 새로 필요

앱 실행 명령을 받아 Intent로 실행합니다.

**요청:**
```json
{
  "packageName": "com.StoryWing.Jurassic",
  "activityName": "com.unity3d.player.UnityPlayerActivity",
  "extras": {
    "stage": 3,
    "lesson": 1
  }
}
```

**Android 구현 예시:**
```kotlin
fun launchStoryWingApp(
    context: Context,
    packageName: String,
    activityName: String,
    extras: Map<String, Int>
): Boolean {
    return try {
        val intent = Intent().apply {
            setClassName(packageName, activityName)
            addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
            addFlags(Intent.FLAG_ACTIVITY_CLEAR_TOP)

            for ((key, value) in extras) {
                putExtra(key, value)
            }
        }

        context.startActivity(intent)
        true
    } catch (e: Exception) {
        Log.e("MQ-Agent", "launchApp failed: $packageName", e)
        false
    }
}
```

**주의:**
- Android 10+ background activity launch 제한 고려
- Agent를 foreground/visible 상태로 유지 권장
- 실행 실패 시 false 반환

---

### 3. POST /command/stop ? 새로 필요

앱에게 종료 요청 브로드캐스트를 보냅니다 (협력 종료).

**요청:**
```json
{
  "packageName": "com.StoryWing.Jurassic"
}
```

**Android Agent 구현 예시:**
```kotlin
fun requestStopApp(context: Context, packageName: String): Boolean {
    return try {
        val intent = Intent("com.storywing.multiquest.ACTION_STOP").apply {
            setPackage(packageName)
        }

        context.sendBroadcast(intent)
        true
    } catch (e: Exception) {
        Log.e("MQ-Agent", "requestStop failed: $packageName", e)
        false
    }
}
```

**중요:** 
이 방식은 **협력 종료**입니다. force-stop이 아닙니다.
StoryWing 앱들이 이 브로드캐스트를 받아야 동작합니다.

---

### 4. StoryWing 앱 측 구현 ? 필수

각 StoryWing Unity 앱에 종료 명령 수신 기능을 추가해야 합니다.

#### AndroidManifest.xml
```xml
<receiver android:name=".MultiQuestCommandReceiver"
          android:exported="false">
    <intent-filter>
        <action android:name="com.storywing.multiquest.ACTION_STOP" />
    </intent-filter>
</receiver>
```

#### Android Plugin (Kotlin/Java)
```kotlin
class MultiQuestCommandReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        when (intent.action) {
            "com.storywing.multiquest.ACTION_STOP" -> {
                UnityPlayer.UnitySendMessage(
                    "MultiQuestCommandReceiver",
                    "OnStopRequested",
                    ""
                )
            }
        }
    }
}
```

#### Unity C# Script
```csharp
public class MultiQuestCommandReceiver : MonoBehaviour
{
    public void OnStopRequested(string payload)
    {
        // 옵션 1: 앱 종료
        Application.Quit();

        // 옵션 2: 메인 메뉴로 이동
        // SceneManager.LoadScene("MainMenu");

        // 옵션 3: 홈 화면 호출
        // AndroidJavaClass unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
        // AndroidJavaObject currentActivity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
        // AndroidJavaObject intent = new AndroidJavaObject("android.content.Intent");
        // intent.Call<AndroidJavaObject>("setAction", "android.intent.action.MAIN");
        // intent.Call<AndroidJavaObject>("addCategory", "android.intent.category.HOME");
        // currentActivity.Call("startActivity", intent);
    }
}
```

---

### 5. POST /command/restartCapture (선택)

RTSP 스트림 재시작 명령입니다.

**요청:** 본문 없음

**Android 구현:**
```kotlin
fun restartCapture() {
    stopMediaProjection()
    Thread.sleep(300)
    startMediaProjection()
}
```

---

### 6. POST /command/home (선택)

홈 화면으로 이동합니다.

**요청:** 본문 없음

**Android 구현:**
```kotlin
fun goHome(context: Context): Boolean {
    return try {
        val intent = Intent(Intent.ACTION_MAIN).apply {
            addCategory(Intent.CATEGORY_HOME)
            flags = Intent.FLAG_ACTIVITY_NEW_TASK
        }
        context.startActivity(intent)
        true
    } catch (e: Exception) {
        Log.e("MQ-Agent", "goHome failed", e)
        false
    }
}
```

---

## AndroidManifest.xml 추가 필요

Agent가 StoryWing 앱들을 찾으려면 package visibility 선언이 필요합니다 (Android 11+):

```xml
<manifest ...>
    <queries>
        <package android:name="com.StoryWing.EnglishTown" />
        <package android:name="com.StoryWing.Ocean_Adventure" />
        <package android:name="com.StoryWing.Korea_History" />
        <package android:name="com.StoryWing.Solar_System" />
        <package android:name="com.StoryWing.Fire_Safety" />
        <package android:name="com.StoryWing.Jurassic" />
        <package android:name="com.StoryWing.BrainPop" />
        <package android:name="com.StoryWing.SmartFarm" />
        <package android:name="com.StoryWing.Museum" />
        <package android:name="com.StoryWing.World_Travel" />
        <package android:name="com.StoryWing.XR_Coding" />
        <package android:name="com.StoryWing.Storywing_Class" />
    </queries>

    <application ...>
        ...
    </application>
</manifest>
```

---

## 테스트 시나리오

### 1. 배터리 조회 테스트

**시나리오 A: Agent 있는 기기**
1. Agent 검색으로 기기 추가
2. 배터리 표시 확인
3. Wireshark로 `GET /status` 호출 확인
4. ADB 명령이 **실행되지 않음** 확인

**시나리오 B: ADB만 있는 기기**
1. USB로 Quest 연결
2. Agent 없이 ADB 연결만 있는 상태
3. 배터리 표시 확인 (ADB 사용)

---

### 2. 앱 실행 테스트

**시나리오 A: Agent 기기**
1. Agent 검색으로 기기 추가
2. "영어마을" 버튼 클릭
3. Wireshark로 `POST /command/launch` 확인
4. Unity 앱 실행 확인
5. ADB 명령이 **실행되지 않음** 확인

**시나리오 B: ADB 기기**
1. USB 연결만 있는 기기
2. "영어마을" 버튼 클릭
3. ADB로 앱 실행 확인

---

### 3. 앱 종료 테스트

**시나리오 A: Agent + 협력 종료**
1. Agent 기기에서 앱 실행
2. "전체 종료" 버튼 클릭
3. Wireshark로 `POST /command/stop` 확인
4. Unity 앱이 `Application.Quit()` 호출 확인
5. 정상 종료 (force-kill 아님)

**시나리오 B: ADB force-stop**
1. ADB만 있는 기기
2. "전체 종료" 버튼 클릭
3. `am force-stop` 명령 실행 확인
4. 앱 즉시 종료

---

## ADB 의존성 현황

### ? Agent API로 대체 완료
- 배터리 조회 (`dumpsys battery`)
- 앱 실행 (`am start`)
- 앱 종료 (`am force-stop` → 협력 종료)
- 미러링 (scrcpy → RTSP)
- 기기 검색 (adb devices → mDNS + 18080 scan)

### ?? ADB 백업으로 유지
- Agent 없는 기기 제어
- Agent 실패 시 fallback
- 복구/관리자 모드

### ? Agent API로 대체 어려움 (ADB 계속 필요)
- 임의 shell 명령 (`input keyevent`)
- 다른 앱 강제 종료 (시스템 권한 필요)
- Guardian/Boundary 데이터 초기화
- 설정 앱 내부 제어
- 무선 디버깅 자동 페어링
- 기기 재부팅

---

## 다음 단계

### 1. Android Agent HTTP 서버 업데이트 ? 최우선
```kotlin
// SimpleStatusHttpServer.kt에 추가
server.createContext("/command/launch") { exchange ->
    // JSON 파싱 → launchStoryWingApp() 호출
}

server.createContext("/command/stop") { exchange ->
    // JSON 파싱 → requestStopApp() 호출
}
```

### 2. StoryWing 앱들에 종료 수신기 추가 ? 필수
모든 Unity 앱에 `MultiQuestCommandReceiver` 추가

### 3. AndroidManifest.xml에 `<queries>` 추가
Agent가 다른 앱을 찾을 수 있도록

### 4. 운영 테스트
- Agent 검색 → 배터리 → 앱 실행 → 앱 종료 전체 흐름
- Agent 없는 기기 ADB fallback 동작 확인

### 5. ADB 타이머 비활성화 (선택)
Agent만으로 운영이 안정되면:
```csharp
// MainWindow.xaml.cs
private const bool EnableAdbDiscovery = false;  // ADB 검색 비활성화
```

---

## 장점

### 1. 무선 운영 가능
- USB 케이블 불필요
- 여러 Quest를 같은 WiFi에서 관리
- 이동식 설치 환경 지원

### 2. 더 나은 앱 제어
- 협력 종료로 정상 종료 절차 실행 가능
- Intent extras로 stage/lesson 전달
- 앱 상태 조회 가능

### 3. 낮은 오버헤드
- ADB 프로세스 생성 오버헤드 없음
- HTTP 직접 호출로 빠른 응답
- 병렬 처리 효율 향상

### 4. 확장 가능
- 새로운 명령 추가 쉬움
- 로그 조회, 스크린샷 등 확장 가능
- 웹 대시보드 통합 가능

---

## 제약사항

### 1. Android Agent 필수
Agent 앱이 설치되고 실행 중이어야 함

### 2. 협력 종료 필요
StoryWing 앱들이 종료 명령을 받아야 함
(구현 전까지는 ADB force-stop만 동작)

### 3. Background Activity Launch 제한
Android 10+ 환경에서는 백그라운드 앱 실행 제한
→ Agent를 foreground 상태로 유지 권장

### 4. Package Visibility
Android 11+ 환경에서는 `<queries>` 선언 필요

---

## 트러블슈팅

### Agent API 호출 실패
```
증상: 배터리/앱 실행/종료가 동작하지 않음
원인: Agent HTTP 서버가 응답하지 않음

해결:
1. curl http://QuestIP:18080/status 테스트
2. Agent 앱이 실행 중인지 확인
3. ForegroundService 유지 확인
4. 배터리 최적화 예외 설정
```

### 앱 실행은 되지만 종료 안 됨
```
증상: Agent API로 앱 실행은 되지만 종료는 안 됨
원인: StoryWing 앱에 BroadcastReceiver 미구현

해결:
1. StoryWing 앱에 MultiQuestCommandReceiver 추가
2. AndroidManifest.xml에 receiver 등록
3. Unity C# 스크립트 추가
4. 빌드 후 재배포
```

### ADB Fallback 실행 안 됨
```
증상: Agent도 없고 ADB도 연결 안 됨
원인: device.Status != "Connected"

해결:
1. USB 연결 확인
2. adb devices 명령으로 기기 확인
3. 무선 디버깅 활성화 확인
```

---

## 참고 자료

### Android 공식 문서
- [Background Activity Launch Restrictions](https://developer.android.com/guide/components/activities/background-starts)
- [Package Visibility](https://developer.android.com/training/package-visibility)
- [ActivityManager.killBackgroundProcesses](https://developer.android.com/reference/android/app/ActivityManager#killBackgroundProcesses(java.lang.String))

### 프로젝트 파일
- `AgentApi.cs`: HTTP API 클라이언트
- `MainWindow.xaml.cs`: 배터리/앱 제어 로직
- `QuestAgentInfo.cs`: Agent 정보 모델
- `AgentMdnsDiscovery.cs`: Agent 검색

---

**? ADB 의존성 제거 1단계 완료!**

다음: Android Agent HTTP 서버에 `/command/launch`, `/command/stop` 구현
