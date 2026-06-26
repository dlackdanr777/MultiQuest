# MultiQuest ADB 제거 프로젝트 - 전체 요약

## 프로젝트 목표

Quest 기기 제어를 **ADB 의존에서 Agent API 기반으로 전환**하여:
- 무선 운영 가능
- 더 나은 앱 제어
- 낮은 오버헤드
- 확장 가능한 구조

---

## 완료된 작업

### 1. RTSP 미러링 전환 ?
**이전:** scrcpy.exe 프로세스 임베딩  
**현재:** LibVLCSharp.WPF로 RTSP 스트림 직접 재생

**파일:**
- `RtspTileViewModel.cs`: RTSP 재생 상태 관리
- `MainWindow.xaml`: VideoView 타일 템플릿
- `MainWindow.xaml.cs`: RTSP 타일 적용 로직

**효과:**
- scrcpy 프로세스 관리 불필요
- 더 낮은 레이턴시
- 안정적인 스트림 복구

---

### 2. Agent API 클라이언트 생성 ?
**파일:** `AgentApi.cs`

**메서드:**
```csharp
GetStatusAsync()         // 배터리, 스트림 상태 조회
LaunchAppAsync()         // 앱 실행
StopAppAsync()           // 앱 종료 요청
RestartCaptureAsync()    // RTSP 재시작
GoHomeAsync()            // 홈 화면 이동
```

**특징:**
- HTTP 기반 통신
- 타임아웃 3초
- 실패 시 null/false 반환

---

### 3. 배터리 조회 전환 ?
**이전:**
```csharp
adb -s {ip} shell dumpsys battery
```

**현재:**
```csharp
// 1순위: Agent API
var status = await AgentApi.GetStatusAsync(device.AgentHost);
level = status.Battery;

// 2순위: ADB 백업
if (level < 0 && device.Status == "Connected")
    level = GetBatteryLevel(device.Ip);
```

**효과:**
- Agent 기기는 ADB 사용 안 함
- 더 빠른 조회 속도
- AgentOnly 기기도 배터리 확인 가능

---

### 4. 앱 실행 전환 ?
**이전:**
```csharp
adb -s {ip} shell am start -n {pkg}/{activity}
```

**현재:**
```csharp
// 1순위: Agent API
bool launched = await AgentApi.LaunchAppAsync(
    device.AgentHost,
    device.AgentStatusPort,
    pkg,
    activity);

// 2순위: ADB 백업
if (!success && device.Status == "Connected")
    RunCmd($"adb -s {device.Ip} shell am start -n {pkg}/{activity}");
```

**효과:**
- 무선 기기에서도 앱 실행
- Intent extras 전달 가능 (stage, lesson)
- 실행 성공 여부 반환

---

### 5. 앱 종료 전환 ?
**이전:**
```csharp
adb -s {ip} shell am force-stop {pkg}
```

**현재:**
```csharp
// 1순위: Agent API (협력 종료)
await AgentApi.StopAppAsync(
    device.AgentHost,
    device.AgentStatusPort,
    pkg);

// 2순위: ADB force-stop 백업
if (!success && device.Status == "Connected")
    RunCmd($"adb -s {device.Ip} shell am force-stop {pkg}");
```

**효과:**
- 정상 종료 절차 실행 가능
- 앱 상태 저장 가능
- force-stop은 백업으로만 사용

---

## 아키텍처

### 제어 흐름

```
WPF (PC)
    ↓ HTTP
Agent (Quest)
    ↓ Intent/Broadcast
StoryWing 앱 (Quest)
```

### 우선순위 시스템

```
모든 제어 명령:
    1순위: Agent API (무선)
        ↓ 실패
    2순위: ADB (USB/무선 디버깅)
        ↓ 실패
    오류 메시지
```

---

## 필요한 Android 작업

### 1. Agent HTTP 서버 업데이트 ? 최우선

`SimpleStatusHttpServer.kt`에 추가:

```kotlin
// POST /command/launch
server.createContext("/command/launch") { ... }

// POST /command/stop
server.createContext("/command/stop") { ... }

// POST /command/restartCapture (선택)
server.createContext("/command/restartCapture") { ... }

// POST /command/home (선택)
server.createContext("/command/home") { ... }
```

**상세 구현:** `Android_Agent_API_구현가이드.md` 참조

---

### 2. StoryWing 앱에 종료 수신기 추가 ? 필수

모든 Unity 앱에 추가:

**AndroidManifest.xml:**
```xml
<receiver android:name="com.storywing.MultiQuestCommandReceiver"
          android:exported="false">
    <intent-filter>
        <action android:name="com.storywing.multiquest.ACTION_STOP" />
    </intent-filter>
</receiver>
```

**Android Plugin (Kotlin):**
```kotlin
class MultiQuestCommandReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        UnityPlayer.UnitySendMessage(
            "MultiQuestCommandReceiver",
            "OnStopRequested",
            ""
        )
    }
}
```

**Unity C#:**
```csharp
public class MultiQuestCommandReceiver : MonoBehaviour
{
    public void OnStopRequested(string payload)
    {
        Application.Quit();
    }
}
```

---

### 3. AndroidManifest.xml 수정

**Agent 앱:**
```xml
<queries>
    <package android:name="com.StoryWing.EnglishTown" />
    <package android:name="com.StoryWing.Ocean_Adventure" />
    <!-- ... 모든 StoryWing 앱 -->
</queries>
```

---

## 테스트 체크리스트

### ? Phase 1: Agent API 동작 확인

```bash
# 1. Agent 상태 조회
curl http://192.168.0.243:18080/status

# 2. 앱 실행 테스트
curl -X POST http://192.168.0.243:18080/command/launch \
  -H "Content-Type: application/json" \
  -d '{"packageName":"com.StoryWing.Jurassic","activityName":"com.unity3d.player.UnityPlayerActivity"}'

# 3. 앱 종료 테스트
curl -X POST http://192.168.0.243:18080/command/stop \
  -H "Content-Type: application/json" \
  -d '{"packageName":"com.StoryWing.Jurassic"}'
```

### ? Phase 2: WPF 통합 테스트

1. **Agent 검색**
   - Agent 검색 버튼 클릭
   - Meta Device 화면에 타일 표시 확인
   - 배터리 표시 확인

2. **RTSP 미러링**
   - 타일에 RTSP 스트림 재생 확인
   - scrcpy 창이 뜨지 않는지 확인
   - 상태 오버레이 표시 확인

3. **앱 실행 (Agent)**
   - "영어마을" 버튼 클릭
   - Unity 앱 실행 확인
   - Wireshark로 HTTP POST 확인
   - ADB 명령이 실행되지 않는지 확인

4. **앱 종료 (Agent)**
   - "전체 종료" 버튼 클릭
   - Unity 앱이 정상 종료되는지 확인
   - Application.Quit() 호출 확인

5. **ADB Fallback**
   - Agent 없는 USB 연결 기기
   - 배터리/앱 실행/종료가 ADB로 동작하는지 확인

---

## 현재 상태

### ? 완료
- RTSP 미러링 (scrcpy 대체)
- Agent 검색 (mDNS + DirectScan)
- WPF Agent API 클라이언트
- 배터리 조회 Agent 전환
- 앱 실행 Agent 전환
- 앱 종료 Agent 전환
- 문서화

### ?? 진행 필요
- Android Agent HTTP 서버 업데이트
- StoryWing 앱 종료 수신기 추가
- 통합 테스트

### ? 향후 고려사항
- ADB 타이머 비활성화 옵션
- 웹 대시보드 통합
- 로그 조회 API
- 스크린샷 API
- 앱 상태 모니터링 API

---

## ADB 의존성 상태

### ? Agent API로 대체 완료
| 기능 | 이전 방식 | 현재 방식 | 비고 |
|------|-----------|-----------|------|
| 미러링 | scrcpy | RTSP | 완전 대체 |
| 검색 | adb devices | mDNS + 18080 scan | 완전 대체 |
| 배터리 | dumpsys battery | Agent /status | ADB 백업 |
| 앱 실행 | am start | Agent /command/launch | ADB 백업 |
| 앱 종료 | am force-stop | Agent /command/stop | ADB 백업 |

### ?? ADB 백업 유지
- Agent 없는 기기 제어
- Agent 실패 시 fallback
- 관리자/복구 모드

### ? Agent로 대체 불가 (ADB 계속 필요)
- 임의 shell 명령 (`input keyevent`)
- 다른 앱 강제 종료 (시스템 권한)
- Guardian/Boundary 초기화
- 설정 앱 제어
- 무선 디버깅 자동 페어링
- 기기 재부팅

---

## 장점

### 1. 무선 운영
- USB 케이블 불필요
- 여러 Quest 동시 관리
- 이동식 설치 가능

### 2. 더 나은 제어
- 협력 종료로 정상 종료 절차
- Intent extras로 데이터 전달
- 앱 상태 조회 가능

### 3. 성능 향상
- ADB 프로세스 오버헤드 제거
- HTTP 직접 호출
- 병렬 처리 효율

### 4. 확장성
- 새 명령 추가 쉬움
- 웹 대시보드 통합 가능
- 다른 플랫폼 지원 가능

---

## 제약사항

### 1. Android Agent 필수
Agent 앱이 설치/실행 중이어야 함

### 2. 협력 종료 구현 필요
StoryWing 앱들이 종료 명령을 받아야 함

### 3. Background Activity Launch 제한
Android 10+ 환경에서는 백그라운드 실행 제한  
→ Agent를 foreground 유지 권장

### 4. Package Visibility
Android 11+ 환경에서는 `<queries>` 선언 필요

---

## 프로젝트 파일

### WPF (PC)
```
MultiQuest-Management/
├── AgentApi.cs                    # Agent HTTP API 클라이언트 ? 새로 추가
├── MainWindow.xaml.cs             # 배터리/앱 제어 로직 수정
├── MainWindow.xaml                # RTSP VideoView 타일 수정
├── RtspTileViewModel.cs           # RTSP 재생 관리
├── RtspMirrorWindow.xaml.cs       # (참고용, A안에서는 미사용)
└── RtspMirrorWindow.xaml          # (참고용, A안에서는 미사용)
```

### 문서
```
├── RTSP_A안_완료.md                         # RTSP 전환 문서
├── ADB제거_Agent전환_완료.md                # Agent API 전환 문서
├── Android_Agent_API_구현가이드.md          # Agent HTTP 서버 구현 가이드
└── ADB제거_전체요약.md                      # 이 파일
```

### Android (Quest) - 구현 필요
```
Agent/
├── SimpleStatusHttpServer.kt      # /command/* 엔드포인트 추가 필요
└── AndroidManifest.xml            # <queries> 추가 필요

StoryWing 앱들/
├── MultiQuestCommandReceiver.kt   # BroadcastReceiver 추가 필요
├── MultiQuestCommandReceiver.cs   # Unity C# 스크립트 추가 필요
└── AndroidManifest.xml            # receiver 등록 필요
```

---

## 다음 단계

### 1. Android Agent 업데이트 (최우선)
- [ ] `SimpleStatusHttpServer.kt`에 `/command/launch` 추가
- [ ] `SimpleStatusHttpServer.kt`에 `/command/stop` 추가
- [ ] `AndroidManifest.xml`에 `<queries>` 추가
- [ ] curl로 각 엔드포인트 테스트
- [ ] adb logcat으로 로그 확인

### 2. StoryWing 앱 업데이트 (필수)
- [ ] 각 앱에 `MultiQuestCommandReceiver` 추가
- [ ] AndroidManifest.xml 수정
- [ ] 빌드 및 배포
- [ ] 종료 명령 테스트

### 3. 통합 테스트
- [ ] WPF Agent 검색 → RTSP 미러링
- [ ] 배터리 조회 (Agent API)
- [ ] 앱 실행 (Agent API)
- [ ] 앱 종료 (Agent API)
- [ ] ADB fallback 동작 확인

### 4. 운영 배포
- [ ] 현장 테스트
- [ ] 문제 수집 및 수정
- [ ] ADB 타이머 비활성화 고려
- [ ] 모니터링 및 로그 수집

---

## 참고 자료

### Android 공식 문서
- [Background Activity Launch Restrictions](https://developer.android.com/guide/components/activities/background-starts)
- [Package Visibility](https://developer.android.com/training/package-visibility)
- [BroadcastReceiver](https://developer.android.com/guide/components/broadcasts)
- [Intent](https://developer.android.com/reference/android/content/Intent)

### 프로젝트 문서
- **RTSP 전환:** `RTSP_A안_완료.md`
- **Agent 전환:** `ADB제거_Agent전환_완료.md`
- **Android 구현:** `Android_Agent_API_구현가이드.md`

---

## 연락처 / 이슈

문제 발생 시:
1. WPF 로그 확인: Visual Studio Output 창
2. Android 로그 확인: `adb logcat | grep -i "MQ-HTTP\|MQ-Agent\|MultiQuest"`
3. HTTP 통신 확인: Wireshark 또는 curl 테스트
4. GitHub Issues에 로그와 함께 보고

---

**? ADB 제거 프로젝트 WPF 측 완료!**

**다음:** Android Agent HTTP 서버 업데이트 및 StoryWing 앱 종료 수신기 구현
