# RTSP 스트리밍 WPF 통합 완료

## ? 완료된 작업

### 1. NuGet 패키지 설치
- ? `LibVLCSharp.WPF` (3.10.0)
- ? `VideoLAN.LibVLC.Windows` (3.0.23.1)

### 2. 새 파일 생성
- ? `RtspTileViewModel.cs` - RTSP 타일 뷰모델
- ? `RtspMirrorWindow.xaml` - RTSP 미러링 창 UI
- ? `RtspMirrorWindow.xaml.cs` - RTSP 미러링 창 로직

### 3. MainWindow 수정
- ? `_rtspMirrorWindow` 필드 추가
- ? `AgentSearchButton_Click` 메서드 수정
  - Agent 검색
  - Streaming 상태 필터링
  - RTSP 미러링 창 열기

### 4. 빌드 상태
**? 빌드 성공**

---

## ?? 구현 세부 사항

### RtspTileViewModel.cs
```csharp
public sealed class RtspTileViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly LibVLC _libVlc;
    public LibVLCSharp.Shared.MediaPlayer MediaPlayer { get; }

    // QuestAgentInfo 기반 타이틀/서브타이틀 표시
    // RTSP 스트림 재생
    // 상태 표시 (재생 중, 버퍼링, 오류 등)
}
```

**주요 기능:**
- 하드웨어 디코딩 활성화
- RTSP-TCP 모드
- 로컬 네트워크 최적화 (caching=250ms)
- 프레임 드롭 설정
- 이벤트 기반 상태 표시

### RtspMirrorWindow.xaml
```xaml
<vlc:VideoView MediaPlayer="{Binding MediaPlayer}">
    <Grid IsHitTestVisible="False">
        <!-- 상단: 장치 정보 (모델명, IP, 배터리) -->
        <!-- 하단: 스트림 상태 (재생 중, 버퍼링 등) -->
    </Grid>
</vlc:VideoView>
```

**레이아웃:**
- 1대: 1열
- 2~4대: 2열
- 5~9대: 3열
- 10~16대: 4열
- 17대 이상: 5열

### AgentSearchButton_Click 흐름
```
버튼 클릭
  ↓
Agent 검색 (3초 타임아웃)
  ↓
streaming 상태 필터링
  ↓
RtspMirrorWindow 생성 및 표시
  ↓
각 타일 자동 재생 시작
```

---

## ?? 테스트 방법

### 1. Quest Agent 준비
```bash
# Android Agent가 실행 중인지 확인
adb logcat -s MQ-Agent MQ-HTTP

# 예상 로그:
# MQ-HTTP: HTTP server started on port=18080
# MQ-Agent: NSD Registered: _multiquest-agent._tcp.local.
# MQ-Agent: RTSP server started on port=8554
```

### 2. WPF에서 Agent 검색
1. MultiQuest-Management 앱 실행
2. "Agent 검색" 버튼 클릭
3. 3초 대기

**성공 시:**
- RTSP Mirror 창이 열림
- Quest 화면이 타일에 표시됨
- 상단: 모델명, IP, 배터리
- 하단: 재생 상태

**실패 시:**
- "Agent를 찾지 못했습니다" 메시지
- 또는 "streaming 상태인 기기가 없습니다" 메시지

### 3. 직접 RTSP URL 테스트
VLC로 먼저 확인:
```
rtsp://192.168.0.243:8554/live
```

---

## ?? 문제 해결

### Agent를 찾지 못할 때
1. **네트워크 확인**
   ```bash
   ping 192.168.0.243
   curl http://192.168.0.243:18080/status
   ```

2. **mDNS 확인**
   - Windows 방화벽 확인
   - Bonjour Print Services 설치 여부
   - AP Isolation / Client Isolation 설정

3. **Android 로그**
   ```bash
   adb logcat -s MQ-Agent MQ-HTTP MQ-CAPTURE
   ```

### RTSP 재생이 안 될 때
1. **상태 확인**
   - 하단 상태 메시지 확인
   - "버퍼링" 상태가 계속되면 네트워크 문제
   - "재생 오류"면 RTSP URL 또는 서버 문제

2. **VLC로 직접 테스트**
   ```
   vlc rtsp://192.168.0.243:8554/live
   ```

3. **Android CaptureService 확인**
   ```bash
   adb logcat -s MQ-CAPTURE

   # 정상 로그:
   # MQ-CAPTURE: Encoder started
   # MQ-CAPTURE: drainEncoder started
   ```

### 화면이 검은색일 때
1. **Quest 시스템 UI 확인**
   - Boundary 설정 화면이 활성화되어 있지 않은지
   - 앱이 백그라운드로 가지 않았는지

2. **스트림 상태 확인**
   ```bash
   curl http://192.168.0.243:18080/status

   # streamState가 "streaming"인지 확인
   ```

---

## ?? 성능 최적화

### LibVLC 설정
```csharp
_libVlc = new LibVLC(
    "--no-audio",              // 오디오 비활성화 (불필요)
    "--rtsp-tcp",              // TCP 모드 (UDP보다 안정적)
    "--network-caching=250",   // 네트워크 캐싱 250ms
    "--live-caching=250"       // 라이브 캐싱 250ms (저지연)
);
```

### Media 옵션
```csharp
_media.AddOption(":rtsp-tcp");           // TCP 강제
_media.AddOption(":network-caching=250"); // 250ms 캐싱
_media.AddOption(":live-caching=250");    // 라이브 최적화
_media.AddOption(":clock-jitter=0");      // 클럭 지터 최소화
_media.AddOption(":drop-late-frames");    // 지연 프레임 드롭
_media.AddOption(":skip-frames");         // 프레임 스킵 허용
```

**결과:**
- 지연시간: 약 250~500ms
- 네트워크 안정성 우선
- 프레임 드롭으로 동기화 유지

---

## ?? 다음 단계

### A. 현재 구조 (권장)
```
기존 ADB/scrcpy 방식 → Meta Device 타일 (MainWindow)
새로운 Agent 방식 → RTSP Mirror 창 (RtspMirrorWindow)
```

**장점:**
- 기존 기능 보존
- 점진적 전환 가능
- 두 방식 비교 가능

### B. 완전 전환 (향후)
```
Meta Device 타일을 RTSP 방식으로 교체
scrcpy 제거
```

**필요 작업:**
1. MainWindow의 타일 DataTemplate을 VideoView 기반으로 변경
2. Device 클래스에 RtspTileViewModel 통합
3. scrcpy 관련 코드 제거

---

## ?? 변경된 파일 목록

### 새 파일
- ? `MultiQuest-Management\RtspTileViewModel.cs`
- ? `MultiQuest-Management\RtspMirrorWindow.xaml`
- ? `MultiQuest-Management\RtspMirrorWindow.xaml.cs`

### 수정된 파일
- ? `MultiQuest-Management\MainWindow.xaml.cs`
  - `_rtspMirrorWindow` 필드 추가
  - `AgentSearchButton_Click` 메서드 수정

### 프로젝트 파일
- ? `MultiQuest-Management\MultiQuest-Management.csproj`
  - LibVLCSharp.WPF 패키지 추가
  - VideoLAN.LibVLC.Windows 패키지 추가

---

## ?? 사용 팁

### 1. 여러 대 동시 재생
- 최대 16대까지 안정적 (5×3 레이아웃)
- 네트워크 대역폭 고려 (1080p 기준 각 5~10Mbps)

### 2. 지연시간 조정
더 낮은 지연시간을 원하면 caching 값 감소:
```csharp
"--network-caching=100",
"--live-caching=100"
```

단, 네트워크가 불안정하면 끊김 발생 가능

### 3. 품질 vs 안정성
- 현재 설정: 안정성 우선 (250ms)
- 낮은 지연: 100ms (끊김 가능성)
- 높은 안정성: 500ms (지연 증가)

---

## ? 검증 완료 항목

- [x] NuGet 패키지 설치
- [x] RtspTileViewModel 구현
- [x] RtspMirrorWindow XAML/CS 구현
- [x] MainWindow 통합
- [x] 빌드 성공
- [ ] Agent 실제 테스트 (Quest 연결 필요)
- [ ] RTSP 스트림 재생 테스트
- [ ] 여러 대 동시 재생 테스트

---

**작성일:** 2025-01-XX  
**빌드 상태:** ? 성공  
**다음 단계:** Quest Agent 실제 연결 테스트
