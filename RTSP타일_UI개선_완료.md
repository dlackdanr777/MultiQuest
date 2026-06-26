# RTSP 타일 UI 개선 완료

## 변경 사항

### 1. 바인딩 오류 수정 ?
**문제:** `System.InvalidOperationException` - VideoView 내부에 Grid를 중첩하면서 바인딩 경로가 꼬임

**해결:** VideoView를 단순화하고 overlay를 VideoView 외부로 분리

**변경 전:**
```xml
<vlc:VideoView MediaPlayer="{Binding Rtsp.MediaPlayer}">
    <Grid IsHitTestVisible="False">
        <!-- 오버레이들 -->
    </Grid>
</vlc:VideoView>
```

**변경 후:**
```xml
<Grid>
    <vlc:VideoView MediaPlayer="{Binding Rtsp.MediaPlayer}" />
    <!-- 로딩 인디케이터 -->
    <!-- 미연결 메시지 -->
</Grid>
```

---

### 2. 로딩 인디케이터 추가 ?
**요구사항:** ADB scrcpy처럼 스트림 연결 중일 때 로딩 표시

**구현:**
- 회전하는 "?" 아이콘
- "연결 중..." 텍스트
- 반투명 검은 배경

**표시 조건:**
```xml
<DataTrigger Binding="{Binding Rtsp.Status}" Value="연결 중">
    <Setter Property="Visibility" Value="Visible" />
</DataTrigger>
<DataTrigger Binding="{Binding Rtsp.Status}" Value="버퍼링">
    <Setter Property="Visibility" Value="Visible" />
</DataTrigger>
```

**애니메이션:**
```xml
<DoubleAnimation Storyboard.TargetProperty="Angle"
                 From="0" To="360"
                 Duration="0:0:1.5"
                 RepeatBehavior="Forever" />
```

---

### 3. 오버레이 정보 제거 ?
**제거된 요소:**
- ? 상단 오버레이: 기기 이름, Agent 호스트, 배터리
- ? 하단 오버레이: RTSP 상태, 상태 색상

**이유:**
- 깔끔한 미러링 화면 유지
- 타일 상단 헤더에 이미 이름/배터리 정보 표시됨
- RTSP 상태는 로딩 인디케이터로 충분

---

## 파일 변경 요약

### MainWindow.xaml
**변경 내용:**
1. VideoView 구조 단순화
2. 로딩 인디케이터 추가 (회전 애니메이션)
3. 이름/배터리/상태 오버레이 제거

**Before:**
```xml
<vlc:VideoView>
    <Grid>
        <Border VerticalAlignment="Top">
            <!-- 이름, Agent, 배터리 -->
        </Border>
        <Border VerticalAlignment="Bottom">
            <!-- RTSP 상태 -->
        </Border>
    </Grid>
</vlc:VideoView>
```

**After:**
```xml
<Grid>
    <vlc:VideoView MediaPlayer="{Binding Rtsp.MediaPlayer}" />

    <Border> <!-- 로딩 인디케이터 -->
        <StackPanel>
            <TextBlock Text="?" /> <!-- 회전 애니메이션 -->
            <TextBlock Text="연결 중..." />
        </StackPanel>
    </Border>

    <TextBlock Text="RTSP 미연결" />
</Grid>
```

---

### RtspTileViewModel.cs
**변경 내용:**
1. `Buffering` 이벤트: 퍼센트 제거 → "버퍼링"으로 단순화
2. `Start()` 메서드: "연결 중: URL" → "연결 중"으로 단순화

**변경 이유:**
- XAML DataTrigger가 정확한 문자열 매칭 필요
- 동적 퍼센트/URL은 Trigger 조건 충족 불가

**Before:**
```csharp
MediaPlayer.Buffering += (_, e) =>
    SetStatus($"버퍼링 {e.Cache:0}%", ...);

SetStatus($"연결 중: {Agent.RtspUrl}", ...);
```

**After:**
```csharp
MediaPlayer.Buffering += (_, e) =>
    SetStatus("버퍼링", ...);

SetStatus("연결 중", ...);
```

---

## 동작 흐름

### 1. RTSP 미연결 상태
```
┌─────────────────────┐
│                     │
│   RTSP 미연결       │  ← 회색 텍스트
│                     │
└─────────────────────┘
```
**조건:** `HasRtsp == false`

---

### 2. 연결 중 / 버퍼링
```
┌─────────────────────┐
│        ?           │  ← 회전 아이콘
│    연결 중...       │  ← 흰색 텍스트
│  (반투명 검은 배경)  │
└─────────────────────┘
```
**조건:** `Rtsp.Status == "연결 중"` 또는 `"버퍼링"`

---

### 3. 재생 중
```
┌─────────────────────┐
│                     │
│   [Quest 화면]      │  ← 깔끔한 미러링
│                     │
└─────────────────────┘
```
**조건:** `Rtsp.Status == "재생 중"`
- 로딩 인디케이터 숨김
- 오버레이 없음

---

## 테스트 시나리오

### ? 시나리오 1: 정상 연결
1. Agent 검색 버튼 클릭
2. **로딩 인디케이터** 표시 (? 회전)
3. 2-3초 후 Quest 화면 재생
4. 로딩 인디케이터 사라짐
5. 깔끔한 미러링 화면 표시

### ? 시나리오 2: 네트워크 지연
1. Agent 검색
2. 로딩 인디케이터 표시
3. "버퍼링" 상태로 전환 (로딩 유지)
4. 연결 완료 후 재생
5. 로딩 인디케이터 사라짐

### ? 시나리오 3: 연결 실패
1. Agent 검색
2. 로딩 인디케이터 표시
3. RTSP 서버 응답 없음
4. "재생 오류" 상태
5. (로딩은 숨겨지지만 화면은 검은색)

### ? 시나리오 4: Agent 없음
1. Meta Device 화면 진입
2. "RTSP 미연결" 메시지 표시
3. 로딩 인디케이터 없음

---

## UI 상태 매트릭스

| Rtsp.Status | HasRtsp | 로딩 표시 | 미연결 메시지 | 비디오 |
|-------------|---------|----------|--------------|--------|
| null | false | ? | ? | ? |
| "연결 중" | true | ? | ? | ? |
| "버퍼링" | true | ? | ? | ?? |
| "재생 중" | true | ? | ? | ? |
| "재생 오류" | true | ? | ? | ? |
| "중지됨" | true | ? | ? | ? |

---

## 장점

### 1. 깔끔한 UI
- 오버레이 없어서 미러링 화면에 집중
- 타일 헤더에 이미 정보 표시됨 (중복 제거)

### 2. 직관적인 로딩 표시
- 사용자가 "연결 중"임을 명확히 인지
- ADB scrcpy 방식과 동일한 UX

### 3. 바인딩 안정성
- VideoView 구조 단순화로 오류 제거
- DataTrigger가 정확한 상태 매칭

---

## 비교: ADB vs RTSP

### ADB (scrcpy)
```
┌─────────────────────┐
│    [로딩 중...]     │  ← scrcpy 프로세스 시작
│                     │
└─────────────────────┘
        ↓
┌─────────────────────┐
│   [Quest 화면]      │  ← scrcpy 창 임베드
│                     │
└─────────────────────┘
```

### RTSP (현재)
```
┌─────────────────────┐
│        ?           │  ← 로딩 인디케이터
│    연결 중...       │
└─────────────────────┘
        ↓
┌─────────────────────┐
│   [Quest 화면]      │  ← VideoView 재생
│                     │
└─────────────────────┘
```

**동일한 UX, 더 나은 성능!**

---

## 추가 개선 가능 사항 (선택)

### 1. 재연결 버튼
연결 실패 시 재시도 버튼 추가:
```xml
<Button Content="재연결" Click="RetryRtsp_Click">
    <Button.Style>
        <Style TargetType="Button">
            <Setter Property="Visibility" Value="Collapsed" />
            <Style.Triggers>
                <DataTrigger Binding="{Binding Rtsp.Status}" Value="재생 오류">
                    <Setter Property="Visibility" Value="Visible" />
                </DataTrigger>
            </Style.Triggers>
        </Style>
    </Button.Style>
</Button>
```

### 2. 자동 재연결
일정 시간 후 자동으로 재시도:
```csharp
MediaPlayer.EncounteredError += async (_, __) =>
{
    SetStatus("재생 오류", Brushes.OrangeRed);
    await Task.Delay(3000);
    Start(); // 자동 재연결
};
```

### 3. 로딩 타임아웃
너무 오래 연결 중이면 오류 표시:
```csharp
var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
// 10초 후에도 연결 안 되면 타임아웃
```

---

## 트러블슈팅

### 로딩 인디케이터가 계속 표시됨
**원인:** RTSP 서버가 응답하지 않음

**해결:**
1. Quest Agent 앱에서 "Start Streaming" 확인
2. `curl http://QuestIP:18080/status` 확인
3. `Test-NetConnection QuestIP -Port 8554` 확인

### 로딩 인디케이터가 아예 안 보임
**원인:** `Rtsp.Status`가 "연결 중"이나 "버퍼링"이 아님

**해결:**
1. Visual Studio Output 창에서 상태 확인
2. RtspTileViewModel의 `SetStatus()` 호출 확인
3. XAML DataTrigger Value 대소문자 일치 확인

### 바인딩 오류 재발
**원인:** VideoView 내부에 컨트롤 추가

**해결:**
- VideoView는 자식 요소를 가지지 않도록 유지
- 오버레이는 Grid의 형제 요소로 배치

---

**? 모든 요구사항 완료!**

1. ? 바인딩 오류 수정
2. ? 로딩 인디케이터 추가 (ADB 방식과 동일)
3. ? 오버레이 정보 제거 (깔끔한 화면)
4. ? 빌드 성공
