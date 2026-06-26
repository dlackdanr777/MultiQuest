# Android Agent HTTP API 구현 가이드

이 파일은 WPF Agent API 클라이언트가 호출하는 엔드포인트의 Android 구현 예시입니다.

## SimpleStatusHttpServer.kt 수정

기존 `/status` 엔드포인트에 추가로 다음을 구현하세요:

### 1. POST /command/launch - 앱 실행

```kotlin
server.createContext("/command/launch") { exchange ->
    if (exchange.requestMethod != "POST") {
        exchange.sendResponseHeaders(405, -1)
        return@createContext
    }

    try {
        val body = exchange.requestBody.bufferedReader().use { it.readText() }
        val json = JSONObject(body)

        val packageName = json.getString("packageName")
        val activityName = json.optString("activityName", "com.unity3d.player.UnityPlayerActivity")

        // extras는 선택적
        val extrasMap = mutableMapOf<String, Int>()
        if (json.has("extras")) {
            val extras = json.getJSONObject("extras")
            extras.keys().forEach { key ->
                extrasMap[key] = extras.getInt(key)
            }
        }

        // Intent로 앱 실행
        val success = launchStoryWingApp(
            context = applicationContext,
            packageName = packageName,
            activityName = activityName,
            extras = extrasMap
        )

        val response = if (success) {
            """{"status":"ok","launched":true}"""
        } else {
            """{"status":"error","launched":false}"""
        }

        exchange.responseHeaders.add("Content-Type", "application/json")
        exchange.sendResponseHeaders(if (success) 200 else 500, response.length.toLong())
        exchange.responseBody.write(response.toByteArray())
        exchange.responseBody.close()

        Log.i("MQ-HTTP", "Launch request: $packageName -> $success")
    } catch (e: Exception) {
        Log.e("MQ-HTTP", "Launch error", e)
        exchange.sendResponseHeaders(500, -1)
    }
}

private fun launchStoryWingApp(
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

            // extras 전달 (stage, lesson 등)
            for ((key, value) in extras) {
                putExtra(key, value)
            }
        }

        context.startActivity(intent)
        Log.i("MQ-Agent", "Launched: $packageName/$activityName with extras=$extras")
        true
    } catch (e: ActivityNotFoundException) {
        Log.e("MQ-Agent", "Activity not found: $packageName/$activityName", e)
        false
    } catch (e: Exception) {
        Log.e("MQ-Agent", "Launch failed: $packageName/$activityName", e)
        false
    }
}
```

**주의사항:**
- Android 10+ background activity launch 제한이 있습니다
- Agent가 foreground 상태가 아니면 실행이 차단될 수 있습니다
- ForegroundService로 Agent를 유지하거나, 잠깐 투명 Activity를 띄운 뒤 실행하는 방법도 고려하세요

---

### 2. POST /command/stop - 앱 종료 요청

```kotlin
server.createContext("/command/stop") { exchange ->
    if (exchange.requestMethod != "POST") {
        exchange.sendResponseHeaders(405, -1)
        return@createContext
    }

    try {
        val body = exchange.requestBody.bufferedReader().use { it.readText() }
        val json = JSONObject(body)

        val packageName = json.getString("packageName")

        // 브로드캐스트로 종료 요청
        val success = requestStopApp(
            context = applicationContext,
            packageName = packageName
        )

        val response = if (success) {
            """{"status":"ok","requested":true}"""
        } else {
            """{"status":"error","requested":false}"""
        }

        exchange.responseHeaders.add("Content-Type", "application/json")
        exchange.sendResponseHeaders(if (success) 200 else 500, response.length.toLong())
        exchange.responseBody.write(response.toByteArray())
        exchange.responseBody.close()

        Log.i("MQ-HTTP", "Stop request: $packageName -> $success")
    } catch (e: Exception) {
        Log.e("MQ-HTTP", "Stop error", e)
        exchange.sendResponseHeaders(500, -1)
    }
}

private fun requestStopApp(context: Context, packageName: String): Boolean {
    return try {
        val intent = Intent("com.storywing.multiquest.ACTION_STOP").apply {
            setPackage(packageName)
        }

        context.sendBroadcast(intent)
        Log.i("MQ-Agent", "Stop requested: $packageName")
        true
    } catch (e: Exception) {
        Log.e("MQ-Agent", "Stop request failed: $packageName", e)
        false
    }
}
```

**중요:** 
이 방식은 **협력 종료**입니다. 
StoryWing 앱이 브로드캐스트를 받아서 스스로 종료해야 합니다.
일반 Android 앱 권한으로는 다른 앱을 force-stop 할 수 없습니다.

---

### 3. POST /command/restartCapture - RTSP 재시작

```kotlin
server.createContext("/command/restartCapture") { exchange ->
    if (exchange.requestMethod != "POST") {
        exchange.sendResponseHeaders(405, -1)
        return@createContext
    }

    try {
        // MediaProjection 재시작
        val success = restartCapture()

        val response = if (success) {
            """{"status":"ok","restarted":true}"""
        } else {
            """{"status":"error","restarted":false}"""
        }

        exchange.responseHeaders.add("Content-Type", "application/json")
        exchange.sendResponseHeaders(if (success) 200 else 500, response.length.toLong())
        exchange.responseBody.write(response.toByteArray())
        exchange.responseBody.close()

        Log.i("MQ-HTTP", "Restart capture -> $success")
    } catch (e: Exception) {
        Log.e("MQ-HTTP", "Restart capture error", e)
        exchange.sendResponseHeaders(500, -1)
    }
}

private fun restartCapture(): Boolean {
    return try {
        // CaptureService 재시작 로직
        // 실제 구현은 프로젝트 구조에 따라 다를 수 있습니다

        // 예: Service Intent 재시작
        val intent = Intent(applicationContext, CaptureService::class.java).apply {
            action = "ACTION_RESTART_CAPTURE"
        }
        applicationContext.startService(intent)

        true
    } catch (e: Exception) {
        Log.e("MQ-Agent", "Restart capture failed", e)
        false
    }
}
```

---

### 4. POST /command/home - 홈 화면 이동

```kotlin
server.createContext("/command/home") { exchange ->
    if (exchange.requestMethod != "POST") {
        exchange.sendResponseHeaders(405, -1)
        return@createContext
    }

    try {
        val success = goHome(applicationContext)

        val response = if (success) {
            """{"status":"ok","home":true}"""
        } else {
            """{"status":"error","home":false}"""
        }

        exchange.responseHeaders.add("Content-Type", "application/json")
        exchange.sendResponseHeaders(if (success) 200 else 500, response.length.toLong())
        exchange.responseBody.write(response.toByteArray())
        exchange.responseBody.close()

        Log.i("MQ-HTTP", "Go home -> $success")
    } catch (e: Exception) {
        Log.e("MQ-HTTP", "Go home error", e)
        exchange.sendResponseHeaders(500, -1)
    }
}

private fun goHome(context: Context): Boolean {
    return try {
        val intent = Intent(Intent.ACTION_MAIN).apply {
            addCategory(Intent.CATEGORY_HOME)
            flags = Intent.FLAG_ACTIVITY_NEW_TASK
        }
        context.startActivity(intent)
        Log.i("MQ-Agent", "Launched home screen")
        true
    } catch (e: Exception) {
        Log.e("MQ-Agent", "Go home failed", e)
        false
    }
}
```

---

## AndroidManifest.xml 추가

### 1. Package Visibility (Android 11+)

Agent가 StoryWing 앱들을 찾고 실행할 수 있도록:

```xml
<manifest ...>
    <queries>
        <!-- StoryWing 앱 목록 -->
        <package android:name="com.StoryWing.EnglishTown" />
        <package android:name="com.StoryWing.AlphabatApp" />
        <package android:name="com.StoryWing.Ocean_Adventure" />
        <package android:name="com.StoryWing.OceanAdventure" />
        <package android:name="com.StoryWing.Korea_History" />
        <package android:name="com.StoryWing.KoreaHistory2" />
        <package android:name="com.StoryWing.Solar_System" />
        <package android:name="com.StoryWing.SpaceApp" />
        <package android:name="com.StoryWing.Fire_Safety" />
        <package android:name="com.StoryWing.FirepreventionApp" />
        <package android:name="com.StoryWing.Jurassic" />
        <package android:name="com.StoryWing.JurassicV2" />
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

## StoryWing 앱 측 구현 (Unity)

각 StoryWing 콘텐츠 앱에 종료 명령 수신 기능을 추가해야 합니다.

### 1. AndroidManifest.xml (Plugins/Android/)

```xml
<!-- 기존 manifest에 추가 -->
<receiver android:name="com.storywing.MultiQuestCommandReceiver"
          android:exported="false">
    <intent-filter>
        <action android:name="com.storywing.multiquest.ACTION_STOP" />
    </intent-filter>
</receiver>
```

### 2. Android Plugin (Kotlin) - Plugins/Android/src/

```kotlin
package com.storywing

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.util.Log
import com.unity3d.player.UnityPlayer

class MultiQuestCommandReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        when (intent.action) {
            "com.storywing.multiquest.ACTION_STOP" -> {
                Log.i("MultiQuest", "Stop command received")

                // Unity로 메시지 전달
                UnityPlayer.UnitySendMessage(
                    "MultiQuestCommandReceiver",  // GameObject 이름
                    "OnStopRequested",            // 메서드 이름
                    ""                            // 파라미터 (빈 문자열)
                )
            }
        }
    }
}
```

### 3. Unity C# Script - Assets/Scripts/

```csharp
using UnityEngine;

public class MultiQuestCommandReceiver : MonoBehaviour
{
    void Awake()
    {
        // GameObject 이름을 "MultiQuestCommandReceiver"로 설정
        gameObject.name = "MultiQuestCommandReceiver";

        // 씬 전환 시에도 유지
        DontDestroyOnLoad(gameObject);
    }

    /// <summary>
    /// Android Agent로부터 종료 명령을 받았을 때 호출됩니다.
    /// </summary>
    public void OnStopRequested(string payload)
    {
        Debug.Log("MultiQuest stop requested");

        // 옵션 1: 앱 종료
        Application.Quit();

        // 옵션 2: 메인 메뉴로 이동
        // UnityEngine.SceneManagement.SceneManager.LoadScene("MainMenu");

        // 옵션 3: 홈 화면 호출 (종료 안 하고 백그라운드로)
        /*
        #if UNITY_ANDROID && !UNITY_EDITOR
        using (AndroidJavaClass unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
        using (AndroidJavaObject currentActivity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
        using (AndroidJavaObject intent = new AndroidJavaObject("android.content.Intent"))
        {
            intent.Call<AndroidJavaObject>("setAction", "android.intent.action.MAIN");
            intent.Call<AndroidJavaObject>("addCategory", "android.intent.category.HOME");
            currentActivity.Call("startActivity", intent);
        }
        #endif
        */
    }
}
```

### 4. Unity Scene 설정

메인 씬에 빈 GameObject를 만들고 `MultiQuestCommandReceiver` 스크립트를 추가하세요.
또는 첫 씬에서 생성:

```csharp
// 앱 시작 시 자동 생성
void Start()
{
    GameObject receiver = new GameObject("MultiQuestCommandReceiver");
    receiver.AddComponent<MultiQuestCommandReceiver>();
}
```

---

## 테스트

### 1. HTTP 서버 테스트 (PC → Quest)

```bash
# 앱 실행 테스트
curl -X POST http://192.168.0.243:18080/command/launch \
  -H "Content-Type: application/json" \
  -d '{"packageName":"com.StoryWing.Jurassic","activityName":"com.unity3d.player.UnityPlayerActivity","extras":{"stage":3,"lesson":1}}'

# 앱 종료 테스트
curl -X POST http://192.168.0.243:18080/command/stop \
  -H "Content-Type: application/json" \
  -d '{"packageName":"com.StoryWing.Jurassic"}'

# RTSP 재시작 테스트
curl -X POST http://192.168.0.243:18080/command/restartCapture

# 홈 화면 테스트
curl -X POST http://192.168.0.243:18080/command/home
```

### 2. Android 로그 확인

```bash
adb logcat | grep -i "MQ-HTTP\|MQ-Agent\|MultiQuest"
```

예상 출력:
```
I/MQ-HTTP: Launch request: com.StoryWing.Jurassic -> true
I/MQ-Agent: Launched: com.StoryWing.Jurassic/com.unity3d.player.UnityPlayerActivity with extras={stage=3, lesson=1}
I/MultiQuest: Stop command received
I/MQ-Agent: Stop requested: com.StoryWing.Jurassic
```

---

## 보안 고려사항

### 1. 외부 접근 제한 (선택)

만약 같은 LAN이 아닌 외부에서 접근을 막으려면:

```kotlin
// IP 필터링 추가
val remoteAddress = exchange.remoteAddress.address.hostAddress
if (!isLocalNetwork(remoteAddress)) {
    exchange.sendResponseHeaders(403, -1)
    return@createContext
}

private fun isLocalNetwork(ip: String): Boolean {
    return ip.startsWith("192.168.") || 
           ip.startsWith("10.") || 
           ip.startsWith("172.16.") ||
           ip == "127.0.0.1"
}
```

### 2. 인증 추가 (선택)

간단한 토큰 인증:

```kotlin
// 요청 헤더에서 토큰 확인
val authHeader = exchange.requestHeaders.getFirst("Authorization")
if (authHeader != "Bearer YOUR_SECRET_TOKEN") {
    exchange.sendResponseHeaders(401, -1)
    return@createContext
}
```

---

## 문제 해결

### Background Activity Launch 실패

**증상:** 앱 실행 요청은 성공하지만 실제로 실행되지 않음

**원인:** Android 10+ background activity launch 제한

**해결책:**
1. Agent를 ForegroundService로 유지
2. 또는 투명 Activity를 잠깐 띄운 뒤 실행:

```kotlin
// LauncherActivity.kt (투명 Activity)
class LauncherActivity : Activity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        val targetPackage = intent.getStringExtra("targetPackage")
        val targetActivity = intent.getStringExtra("targetActivity")

        if (targetPackage != null && targetActivity != null) {
            val launchIntent = Intent().apply {
                setClassName(targetPackage, targetActivity)
                addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
            }
            startActivity(launchIntent)
        }

        finish()  // 즉시 종료
    }
}
```

AndroidManifest.xml:
```xml
<activity
    android:name=".LauncherActivity"
    android:theme="@android:style/Theme.Translucent.NoTitleBar"
    android:excludeFromRecents="true"
    android:noHistory="true" />
```

---

## 참고

- HTTP 서버는 이미 포트 18080에서 실행 중
- `/status` 엔드포인트는 이미 구현됨
- mDNS 서비스 `_multiquest-agent._tcp.local.`도 이미 광고 중
- 위 코드는 기존 `SimpleStatusHttpServer.kt`에 추가하면 됩니다

---

**? Agent HTTP API 구현 가이드 완료**
