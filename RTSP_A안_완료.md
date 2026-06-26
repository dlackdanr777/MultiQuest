# RTSP A안 완료 - Meta Device 타일 RTSP 전환

## 개요
기존 scrcpy 임베딩 방식에서 **RTSP 스트림 직접 재생 방식**으로 완전히 전환했습니다.
- ? Meta Device 타일이 이제 LibVLCSharp.WPF의 `VideoView`로 RTSP 스트림을 직접 표시합니다
- ? scrcpy 프로세스 관리 코드는 `UseRtspMirroring = true` 플래그로 비활성화되었습니다
- ? 빌드 성공 확인 완료

---

## 주요 변경 사항

### 1. MainWindow.xaml.cs

#### 1.1 새로운 using 추가
```csharp
using LibVLCSharp.Shared;
```

#### 1.2 필드 추가
```csharp
private LibVLC _rtspLibVlc;
private const bool UseRtspMirroring = true;  // RTSP 모드 활성화
```

#### 1.3 생성자에 LibVLC 초기화
```csharp
public MainWindow()
{
    EnsureAdbRunning();
    InitializeComponent();

    Core.Initialize();  // LibVLCSharp 초기화
    _rtspLibVlc = new LibVLC(
        "--no-audio",
        "--rtsp-tcp",
        "--network-caching=250",
        "--live-caching=250"
    );

    // ... 나머지 코드
}
```

#### 1.4 scrcpy 자동 시작 비활성화
- `HookDevice(Device d)`: `UseRtspMirroring` 체크로 자동 미러링 시작 방지
- `Device_PropertyChanged`: 연결 상태 변경 시 scrcpy 시작 방지
- `ConnectionCheckTimer_Tick`: scrcpy 프로세스 복구 로직 비활성화

#### 1.5 Agent 검색 및 RTSP 적용
```csharp
private async void AgentSearchButton_Click(object sender, RoutedEventArgs e)
{
    // 1. mDNS + DirectScan으로 Agent 검색
    var agents = await AgentMdnsDiscovery.DiscoverAsync(
        mdnsTimeoutMs: 3000,
        directScanTimeoutMs: 2500);

    // 2. streaming 상태인 Agent만 필터링
    var streamingAgents = agents
        .Where(a => !string.IsNullOrWhiteSpace(a.RtspUrl) &&
                    string.Equals(a.StreamState, "streaming", StringComparison.OrdinalIgnoreCase))
        .ToList();

    // 3. 타일에 RTSP 적용
    ApplyRtspAgentsToDeviceTiles(streamingAgents);
}
```

#### 1.6 RTSP 타일 적용 로직
```csharp
private void ApplyRtspAgentsToDeviceTiles(IReadOnlyList<QuestAgentInfo> agents)
{
    // 1. scrcpy 완전 중지
    StopAllScrcpyMirrors();

    foreach (var agent in agents)
    {
        // 2. 기존 Device 찾기 또는 새로 생성
        var device = FindDeviceByAgentHost(host) ?? new Device { ... };

        // 3. RTSP 정보 업데이트
        device.AgentHost = host;
        device.RtspUrl = agent.RtspUrl;
        device.BatteryLevel = agent.Battery;

        // 4. RtspTileViewModel 생성 및 시작
        if (needNewTile)
        {
            device.Rtsp?.Dispose();
            device.Rtsp = new RtspTileViewModel(_rtspLibVlc, agent);
        }
        device.Rtsp.Start();
    }
}
```

#### 1.7 타일 버튼 동작 변경

**Mirror 버튼:**
```csharp
private void MirrorTile_Click(object sender, RoutedEventArgs e)
{
    if (device.Rtsp == null)
    {
        MessageBox.Show("RTSP 스트림이 연결되지 않았습니다. Agent 검색을 먼저 실행하세요.");
        return;
    }
    device.Rtsp.Start();
}
```

**Refresh 버튼:**
```csharp
private async void RefreshTile_Click(object sender, RoutedEventArgs e)
{
    if (device.Rtsp != null)
    {
        // RTSP 재시작
        device.Rtsp.Stop();
        await Task.Delay(300);
        device.Rtsp.Start();
    }
    else
    {
        // Agent 재검색
        var agents = await AgentMdnsDiscovery.DiscoverAsync(...);
        ApplyRtspAgentsToDeviceTiles(new[] { agent });
    }
}
```

**Stop 버튼:**
```csharp
private void StopTile_Click(object sender, RoutedEventArgs e)
{
    device.Rtsp?.Stop();
}
```

#### 1.8 OnClose 정리
```csharp
private void OnClose(object sender, EventArgs e)
{
    // RTSP 리소스 정리
    foreach (var device in Devices)
    {
        try { device.Rtsp?.Dispose(); } catch { }
        device.Rtsp = null;
    }
    try { _rtspLibVlc?.Dispose(); } catch { }

    // ... 나머지 정리 코드
}
```

---

### 2. Device 클래스 (MainWindow.xaml.cs 내부)

#### 2.1 RTSP 관련 속성 추가
```csharp
public class Device : INotifyPropertyChanged
{
    private RtspTileViewModel _rtsp;
    private string _agentHost;
    private int _agentStatusPort;
    private string _rtspUrl;

    public RtspTileViewModel Rtsp
    {
        get => _rtsp;
        set
        {
            if (_rtsp != value)
            {
                _rtsp = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasRtsp));
            }
        }
    }

    public bool HasRtsp => Rtsp != null;
    public string AgentHost { get; set; }
    public int AgentStatusPort { get; set; }
    public string RtspUrl { get; set; }
    public bool IsAgentOnly => string.Equals(Status, "AgentOnly", StringComparison.OrdinalIgnoreCase);
}
```

**중요:** `IsConnected`는 **ADB 연결 상태**를 나타내므로 그대로 유지합니다.
- Agent-only 기기를 `Connected`로 처리하면 ADB 명령이 실패할 수 있습니다.

---

### 3. MainWindow.xaml

#### 3.1 LibVLCSharp 네임스페이스 추가
```xml
<Window x:Class="MultiQuest_Management.MainWindow"
        xmlns:vlc="clr-namespace:LibVLCSharp.WPF;assembly=LibVLCSharp.WPF"
        ...>
```

#### 3.2 타일 미러 영역 교체
**기존 (scrcpy 호스트):**
```xml
<Border x:Name="PART_MirrorHost"
        Style="{StaticResource MirrorSurface}"/>
```

**변경 (RTSP VideoView):**
```xml
<Border x:Name="PART_MirrorHost"
        Background="#111111"
        ClipToBounds="True">
    <Grid>
        <!-- RTSP 비디오 재생 -->
        <vlc:VideoView MediaPlayer="{Binding Rtsp.MediaPlayer}">
            <Grid IsHitTestVisible="False">
                <!-- 상단 오버레이: 기기명, Agent 호스트, 배터리 -->
                <Border VerticalAlignment="Top"
                        Background="#AA000000"
                        Padding="8">
                    <StackPanel>
                        <TextBlock Text="{Binding Name}"
                                   Foreground="White"
                                   FontSize="15"
                                   FontWeight="SemiBold" />
                        <TextBlock Foreground="#CCCCCC" FontSize="12">
                            <Run Text="Agent: " />
                            <Run Text="{Binding AgentHost}" />
                            <Run Text=" / Battery " />
                            <Run Text="{Binding BatteryText}" />
                        </TextBlock>
                    </StackPanel>
                </Border>

                <!-- 하단 오버레이: RTSP 상태 -->
                <Border VerticalAlignment="Bottom"
                        Background="#AA000000"
                        Padding="8">
                    <TextBlock Text="{Binding Rtsp.Status}"
                               Foreground="{Binding Rtsp.StatusBrush}"
                               FontSize="13"
                               TextWrapping="Wrap" />
                </Border>
            </Grid>
        </vlc:VideoView>

        <!-- RTSP 미연결 메시지 -->
        <TextBlock Text="RTSP 미연결"
                   Foreground="#AAAAAA"
                   HorizontalAlignment="Center"
                   VerticalAlignment="Center"
                   FontSize="18">
            <TextBlock.Style>
                <Style TargetType="TextBlock">
                    <Setter Property="Visibility" Value="Collapsed" />
                    <Style.Triggers>
                        <DataTrigger Binding="{Binding HasRtsp}" Value="False">
                            <Setter Property="Visibility" Value="Visible" />
                        </DataTrigger>
                    </Style.Triggers>
                </Style>
            </TextBlock.Style>
        </TextBlock>
    </Grid>
</Border>
```

---

## 테스트 순서

### 1. Android Agent 준비
```bash
# Quest에서 Agent 앱 실행 확인
adb shell "ps | grep multiquest"

# HTTP 서버 포트 확인
curl http://192.168.0.243:18080/status
```

### 2. Quest에서 화면 송출 시작
Agent 앱에서 "Start Streaming" 버튼 클릭

### 3. WPF 실행 및 테스트
1. MultiQuest Management 실행
2. **Agent 검색** 버튼 클릭
3. Meta Device 화면에 RTSP 타일 표시 확인
4. **기존 scrcpy 창이 뜨지 않는지** 확인 ?
5. 타일 내부에 RTSP 스트림 재생 확인
6. 배터리, 상태 정보 오버레이 확인

### 4. PC 네트워크 확인
```powershell
# RTSP 포트 연결 테스트
Test-NetConnection 192.168.0.243 -Port 8554

# HTTP 상태 확인
Invoke-RestMethod http://192.168.0.243:18080/status
```

---

## 구조 비교

### 기존 (scrcpy)
```
Devices → PART_MirrorHost → ScrcpyEmbedder → scrcpy.exe 창 임베드
```

### 변경 (RTSP)
```
Devices → PART_MirrorHost → LibVLCSharp VideoView → rtsp://QuestIP:8554/live
```

---

## 주의 사항

### 1. ADB는 완전히 제거하지 않음
- 앱 실행 (`StartApp`)
- 배터리 조회
- 기기 제어
- 기타 명령어

위 기능들은 여전히 ADB를 사용하므로 ADB 코드는 그대로 유지됩니다.

### 2. scrcpy 관련 코드는 남아있음
- `UseRtspMirroring = false`로 설정하면 기존 scrcpy 방식으로 되돌릴 수 있습니다
- 완전히 제거하려면 나중에 별도 정리 작업이 필요합니다

### 3. RtspMirrorWindow는 사용하지 않음
- `RtspMirrorWindow.xaml`
- `RtspMirrorWindow.xaml.cs`

이 파일들은 이전 접근법에서 만든 별도 창 방식이며, A안에서는 사용하지 않습니다.
삭제해도 되지만, 지금은 빌드 안정화가 우선이므로 남겨둡니다.

---

## 트러블슈팅

### Agent를 찾지 못하는 경우
1. Quest와 PC가 같은 네트워크(공유기)인지 확인
2. PC 방화벽에서 18080, 8554 포트 허용 확인
3. 공유기 설정에서 **AP Isolation / Client Isolation** 비활성화 확인
4. `curl http://QuestIP:18080/status`로 HTTP 서버 응답 확인

### RTSP 스트림이 재생되지 않는 경우
1. Quest Agent 앱에서 "Start Streaming" 버튼을 눌렀는지 확인
2. `Test-NetConnection QuestIP -Port 8554`로 RTSP 포트 연결 확인
3. VLC Player로 직접 테스트:
   ```
   vlc rtsp://192.168.0.243:8554/live
   ```
4. Android 로그 확인:
   ```
   adb logcat | grep -i "rtsp\|mediaprojection\|capture"
   ```

### 빌드 오류가 발생하는 경우
1. NuGet 패키지 복원:
   ```
   dotnet restore
   ```
2. 필수 패키지 확인:
   - `LibVLCSharp.WPF` (3.10.0)
   - `VideoLAN.LibVLC.Windows` (3.0.23.1)

---

## 다음 단계 (선택 사항)

### 1. scrcpy 코드 완전 제거
`UseRtspMirroring`으로 분기된 코드를 정리하여 RTSP 전용으로 단순화

### 2. RTSP 자동 재연결
네트워크 끊김 시 자동으로 재연결하는 로직 추가

### 3. 다중 해상도/비트레이트 지원
Agent에서 여러 품질 옵션 제공 및 PC에서 선택 가능하도록 개선

### 4. RTSP 딜레이 최적화
현재 `--network-caching=250`이지만, 환경에 따라 조정 가능

---

## 참고 파일

- `MainWindow.xaml.cs`: RTSP 통합 메인 로직
- `MainWindow.xaml`: RTSP VideoView UI
- `RtspTileViewModel.cs`: RTSP 재생 상태 관리
- `AgentMdnsDiscovery.cs`: mDNS + DirectScan 검색
- `QuestAgentInfo.cs`: Agent 정보 모델

---

**? A안 전환 완료!**
