# RTSP 동적 품질 관리 시스템 완료

## 개요
16대 동시 미러링을 지원하기 위해 **네트워크와 성능에 따라 자동으로 품질을 조절**하는 시스템을 구현했습니다.

---

## 핵심 기능

### 1. 품질 레벨 자동 조정 ?
- **5가지 품질 레벨**: Ultra → High → Medium → Low → Minimal
- **동시 스트림 수에 따른 초기 품질 결정**
- **버퍼링 발생률에 따른 동적 품질 조정**
- **5초마다 자동 모니터링**

### 2. 품질 레벨별 설정

| 레벨 | 권장 대수 | 버퍼 | 프레임 드롭 | 디코딩 최적화 |
|------|----------|------|------------|--------------|
| **Ultra** | 1~4대 | 250ms | 없음 | 하드웨어 |
| **High** | 5~8대 | 500ms | 지연 프레임 | 하드웨어 |
| **Medium** | 9~12대 | 1000ms | 활성화 | 하드웨어 |
| **Low** | 13~16대 | 2000ms | 적극적 | 빠른 디코딩 |
| **Minimal** | 17대+ | 3000ms | 최대 | 품질 희생 |

---

## 구현 파일

### 1. RtspQualityManager.cs ? 신규

**역할:** 모든 RTSP 스트림의 성능을 모니터링하고 품질 조정 결정

**주요 메서드:**
```csharp
// 스트림 등록 (초기 품질 결정)
QualityLevel RegisterStream(string streamId, int activeStreamCount)

// 버퍼링 이벤트 기록
void RecordBuffering(string streamId)

// 품질 레벨에 맞는 VLC 옵션 반환
static string[] GetVlcOptions(QualityLevel quality)

// 스트림 종료
void UnregisterStream(string streamId)
```

**동작:**
- 5초마다 각 스트림의 버퍼링 비율 계산
- 버퍼링 10% 이상 → 품질 다운
- 버퍼링 2% 이하 → 품질 업 (동시 스트림 수 고려)

---

### 2. RtspTileViewModel.cs 수정

**변경 사항:**
```csharp
// 생성자에 품질 관리자 추가
public RtspTileViewModel(
    LibVLC libVlc, 
    QuestAgentInfo agent,
    RtspQualityManager qualityManager)  // ? 추가

// Start에 동시 스트림 수 전달
public void Start(int activeStreamCount)  // ? 수정
{
    // 품질 레벨 결정
    _currentQuality = _qualityManager.RegisterStream(
        Agent.Host, 
        activeStreamCount);

    // 품질에 맞는 VLC 옵션 적용
    var options = RtspQualityManager.GetVlcOptions(_currentQuality);
}
```

**버퍼링 이벤트 기록:**
```csharp
MediaPlayer.Buffering += (_, e) =>
{
    _qualityManager.RecordBuffering(Agent.Host);  // ? 추가
    SetStatus("버퍼링", Brushes.Khaki);
};
```

---

### 3. MainWindow.xaml.cs 수정

**품질 관리자 생성:**
```csharp
public MainWindow()
{
    // ...
    _rtspLibVlc = new LibVLC(...);
    _rtspQualityManager = new RtspQualityManager();  // ? 추가
}
```

**정리:**
```csharp
private void OnClose(object sender, EventArgs e)
{
    // ...
    try { _rtspQualityManager?.Dispose(); } catch { }  // ? 추가
}
```

**스트림 시작 시 품질 적용:**
```csharp
private void ApplyRtspAgentsToDeviceTiles(IReadOnlyList<QuestAgentInfo> agents)
{
    int activeStreamCount = 0;

    // 1. 타일 생성
    foreach (var agent in agents)
    {
        device.Rtsp = new RtspTileViewModel(
            _rtspLibVlc, 
            agent, 
            _rtspQualityManager);  // ? 품질 관리자 전달

        activeStreamCount++;
    }

    // 2. 스트림 시작 (동시 스트림 수 전달)
    foreach (var agent in agents)
    {
        device.Rtsp.Start(activeStreamCount);  // ? 수 전달
    }
}
```

---

### 4. RtspMirrorWindow.xaml.cs 수정

**품질 관리자 생성:**
```csharp
public RtspMirrorWindow(IEnumerable<QuestAgentInfo> agents)
{
    _libVlc = new LibVLC(...);
    _qualityManager = new RtspQualityManager();  // ? 추가

    foreach (var agent in agentList)
    {
        Tiles.Add(new RtspTileViewModel(
            _libVlc, 
            agent, 
            _qualityManager));  // ? 전달
    }
}

private void RtspMirrorWindow_Loaded(object sender, RoutedEventArgs e)
{
    int count = Tiles.Count;
    foreach (var tile in Tiles)
        tile.Start(count);  // ? 동시 스트림 수 전달
}
```

---

## 품질 레벨별 VLC 옵션

### Ultra (최고 품질) - 1~4대
```
:rtsp-tcp
:network-caching=250
:live-caching=250
:clock-jitter=0
:no-drop-late-frames
:no-skip-frames
```
**특징:** 프레임 드롭 없음, 최소 버퍼, 최고 품질

---

### High (고품질) - 5~8대
```
:rtsp-tcp
:network-caching=500
:live-caching=500
:clock-jitter=0
:drop-late-frames
:no-skip-frames
```
**특징:** 지연 프레임만 드롭, 중간 버퍼

---

### Medium (중품질) - 9~12대
```
:rtsp-tcp
:network-caching=1000
:live-caching=1000
:clock-jitter=50
:drop-late-frames
:skip-frames
```
**특징:** 프레임 스킵 활성화, 중간 버퍼

---

### Low (저품질) - 13~16대
```
:rtsp-tcp
:network-caching=2000
:live-caching=2000
:clock-jitter=100
:drop-late-frames
:skip-frames
:avcodec-fast
```
**특징:** 적극적 프레임 드롭, 빠른 디코딩

---

### Minimal (최소 품질) - 17대+
```
:rtsp-tcp
:network-caching=3000
:live-caching=3000
:clock-jitter=200
:drop-late-frames
:skip-frames
:avcodec-fast
:avcodec-skiploopfilter=4
:avcodec-skipframe=1
```
**특징:** 최대 프레임 드롭, 품질 희생, 안정성 우선

---

## 동작 흐름

### 1. Agent 검색 시 (초기 품질 결정)
```
Agent 검색 완료 (예: 8대 발견)
    ↓
activeStreamCount = 8
    ↓
각 스트림 생성 시:
  RegisterStream(host, 8)
    ↓
8대 → High 품질 선택
    ↓
VLC 옵션 적용:
  network-caching=500ms
  drop-late-frames 활성화
```

---

### 2. 5초 후 모니터링 (동적 품질 조정)
```
[모니터링 타이머]
    ↓
각 스트림의 버퍼링 비율 계산
    ↓
스트림 A: 버퍼링 15% → 품질 다운 (High → Medium)
스트림 B: 버퍼링 1% → 품질 유지 (High)
스트림 C: 버퍼링 0% → 품질 업 시도 (High → Ultra)
                       ↓ (하지만 8대 제한)
                   품질 유지 (High)
```

---

### 3. 버퍼링 이벤트 발생 시
```
VLC: Buffering 이벤트 발생
    ↓
RtspTileViewModel: RecordBuffering() 호출
    ↓
RtspQualityManager: 버퍼링 카운트 증가
    ↓
5초 후 모니터링에서 반영
```

---

## 성능 임계값

### 버퍼링 임계값
```csharp
const double BufferingThreshold_Upgrade = 0.02;    // 2% 이하 → 품질 업
const double BufferingThreshold_Downgrade = 0.10;  // 10% 이상 → 품질 다운
```

**예시:**
- 10초 동안 버퍼링 2회 → 버퍼링률 0.2/초 = 20%
- → 품질 다운 (예: Medium → Low)

---

### 동시 스트림 수 제한
```csharp
const int MaxConcurrentStreams_Ultra = 4;    // Ultra: 최대 4대
const int MaxConcurrentStreams_High = 8;     // High: 최대 8대
const int MaxConcurrentStreams_Medium = 12;  // Medium: 최대 12대
const int MaxConcurrentStreams_Low = 16;     // Low: 최대 16대
```

---

## 실시간 로그

### Visual Studio Output 창
```
[RTSP Quality] 192.168.0.243: High → Medium (버퍼링률: 0.150/s, 활성: 10)
[RTSP Quality] 192.168.0.244: Medium → Low (버퍼링률: 0.120/s, 활성: 14)
[RTSP Quality] 192.168.0.245: Low → Medium (버퍼링률: 0.015/s, 활성: 12)
```

---

## 사용 시나리오

### 시나리오 1: 4대 미러링 (최고 품질)
```
Agent 검색: 4대
    ↓
초기 품질: Ultra (250ms 버퍼)
    ↓
네트워크 안정: 버퍼링 거의 없음
    ↓
품질 유지: Ultra
```
**결과:** 최고 화질, 최소 지연, 부드러운 재생

---

### 시나리오 2: 12대 미러링 (중간 품질)
```
Agent 검색: 12대
    ↓
초기 품질: Medium (1000ms 버퍼)
    ↓
일부 기기 버퍼링 발생 (15%)
    ↓
해당 기기만 품질 다운: Medium → Low
    ↓
다른 기기는 Medium 유지
```
**결과:** 개별 기기별 최적화

---

### 시나리오 3: 16대 미러링 (저품질)
```
Agent 검색: 16대
    ↓
초기 품질: Low (2000ms 버퍼)
    ↓
일부 기기 심한 버퍼링 (20%)
    ↓
품질 다운: Low → Minimal (3000ms 버퍼)
    ↓
버퍼링 안정화
```
**결과:** 품질 희생, 안정성 확보

---

### 시나리오 4: 8대 → 4대로 감소
```
Agent 검색: 8대 (High 품질)
    ↓
일부 기기 재부팅/종료
    ↓
Agent 재검색: 4대
    ↓
품질 업그레이드: High → Ultra
```
**결과:** 동적으로 품질 향상

---

## 장점

### 1. 자동 최적화
- 사용자 개입 없이 자동으로 품질 조정
- 네트워크 상태에 실시간 대응

### 2. 대규모 미러링 지원
- 최대 16대 동시 미러링 가능
- 품질 희생을 통한 안정성 확보

### 3. 개별 최적화
- 각 기기별 독립적인 품질 레벨
- 일부 기기 문제가 전체에 영향 안 줌

### 4. 성능 모니터링
- 5초마다 자동 평가
- 버퍼링 패턴 분석

---

## 제한사항

### 1. 10초 대기 시간
```csharp
if (elapsed < 10) continue; // 최소 10초 동안 데이터 수집
```
**이유:** 초기 연결 불안정 구간 무시, 정확한 통계 수집

### 2. 품질 다운만 즉시 반응
- 버퍼링 10% 이상 → 즉시 품질 다운
- 버퍼링 2% 이하 → 천천히 품질 업

**이유:** 안정성 우선, 급격한 품질 변동 방지

### 3. 동시 스트림 수 제한
- Ultra: 4대 제한
- High: 8대 제한

**이유:** 하드웨어 리소스 보호

---

## 테스트 방법

### 1. 1~4대 테스트 (Ultra)
```
1. Agent 검색 (4대)
2. Output 창 확인:
   - 초기 품질: Ultra (250ms 버퍼)
3. 미러링 품질 확인: 최고 화질
```

### 2. 8대 테스트 (High)
```
1. Agent 검색 (8대)
2. Output 창 확인:
   - 초기 품질: High (500ms 버퍼)
3. 네트워크 부하 추가 (다른 앱 스트리밍)
4. 15초 후 Output 확인:
   - "High → Medium" 로그 확인
```

### 3. 16대 테스트 (Low)
```
1. Agent 검색 (16대)
2. Output 창 확인:
   - 초기 품질: Low (2000ms 버퍼)
3. 버퍼링 발생 관찰
4. 자동 품질 다운 확인:
   - "Low → Minimal" 로그
```

---

## 트러블슈팅

### 품질이 계속 다운됨
**원인:** 네트워크 대역폭 부족

**해결:**
1. 동시 스트림 수 줄이기
2. WiFi 5GHz 사용 확인
3. 공유기 업그레이드

### 품질이 업그레이드 안 됨
**원인:** 동시 스트림 수 제한

**해결:**
```
8대 연결 → High 제한
일부 기기 종료 → Agent 재검색
4대로 줄면 → Ultra로 업그레이드
```

### Output 창에 로그 없음
**원인:** 10초 미만 경과

**해결:** 15초 이상 대기

---

## 향후 개선 가능 사항

### 1. 해상도 동적 조정
현재는 버퍼만 조정, Android Agent에서 해상도도 조정 가능

### 2. 프레임 레이트 조정
30fps → 20fps → 15fps 단계별 조정

### 3. 네트워크 대역폭 측정
실제 네트워크 속도 측정 후 품질 결정

### 4. GPU 사용률 모니터링
GPU 과부하 시 품질 다운

---

## 요약

| 항목 | 값 |
|------|-----|
| 지원 품질 레벨 | 5단계 (Ultra ~ Minimal) |
| 최대 동시 스트림 | 16대 (Minimal 품질) |
| 권장 동시 스트림 | 8대 (High 품질) |
| 최고 품질 지원 | 4대 (Ultra 품질) |
| 모니터링 주기 | 5초 |
| 품질 조정 방식 | 자동 (버퍼링 기반) |

---

**? 16대 동시 미러링 지원 완료!**
**? 네트워크/성능 기반 동적 품질 조정 완료!**
**? 빌드 성공!**
