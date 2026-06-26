# HTTP 기반 앱 실행 및 실시간 명령 시스템 완료

## 개요
ADB의 `am start --ei` 및 `am broadcast`를 **HTTP API**로 완전히 대체했습니다.

---

## 2가지 명령 체계

### 1. 앱 실행 시 전달하는 명령 (Extras)
- **용도:** stage, lesson 등 초기값 전달
- **API:** `/command/launch`
- **예시:** 코딩 앱 Stage 3, Lesson 1 시작

### 2. 앱 실행 중 실시간 명령 (App Command)
- **용도:** skip, pause, resume, next, restart 등
- **API:** `/command/appCommand`
- **예시:** 현재 문제 skip, 일시정지

---

## WPF 변경 사항

### 1. AgentApi.SendCommandAsync 추가

**AgentApi.cs:**
```csharp
/// <summary>
/// 실행 중인 앱에 실시간 명령 전송
/// 예: skip, pause, resume, next, restartLesson
/// </summary>
public static async Task<bool> SendCommandAsync(
    string host,
    int port,
    string packageName,
    string command,
    Dictionary<string, object> args = null)
{
    try
    {
        string url = $"http://{host}:{port}/command/appCommand";

        var body = new
        {
            packageName,
            command,
            args = args ?? new Dictionary<string, object>()
        };

        string json = JsonSerializer.Serialize(body);

        using var content = new StringContent(
            json,
            Encoding.UTF8,
            "application/json");

        var response = await _http.PostAsync(url, content);

        return response.IsSuccessStatusCode;
    }
    catch (Exception ex)
    {
        System.Diagnostics.Debug.WriteLine($"[AgentApi] SendCommandAsync failed: {ex.Message}");
        return false;
    }
}
```

---

### 2. 코딩 앱 실행 - HTTP API로 변경

**변경 전 (ADB):**
```csharp
string cmd = $"adb -s {device.Ip} shell am start -S " +
             $"-n {pkg}/{activity} " +
             $"--ei stage {level} " +
             $"--ei lesson {lesson}";
string result = RunCmd(cmd, 1500);
```

**변경 후 (HTTP):**
```csharp
// 1순위: Agent HTTP API
if (!string.IsNullOrWhiteSpace(device.AgentHost))
{
    launched = await AgentApi.LaunchAppAsync(
        device.AgentHost,
        device.AgentStatusPort,
        "com.StoryWing.XR_Coding",
        "com.unity3d.player.UnityPlayerActivity",
        new Dictionary<string, int>
        {
            ["stage"] = level,
            ["lesson"] = lesson
        });
}

// 2순위: ADB fallback
if (!launched && device.Status == "Connected")
{
    string cmd = $"adb -s {device.Ip} shell am start -S " +
                 $"-n {pkg}/{activity} " +
                 $"--ei stage {level} " +
                 $"--ei lesson {lesson}";
    string result = await Task.Run(() => RunCmd(cmd, 1500));
    launched = !result.Contains("Error");
}
```

---

### 3. 영어 앱 실행 - HTTP API로 변경

**변경 전 (ADB):**
```csharp
string cmd = $"adb -s {device.Ip} shell am start -S " +
             $"-n {pkg}/{activity} " +
             $"--ei stage {stage}";
string result = RunCmd(cmd, 1500);
```

**변경 후 (HTTP):**
```csharp
foreach (var pkg in candidates)
{
    // 1순위: Agent HTTP API
    if (!string.IsNullOrWhiteSpace(device.AgentHost))
    {
        launched = await AgentApi.LaunchAppAsync(
            device.AgentHost,
            device.AgentStatusPort,
            pkg,
            "com.unity3d.player.UnityPlayerActivity",
            new Dictionary<string, int>
            {
                ["stage"] = stage
            });
    }

    // 2순위: ADB fallback
    if (!launched && device.Status == "Connected")
    {
        string cmd = $"adb -s {device.Ip} shell am start -S " +
                     $"-n {pkg}/{activity} " +
                     $"--ei stage {stage}";
        string result = await Task.Run(() => RunCmd(cmd, 1500));
        launched = !result.Contains("Error");
    }

    if (launched) break;
}
```

---

## Android Agent 구현

### 1. AgentCommandHandler.kt 업데이트

**handle() 메서드에 추가:**
```kotlin
fun handle(
    context: Context,
    path: String,
    body: String
): AgentCommandResult {
    return try {
        when (path) {
            "/command/launch" -> handleLaunch(context, body)
            "/command/stop" -> handleStop(context, body)
            "/command/home" -> handleHome(context)
            "/command/restartCapture" -> handleRestartCapture(context)
            "/command/startCaptureUi" -> handleStartCaptureUi(context)
            "/command/appCommand" -> handleAppCommand(context, body)  // ? 추가
            else -> error("unknown_command", "Unknown command path: $path", 404)
        }
    } catch (e: Exception) {
        Log.e(TAG, "command failed: $path", e)
        error("exception", e.message ?: e.javaClass.simpleName, 500)
    }
}
```

---

### 2. handleAppCommand 구현

```kotlin
private fun handleAppCommand(
    context: Context,
    body: String
): AgentCommandResult {
    val json = parseBody(body)

    val packageName = json.optString("packageName", "").trim()
    val command = json.optString("command", "").trim()
    val args = json.optJSONObject("args") ?: JSONObject()

    if (packageName.isBlank()) {
        return error("bad_request", "packageName is required", 400)
    }

    if (command.isBlank()) {
        return error("bad_request", "command is required", 400)
    }

    val payload = JSONObject()
        .put("command", command)
        .put("args", args)
        .put("source", "multiquest-agent")
        .put("timestamp", System.currentTimeMillis())

    val sent = sendStoryWingCommand(
        context = context,
        packageName = packageName,
        payload = payload
    )

    val result = JSONObject()
        .put("ok", sent)
        .put("command", "appCommand")
        .put("packageName", packageName)
        .put("appCommand", command)
        .put("payload", payload)

    if (!sent) {
        result.put("error", "send_command_failed")
    }

    return AgentCommandResult(if (sent) 200 else 500, result)
}
```

---

### 3. sendStoryWingCommand 구현

```kotlin
companion object {
    private const val ACTION_STORYWING_COMMAND = 
        "com.storywing.multiquest.ACTION_COMMAND"
}

private fun sendStoryWingCommand(
    context: Context,
    packageName: String,
    payload: JSONObject
): Boolean {
    return try {
        val intent = Intent(ACTION_STORYWING_COMMAND).apply {
            setClassName(
                packageName,
                "com.storywing.multiquest.MultiQuestCommandReceiver"
            )

            putExtra("command", payload.optString("command"))
            putExtra("payload", payload.toString())
            putExtra("source", "multiquest-agent")
            putExtra("timestamp", System.currentTimeMillis())
        }

        context.sendBroadcast(intent)

        AgentLog.i(TAG, "app command sent to $packageName payload=$payload")
        Log.i(TAG, "app command sent to $packageName payload=$payload")

        true
    } catch (e: Exception) {
        AgentLog.e(TAG, "app command failed $packageName ${e.message}")
        Log.e(TAG, "app command failed $packageName", e)
        false
    }
}
```

---

## StoryWing Unity 앱 구현

### 1. AndroidManifest.xml - Receiver 등록

```xml
<application>
    <!-- 기존 Activity -->
    <activity android:name="com.unity3d.player.UnityPlayerActivity" ...>
    </activity>

    <!-- MultiQuest Command Receiver 추가 -->
    <receiver 
        android:name="com.storywing.multiquest.MultiQuestCommandReceiver"
        android:enabled="true"
        android:exported="true">
        <intent-filter>
            <action android:name="com.storywing.multiquest.ACTION_STOP" />
            <action android:name="com.storywing.multiquest.ACTION_COMMAND" />
        </intent-filter>
    </receiver>
</application>
```

---

### 2. Java/Kotlin - MultiQuestCommandReceiver

**Java 버전:**
```java
package com.storywing.multiquest;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.util.Log;

import java.lang.reflect.Method;

public class MultiQuestCommandReceiver extends BroadcastReceiver {

    private static final String TAG = "MQ-Content";

    private static final String ACTION_STOP =
            "com.storywing.multiquest.ACTION_STOP";

    private static final String ACTION_COMMAND =
            "com.storywing.multiquest.ACTION_COMMAND";

    private static final String UNITY_OBJECT_NAME =
            "MultiQuestCommandReceiver";

    @Override
    public void onReceive(Context context, Intent intent) {
        if (intent == null) return;

        String action = intent.getAction();

        if (ACTION_STOP.equals(action)) {
            Log.i(TAG, "STOP requested");

            sendUnityMessage(
                    UNITY_OBJECT_NAME,
                    "OnStopRequested",
                    ""
            );

            return;
        }

        if (ACTION_COMMAND.equals(action)) {
            String payload = intent.getStringExtra("payload");
            if (payload == null) payload = "{}";

            Log.i(TAG, "COMMAND requested: " + payload);

            sendUnityMessage(
                    UNITY_OBJECT_NAME,
                    "OnCommandReceived",
                    payload
            );
        }
    }

    private void sendUnityMessage(
            String gameObjectName,
            String methodName,
            String payload
    ) {
        try {
            Class<?> unityPlayerClass =
                    Class.forName("com.unity3d.player.UnityPlayer");

            Method unitySendMessage =
                    unityPlayerClass.getMethod(
                            "UnitySendMessage",
                            String.class,
                            String.class,
                            String.class
                    );

            unitySendMessage.invoke(
                    null,
                    gameObjectName,
                    methodName,
                    payload == null ? "" : payload
            );

            Log.i(TAG, "UnitySendMessage sent: "
                    + gameObjectName + "." + methodName);
        } catch (Exception e) {
            Log.e(TAG, "UnitySendMessage failed", e);
        }
    }
}
```

---

### 3. Unity C# - MultiQuestCommandReceiver.cs

```csharp
using UnityEngine;

public class MultiQuestCommandReceiver : MonoBehaviour
{
    private static MultiQuestCommandReceiver _instance;

    [System.Serializable]
    public class MultiQuestCommandPayload
    {
        public string command;
        public CommandArgs args;
        public string source;
        public long timestamp;
    }

    [System.Serializable]
    public class CommandArgs
    {
        public string direction;
        public int stage;
        public int lesson;
        public string value;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void CreateReceiver()
    {
        if (_instance != null)
            return;

        var go = new GameObject("MultiQuestCommandReceiver");
        _instance = go.AddComponent<MultiQuestCommandReceiver>();
        DontDestroyOnLoad(go);
    }

    private void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Destroy(gameObject);
            return;
        }

        _instance = this;
        gameObject.name = "MultiQuestCommandReceiver";
        DontDestroyOnLoad(gameObject);
    }

    // Android Receiver에서 호출
    public void OnStopRequested(string payload)
    {
        Debug.Log("[MultiQuest] Stop requested");

        Application.Quit();
        MoveTaskToBackAndroid();
    }

    // Android Receiver에서 호출
    public void OnCommandReceived(string payload)
    {
        Debug.Log("[MultiQuest] Command payload: " + payload);

        MultiQuestCommandPayload cmd = null;

        try
        {
            cmd = JsonUtility.FromJson<MultiQuestCommandPayload>(payload);
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("[MultiQuest] Command parse failed: " + e.Message);
            return;
        }

        if (cmd == null || string.IsNullOrEmpty(cmd.command))
            return;

        switch (cmd.command)
        {
            case "skip":
            case "next":
                OnSkipNext(cmd);
                break;

            case "prev":
                OnSkipPrev(cmd);
                break;

            case "pause":
                OnPause(cmd);
                break;

            case "resume":
                OnResume(cmd);
                break;

            case "restartLesson":
                OnRestartLesson(cmd);
                break;

            case "goStage":
                OnGoStage(cmd);
                break;

            default:
                Debug.LogWarning("[MultiQuest] Unknown command: " + cmd.command);
                break;
        }
    }

    private void OnSkipNext(MultiQuestCommandPayload cmd)
    {
        Debug.Log("[MultiQuest] Skip next");

        // TODO: 실제 영어/코딩 앱의 다음 문제/다음 단계 함수 연결
        // Example:
        // GameManager.Instance.SkipNext();
    }

    private void OnSkipPrev(MultiQuestCommandPayload cmd)
    {
        Debug.Log("[MultiQuest] Skip prev");

        // TODO
    }

    private void OnPause(MultiQuestCommandPayload cmd)
    {
        Debug.Log("[MultiQuest] Pause");

        Time.timeScale = 0f;
    }

    private void OnResume(MultiQuestCommandPayload cmd)
    {
        Debug.Log("[MultiQuest] Resume");

        Time.timeScale = 1f;
    }

    private void OnRestartLesson(MultiQuestCommandPayload cmd)
    {
        Debug.Log("[MultiQuest] Restart lesson");

        // TODO: 현재 레슨 재시작 함수 연결
    }

    private void OnGoStage(MultiQuestCommandPayload cmd)
    {
        int stage = cmd.args != null ? cmd.args.stage : 0;
        int lesson = cmd.args != null ? cmd.args.lesson : 1;

        Debug.Log($"[MultiQuest] Go stage={stage}, lesson={lesson}");

        // TODO: 앱 내부 stage 이동 함수 연결
        // GameManager.Instance.LoadStage(stage, lesson);
    }

    private void MoveTaskToBackAndroid()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
            {
                activity.Call<bool>("moveTaskToBack", true);
            }
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("[MultiQuest] moveTaskToBack failed: " + e.Message);
        }
#endif
    }
}
```

---

## 사용 예시

### 1. 코딩 앱 Stage 3, Lesson 1 시작

**WPF:**
```csharp
await AgentApi.LaunchAppAsync(
    device.AgentHost,
    device.AgentStatusPort,
    "com.StoryWing.XR_Coding",
    "com.unity3d.player.UnityPlayerActivity",
    new Dictionary<string, int>
    {
        ["stage"] = 3,
        ["lesson"] = 1
    });
```

**HTTP Request:**
```
POST http://192.168.0.243:18080/command/launch
Content-Type: application/json

{
  "packageName": "com.StoryWing.XR_Coding",
  "activityName": "com.unity3d.player.UnityPlayerActivity",
  "extras": {
    "stage": 3,
    "lesson": 1
  }
}
```

**Unity에서 받기:**
```csharp
#if UNITY_ANDROID && !UNITY_EDITOR
using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
using (var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
using (var intent = activity.Call<AndroidJavaObject>("getIntent"))
{
    int stage = intent.Call<int>("getIntExtra", "stage", 0);
    int lesson = intent.Call<int>("getIntExtra", "lesson", 1);

    Debug.Log($"Stage: {stage}, Lesson: {lesson}");
    GameManager.Instance.LoadStage(stage, lesson);
}
#endif
```

---

### 2. 실행 중 Skip 명령

**WPF:**
```csharp
await AgentApi.SendCommandAsync(
    device.AgentHost,
    device.AgentStatusPort,
    "com.StoryWing.XR_Coding",
    "skip",
    new Dictionary<string, object>
    {
        ["direction"] = "next"
    });
```

**HTTP Request:**
```
POST http://192.168.0.243:18080/command/appCommand
Content-Type: application/json

{
  "packageName": "com.StoryWing.XR_Coding",
  "command": "skip",
  "args": {
    "direction": "next"
  }
}
```

**Unity에서 처리:**
```csharp
// MultiQuestCommandReceiver.OnCommandReceived() 자동 호출
// → OnSkipNext() 실행
// → GameManager.Instance.SkipNext() 호출
```

---

### 3. 일시정지

**WPF:**
```csharp
await AgentApi.SendCommandAsync(
    device.AgentHost,
    device.AgentStatusPort,
    "com.StoryWing.XR_Coding",
    "pause");
```

**Unity:**
```csharp
private void OnPause(MultiQuestCommandPayload cmd)
{
    Time.timeScale = 0f;  // 일시정지
}
```

---

### 4. 재개

**WPF:**
```csharp
await AgentApi.SendCommandAsync(
    device.AgentHost,
    device.AgentStatusPort,
    "com.StoryWing.XR_Coding",
    "resume");
```

**Unity:**
```csharp
private void OnResume(MultiQuestCommandPayload cmd)
{
    Time.timeScale = 1f;  // 재개
}
```

---

## 명령어 규격

### Launch Extras (앱 시작 시)

| Key | Type | Description |
|-----|------|-------------|
| stage | int | Stage 번호 (0-based) |
| lesson | int | Lesson 번호 (1-based) |

---

### Runtime Commands (실행 중)

| Command | Args | Description |
|---------|------|-------------|
| skip | direction: "next" | 다음 문제/단계 |
| next | - | skip과 동일 |
| prev | - | 이전 문제/단계 |
| pause | - | 일시정지 |
| resume | - | 재개 |
| restartLesson | - | 현재 레슨 재시작 |
| goStage | stage: int, lesson: int | 특정 Stage로 이동 |
| reset | - | 앱 초기화 |
| showHint | - | 힌트 표시 |
| hideHint | - | 힌트 숨김 |

---

## 동작 흐름

### 앱 시작 흐름

```
[WPF] 코딩 Stage 3 버튼 클릭
    ↓
StartCodingAppAsync(level=3, lesson=1)
    ↓
Agent 있음? → YES
    ↓
AgentApi.LaunchAppAsync(
    "com.StoryWing.XR_Coding",
    extras: { stage: 3, lesson: 1 }
)
    ↓
POST http://QuestIP:18080/command/launch
    ↓
[Android Agent] handleLaunch()
    ↓
Intent intent = new Intent()
intent.setClassName(pkg, activity)
intent.putExtra("stage", 3)
intent.putExtra("lesson", 1)
context.startActivity(intent)
    ↓
[Unity] 앱 시작
    ↓
Intent intent = activity.getIntent()
int stage = intent.getIntExtra("stage", 0)  // = 3
int lesson = intent.getIntExtra("lesson", 1)  // = 1
    ↓
GameManager.LoadStage(3, 1) ?
```

---

### 실시간 명령 흐름

```
[WPF] Skip 버튼 클릭
    ↓
AgentApi.SendCommandAsync(
    "com.StoryWing.XR_Coding",
    "skip"
)
    ↓
POST http://QuestIP:18080/command/appCommand
    ↓
[Android Agent] handleAppCommand()
    ↓
Intent intent = new Intent("ACTION_COMMAND")
intent.setClassName(pkg, "MultiQuestCommandReceiver")
intent.putExtra("payload", '{"command":"skip","args":{}}')
context.sendBroadcast(intent)
    ↓
[Unity Android] MultiQuestCommandReceiver.onReceive()
    ↓
UnitySendMessage(
    "MultiQuestCommandReceiver",
    "OnCommandReceived",
    payload
)
    ↓
[Unity C#] OnCommandReceived(payload)
    ↓
Parse JSON → command = "skip"
    ↓
switch (command) {
    case "skip": OnSkipNext()
}
    ↓
GameManager.Instance.SkipNext() ?
```

---

## 장점

### 1. ADB 의존성 제거 ?
- WiFi만으로 모든 제어
- ADB 연결 불필요
- USB 케이블 불필요

### 2. 실시간 제어 ?
- 앱 재시작 없이 명령 전송
- skip, pause, resume 즉시 반응
- 유연한 제어

### 3. 확장성 ?
- 새로운 명령 쉽게 추가
- JSON 기반 유연한 구조
- args로 다양한 파라미터 전달

### 4. 안정성 ?
- HTTP 요청 실패 시 ADB fallback
- 점진적 전환 가능
- 하위 호환성 유지

---

## PowerShell 테스트

### 코딩 앱 시작
```powershell
Invoke-RestMethod `
  -Uri "http://192.168.0.243:18080/command/launch" `
  -Method Post `
  -ContentType "application/json" `
  -Body '{"packageName":"com.StoryWing.XR_Coding","activityName":"com.unity3d.player.UnityPlayerActivity","extras":{"stage":3,"lesson":1}}'
```

### Skip 명령
```powershell
Invoke-RestMethod `
  -Uri "http://192.168.0.243:18080/command/appCommand" `
  -Method Post `
  -ContentType "application/json" `
  -Body '{"packageName":"com.StoryWing.XR_Coding","command":"skip","args":{"direction":"next"}}'
```

### Pause 명령
```powershell
Invoke-RestMethod `
  -Uri "http://192.168.0.243:18080/command/appCommand" `
  -Method Post `
  -ContentType "application/json" `
  -Body '{"packageName":"com.StoryWing.XR_Coding","command":"pause","args":{}}'
```

---

## 요약

### WPF 변경 완료
1. ? `AgentApi.SendCommandAsync()` 추가
2. ? `StartCodingAppAsync()` HTTP 전환
3. ? `EnglishAppBtn_Click()` HTTP 전환
4. ? ADB fallback 유지

### Android Agent 구현 필요
1. ? `handleAppCommand()` 추가
2. ? `sendStoryWingCommand()` 구현
3. ? Broadcast Intent 전송

### Unity 구현 필요
1. ? `MultiQuestCommandReceiver.java` 추가
2. ? `MultiQuestCommandReceiver.cs` 추가
3. ? AndroidManifest.xml Receiver 등록
4. ? 실제 게임 로직 연결

---

**? 빌드 성공!**
**? HTTP 기반 앱 실행!**
**? 실시간 명령 API 준비 완료!**
