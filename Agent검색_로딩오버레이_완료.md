# Agent 검색 로딩 오버레이 추가 완료

## 변경 사항

### 1. 전체 화면 로딩 오버레이 추가 ?

**요구사항:** Agent 검색 버튼 클릭 시 ADB 검색처럼 전체 화면 로딩 표시

**구현:**
- 기존 `ShowSearchOverlay()` / `HideSearchOverlay()` 메서드 활용
- Agent 검색 시작 시 오버레이 표시
- 검색 완료/실패 시 오버레이 숨김

---

### 2. 타일 내부 로딩 제거 ?

**이유:** 
- 전체 화면 로딩이 이미 표시되므로 타일 내부 로딩은 불필요
- UI 중복 제거
- 더 간결한 사용자 경험

---

## 코드 변경

### MainWindow.xaml.cs - AgentSearchButton_Click()

**변경 전:**
```csharp
private async void AgentSearchButton_Click(object sender, RoutedEventArgs e)
{
    PlayClickSound();
    this.IsEnabled = false;

    try
    {
        var agents = await AgentMdnsDiscovery.DiscoverAsync(...);
        // ... 처리 ...
    }
    finally
    {
        this.IsEnabled = true;
    }
}
```

**변경 후:**
```csharp
private async void AgentSearchButton_Click(object sender, RoutedEventArgs e)
{
    PlayClickSound();
    ShowSearchOverlay("MultiQuest Agent 검색 중...");  // ? 로딩 시작

    try
    {
        var agents = await AgentMdnsDiscovery.DiscoverAsync(...);

        if (agents.Count == 0)
        {
            HideSearchOverlay();  // ? 실패 시 숨김
            MessageBox.Show(...);
            return;
        }

        // ... 처리 ...

        HideSearchOverlay();  // ? 성공 시 숨김
        MessageBox.Show("RTSP 미러링 타일 적용 완료...");
    }
    catch (Exception ex)
    {
        HideSearchOverlay();  // ? 오류 시 숨김
        MessageBox.Show($"Agent 검색 중 오류: {ex.Message}");
    }
}
```

---

### MainWindow.xaml - 타일 내부 로딩 제거

**변경 전:**
```xml
<Grid>
    <vlc:VideoView MediaPlayer="{Binding Rtsp.MediaPlayer}" />

    <!-- 타일 내부 로딩 (? 회전 애니메이션) -->
    <Border>
        <StackPanel>
            <TextBlock Text="?" />
            <TextBlock Text="연결 중..." />
        </StackPanel>
    </Border>

    <TextBlock Text="RTSP 미연결" />
</Grid>
```

**변경 후:**
```xml
<Grid>
    <vlc:VideoView MediaPlayer="{Binding Rtsp.MediaPlayer}" />

    <!-- 미연결 메시지만 유지 -->
    <TextBlock Text="RTSP 미연결" />
</Grid>
```

---

## 동작 흐름

### 1. Agent 검색 시작
```
사용자: Agent 검색 버튼 클릭
    ↓
전체 화면 오버레이 표시
┌─────────────────────────┐
│         ??               │
│  MultiQuest Agent      │
│     검색 중...          │
│                        │
│  (회전 애니메이션)      │
└─────────────────────────┘
```

### 2. 검색 진행 중
- 화면 전체가 오버레이로 덮임
- 사이드 메뉴 버튼 비활성화
- 회전하는 스피너 표시
- "MultiQuest Agent 검색 중..." 텍스트

### 3. 검색 완료
```
Agent 발견
    ↓
오버레이 숨김 (HideSearchOverlay)
    ↓
RTSP 타일 적용
    ↓
성공 메시지 박스
```

### 4. 검색 실패
```
Agent 없음
    ↓
오버레이 숨김 (HideSearchOverlay)
    ↓
실패 메시지 박스
```

---

## UI 상태 비교

### 이전 (타일 내부 로딩)
```
┌──────┐ ┌──────┐ ┌──────┐
│ ?   │ │      │ │      │  ← 각 타일마다 개별 로딩
│ 연결 │ │Quest │ │Quest │
│ 중.. │ │ 화면 │ │ 화면 │
└──────┘ └──────┘ └──────┘
```
**문제:**
- 검색 중에도 타일별로 개별 로딩
- 사용자가 언제 완료되는지 불명확

---

### 현재 (전체 화면 로딩)
```
Agent 검색 버튼 클릭
    ↓
┌─────────────────────────┐
│   전체 화면 오버레이     │
│         ??               │
│  MultiQuest Agent      │
│     검색 중...          │
└─────────────────────────┘
    ↓
검색 완료
    ↓
┌──────┐ ┌──────┐ ┌──────┐
│Quest │ │Quest │ │Quest │  ← 바로 재생 시작
│ 화면 │ │ 화면 │ │ 화면 │
└──────┘ └──────┘ └──────┘
```

**장점:**
- ? 명확한 검색 진행 표시
- ? ADB 검색과 동일한 UX
- ? 검색 완료 후 즉시 재생
- ? 타일별 개별 로딩 불필요

---

## ShowSearchOverlay() 동작

### 자동으로 처리되는 것들:
1. **화면 오버레이 표시**
   - `SearchOverlay.Visibility = Visible`

2. **사이드 버튼 비활성화**
   - Meta Device 버튼
   - XR Experience 버튼
   - XR Coding 버튼
   - XR English 버튼

3. **스피너 애니메이션 시작**
   - 회전하는 아이콘
   - 16ms마다 6도씩 회전

4. **기존 scrcpy 창 숨김**
   - `ScrcpyEmbedder.HideAll()`

---

## HideSearchOverlay() 동작

### 자동으로 처리되는 것들:
1. **오버레이 숨김**
   - `SearchOverlay.Visibility = Collapsed`

2. **사이드 버튼 활성화**
   - 모든 메뉴 버튼 다시 활성화

3. **스피너 정지**
   - 애니메이션 타이머 중지

4. **Meta Device 패널일 경우**
   - 보류 중이던 Attach 처리
   - scrcpy 창 복원

---

## 테스트 시나리오

### ? 시나리오 1: 정상 검색
1. Agent 검색 버튼 클릭
2. **전체 화면 오버레이 표시** (?? 회전)
3. "MultiQuest Agent 검색 중..." 텍스트
4. 2-3초 후 Agent 발견
5. 오버레이 숨김
6. RTSP 타일 표시
7. 성공 메시지 박스

### ? 시나리오 2: Agent 없음
1. Agent 검색 버튼 클릭
2. 전체 화면 오버레이 표시
3. Agent 검색 타임아웃
4. 오버레이 숨김
5. "MultiQuest Agent를 찾지 못했습니다" 메시지

### ? 시나리오 3: 네트워크 오류
1. Agent 검색 버튼 클릭
2. 전체 화면 오버레이 표시
3. 예외 발생 (catch)
4. 오버레이 숨김
5. "Agent 검색 중 오류가 발생했습니다" 메시지

### ? 시나리오 4: Streaming 상태 아님
1. Agent 검색 버튼 클릭
2. 전체 화면 오버레이 표시
3. Agent 발견하지만 streamState != "streaming"
4. 오버레이 숨김
5. "Agent는 찾았지만 streaming 상태인 기기가 없습니다" 메시지

---

## 비교: ADB vs Agent 검색

### ADB 검색 (mDNS)
```
[DEVICE SEARCH] 버튼
    ↓
ShowSearchOverlay("mDNS 디바이스 탐색 중...")
    ↓
mDNS 검색 (3번 시도)
    ↓
HideSearchOverlay()
    ↓
Devices에 추가
```

### Agent 검색 (현재)
```
[Agent 검색] 버튼
    ↓
ShowSearchOverlay("MultiQuest Agent 검색 중...")
    ↓
AgentMdnsDiscovery (mDNS + DirectScan)
    ↓
HideSearchOverlay()
    ↓
RTSP 타일 적용
```

**동일한 UX 패턴!** ?

---

## 타이밍

### Agent 검색 시간
- **mDNS 검색:** 3000ms (3초)
- **DirectScan:** 2500ms (2.5초)
- **합계:** ~3초 (병렬 처리)

### 오버레이 표시 시간
- 검색 시작 ~ 완료/실패까지
- 평균 3초 내외

---

## 예외 처리

### catch 블록 추가
```csharp
catch (Exception ex)
{
    HideSearchOverlay();  // 항상 오버레이 숨김
    MessageBox.Show(
        $"Agent 검색 중 오류가 발생했습니다:\n{ex.Message}",
        "오류",
        MessageBoxButton.OK,
        MessageBoxImage.Error);
}
```

**중요:** 어떤 경로로든 `HideSearchOverlay()`가 호출되도록 보장

---

## 장점

### 1. 일관된 UX
- ADB 검색과 동일한 로딩 방식
- 사용자가 학습할 필요 없음

### 2. 명확한 피드백
- 전체 화면 오버레이로 "검색 중"임을 강하게 표시
- 언제 완료될지 예측 가능

### 3. UI 단순화
- 타일별 개별 로딩 제거
- 검색 완료 후 즉시 재생 가능

### 4. 안정성
- 모든 경로에서 오버레이 정리 보장
- 예외 발생 시에도 UI 복구

---

## 주의사항

### 1. 오버레이 정리 필수
모든 종료 경로에서 `HideSearchOverlay()` 호출:
- ? 성공 시
- ? 실패 시 (Agent 없음)
- ? 예외 시
- ? streaming 없음

### 2. MessageBox 순서
```csharp
HideSearchOverlay();           // 1. 먼저 오버레이 숨김
MessageBox.Show("메시지...");   // 2. 그 다음 메시지 표시
```

이유: MessageBox가 모달이므로 오버레이 아래에 표시되면 안 됨

### 3. 비동기 취소
현재는 취소 기능이 없지만, 필요하면:
```csharp
private CancellationTokenSource _agentSearchCts;

// 검색 시작
_agentSearchCts = new CancellationTokenSource();
var agents = await AgentMdnsDiscovery.DiscoverAsync(
    mdnsTimeoutMs: 3000,
    directScanTimeoutMs: 2500,
    cancellationToken: _agentSearchCts.Token);
```

---

## 추가 개선 가능 사항 (선택)

### 1. 진행률 표시
```csharp
SearchPercentText.Text = "50%";  // mDNS 완료
SearchPercentText.Text = "100%"; // DirectScan 완료
```

### 2. 검색 취소 버튼
오버레이에 "취소" 버튼 추가:
```xml
<Button Content="취소" Click="CancelAgentSearch_Click" />
```

### 3. 발견된 Agent 실시간 표시
```csharp
SearchStatusText.Text = $"Agent 발견: {agents.Count}개";
```

---

## 트러블슈팅

### 오버레이가 사라지지 않음
**원인:** `HideSearchOverlay()` 호출 누락

**해결:** 모든 종료 경로 확인
```csharp
if (agents.Count == 0)
{
    HideSearchOverlay();  // ?
    return;
}
```

### 오버레이 아래 메시지 박스
**원인:** `HideSearchOverlay()` 전에 `MessageBox.Show()` 호출

**해결:** 순서 변경
```csharp
HideSearchOverlay();        // 1. 먼저
MessageBox.Show(...);       // 2. 나중에
```

### 검색 중 다른 버튼 클릭 가능
**원인:** `ShowSearchOverlay()`가 사이드 버튼만 비활성화

**해결:** 이미 구현됨 - `ShowSearchOverlay()`가 자동으로 처리

---

**? 전체 화면 로딩 오버레이 완료!**

이제 Agent 검색이 ADB 검색과 동일한 UX로 동작합니다.
