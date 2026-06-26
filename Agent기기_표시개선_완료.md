# Agent 기기 표시 개선 완료

## 문제 해결

### 1. ? 빨간 원 (연결 안 됨 표시)
**원인:** `IsConnected`가 ADB 연결 상태만 확인  
**해결:** Agent 있으면 연결됨으로 표시

### 2. ? 이름이 "Quest 3"
**원인:** Settings에서 설정한 이름 적용 안 됨  
**해결:** 시리얼 넘버 매핑으로 설정된 이름 우선 적용

### 3. ? 충전 표시 안 나타남
**원인:** Android Agent에서 `isCharging` 정보 안 보냄  
**해결:** WPF 준비 완료, Android 수정 필요

---

## WPF 변경 사항

### 1. IsConnected 로직 개선

**변경 전:**
```csharp
public bool IsConnected => 
    string.Equals(Status, "Connected", StringComparison.OrdinalIgnoreCase);
```
- ADB 연결만 "연결됨"
- Agent 전용 기기는 빨간 원 ??

**변경 후:**
```csharp
public bool IsConnected => 
    string.Equals(Status, "Connected", StringComparison.OrdinalIgnoreCase) ||
    !string.IsNullOrWhiteSpace(AgentHost);  // Agent 있으면 연결됨
```
- ADB 연결 또는 Agent 있으면 "연결됨"
- Agent 기기도 초록 원 ??

---

### 2. 이름 설정 우선순위 개선

**변경 전:**
```csharp
if (!string.IsNullOrWhiteSpace(agent.Model) &&
    (string.IsNullOrWhiteSpace(device.Name) || device.Name == device.Serial))
{
    device.Name = agent.Model;
}
```
- Settings에서 설정한 이름 무시
- 항상 "Quest 3" 표시

**변경 후:**
```csharp
// 이름 설정 우선순위:
// 1. Settings에서 설정한 이름 (최우선)
// 2. Agent의 Model 정보
// 3. Serial 또는 Host

if (!string.IsNullOrEmpty(device.Serial) && 
    _serialNameDic.TryGetValue(device.Serial, out var customName))
{
    device.Name = customName;  // Settings 이름 우선
}
else if (!string.IsNullOrWhiteSpace(agent.Model) &&
         (string.IsNullOrWhiteSpace(device.Name) || device.Name == device.Serial || device.Name == host))
{
    device.Name = agent.Model;  // Model 이름
}
else if (string.IsNullOrWhiteSpace(device.Name))
{
    device.Name = !string.IsNullOrEmpty(device.Serial) ? device.Serial : host;  // 기본값
}
```

---

## 동작 시나리오

### 시나리오 1: Settings에서 이름 설정 (최우선)

```
1. Agent 검색
   ↓
2. Agent에서 Serial 받음: "1WMHH81234567890"
   ↓
3. _serialNameDic 확인
   ↓
4. "1WMHH81234567890" → "거실 Quest" 매핑 존재
   ↓
5. device.Name = "거실 Quest" ?
   ↓
6. 타일 표시:
┌──────────────────────────┐
│ ?? 거실 Quest            │  ← Settings 이름
│ 95% ?                   │  ← 충전 표시 (Agent 수정 후)
└──────────────────────────┘
```

---

### 시나리오 2: Settings 이름 없음 (Model 사용)

```
1. Agent 검색
   ↓
2. Agent에서 받음:
   - Serial: "1WMHH81234567890"
   - Model: "Quest 3"
   ↓
3. _serialNameDic 확인 → 없음
   ↓
4. device.Name = "Quest 3" (Model)
   ↓
5. 타일 표시:
┌──────────────────────────┐
│ ?? Quest 3               │  ← Model 이름
│ 95%                      │
└──────────────────────────┘
```

---

### 시나리오 3: Settings에서 나중에 이름 설정

```
1. 초기 상태: "Quest 3" 표시
   ↓
2. Settings 창 열기
   ↓
3. Serial "1WMHH81234567890" → "안방 Quest" 설정
   ↓
4. 저장 클릭
   ↓
5. OnChangeSerialName() 호출
   ↓
6. device.Name = "안방 Quest" 업데이트
   ↓
7. 즉시 UI 반영 ?
┌──────────────────────────┐
│ ?? 안방 Quest            │  ← 변경된 이름
│ 95%                      │
└──────────────────────────┘
```

---

## 연결 상태 표시

### 변경 전

| 상태 | IsConnected | 표시 |
|------|-------------|------|
| ADB 연결 | ? True | ?? 초록 원 |
| Agent만 | ? False | ?? 빨간 원 |
| 둘 다 없음 | ? False | ?? 빨간 원 |

**문제:** Agent로 정상 작동해도 빨간 원

---

### 변경 후

| 상태 | IsConnected | 표시 |
|------|-------------|------|
| ADB 연결 | ? True | ?? 초록 원 |
| Agent 있음 | ? True | ?? 초록 원 |
| 둘 다 있음 | ? True | ?? 초록 원 |
| 둘 다 없음 | ? False | ?? 빨간 원 |

**결과:** Agent 기기도 초록 원 표시

---

## XAML 바인딩

**연결 상태 표시 (842-853줄):**
```xml
<Ellipse Width="8" Height="8" Margin="0,0,5,0">
    <Ellipse.Style>
        <Style TargetType="Ellipse">
            <Setter Property="Fill" Value="{StaticResource Bad}"/>  <!-- 기본: 빨간색 -->
            <Style.Triggers>
                <DataTrigger Binding="{Binding IsConnected}" Value="True">
                    <Setter Property="Fill" Value="{StaticResource Ok}"/>  <!-- 초록색 -->
                </DataTrigger>
            </Style.Triggers>
        </Style>
    </Ellipse.Style>
</Ellipse>
```

**이름 표시 (854줄):**
```xml
<TextBlock Text="{Binding Name}" 
           Foreground="{StaticResource Text}"
           FontWeight="SemiBold" 
           FontSize="11"/>
```

**배터리 표시 (861줄):**
```xml
<TextBlock Text="{Binding BatteryText}"
           Foreground="{StaticResource Text}"
           HorizontalAlignment="Center" 
           VerticalAlignment="Center"
           FontSize="9" 
           FontWeight="Bold"/>
```

---

## 충전 표시 (Android 수정 필요)

### WPF 준비 완료

**Device.cs:**
```csharp
public string BatteryText
{
    get
    {
        if (BatteryLevel < 0) return "N/A";
        string text = $"{BatteryLevel}%";
        if (IsCharging) text += " ?";  // 충전 중이면 번개 표시
        return text;
    }
}
```

**UpdateBatteryStatus:**
```csharp
var status = await AgentApi.GetStatusAsync(device.AgentHost, device.AgentStatusPort);
if (status != null)
{
    if (status.Battery >= 0)
        level = status.Battery;
    isCharging = status.IsCharging;  // ? 받아옴
}

d.IsCharging = isCharging;  // ? UI 업데이트
```

---

### Android 수정 필요

**/status API 응답에 추가:**
```json
{
  "battery": 95,
  "isCharging": true,  // ← 추가 필요
  "chargingStatus": "Charging",  // ← 추가 필요
  "serial": "1WMHH81234567890"  // ← 추가 필요
}
```

**Kotlin 코드:**
```kotlin
val batteryIntent = context.registerReceiver(
    null,
    IntentFilter(Intent.ACTION_BATTERY_CHANGED)
)

val status = batteryIntent?.getIntExtra(BatteryManager.EXTRA_STATUS, -1) ?: -1
val isCharging = status == BatteryManager.BATTERY_STATUS_CHARGING ||
                 status == BatteryManager.BATTERY_STATUS_FULL

val serial = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
    Build.getSerial()
} else {
    Build.SERIAL
}

// JSON 응답에 추가
.put("isCharging", isCharging)
.put("chargingStatus", when (status) {
    BatteryManager.BATTERY_STATUS_CHARGING -> "Charging"
    BatteryManager.BATTERY_STATUS_FULL -> "Full"
    else -> "Not charging"
})
.put("serial", serial)
```

---

## 테스트 시나리오

### 테스트 1: 초록 원 표시
```
1. Quest Agent 앱 실행
2. WPF에서 Agent 검색
3. 타일 왼쪽 상단 확인
4. ?? 초록 원 표시 확인 ?
```

### 테스트 2: Settings 이름 적용
```
1. Agent 검색 (타일: "Quest 3")
2. Settings 버튼 클릭
3. Serial "1WMHH81234567890" → "거실 Quest" 입력
4. 저장
5. 타일 이름 확인: "거실 Quest" ?
```

### 테스트 3: 충전 표시 (Android 수정 후)
```
1. Quest를 충전기에 연결
2. Agent 앱 실행
3. WPF에서 Agent 검색
4. 배터리 표시 확인: "95% ?" ?
```

---

## 이름 변경 흐름

```
[Agent 검색]
    ↓
Serial 받음: "1WMHH81234567890"
    ↓
Settings 확인
    ↓
매핑 존재? → YES → "거실 Quest" ?
    ↓ NO
Agent Model? → YES → "Quest 3"
    ↓ NO
Serial 표시: "1WMHH81234567890"
```

---

## Settings 창 동작

```
[Settings 버튼 클릭]
    ↓
현재 Devices 목록 표시
    ↓
각 Device:
  - Serial: "1WMHH81234567890"
  - Name: "Quest 3" (현재)
    ↓
사용자 입력: "거실 Quest"
    ↓
[저장] 클릭
    ↓
SettingsService.Save(_serialNameDic)
    ↓
OnChangeSerialName() 발동
    ↓
모든 Device 순회:
  if (_serialNameDic.TryGetValue(device.Serial, out var name))
      device.Name = name;  // "거실 Quest"로 변경
    ↓
UI 즉시 업데이트 ?
```

---

## 비교표

| 항목 | 변경 전 | 변경 후 |
|------|---------|---------|
| **연결 표시** | Agent만 있으면 ?? | Agent 있으면 ?? |
| **이름 우선순위** | Model 우선 | Settings > Model > Serial |
| **Settings 이름** | 적용 안 됨 | 즉시 적용 ? |
| **충전 표시** | 없음 | "95% ?" (Android 수정 필요) |

---

## 트러블슈팅

### 여전히 빨간 원
**원인:** `AgentHost`가 비어있음

**확인:**
```csharp
Debug.WriteLine($"AgentHost: {device.AgentHost}");
Debug.WriteLine($"IsConnected: {device.IsConnected}");
```

**해결:** Agent 검색 다시 실행

---

### 이름이 "Quest 3"으로 유지
**원인:** Settings에 Serial 매핑 없음

**확인:**
1. Settings 창 열기
2. Serial 확인 (예: "1WMHH81234567890")
3. 이름 입력 후 저장

**해결:** Settings에서 수동으로 이름 설정

---

### 충전 표시 안 나타남
**원인:** Android에서 `isCharging` 안 보냄

**확인:**
```bash
curl http://QuestIP:18080/status | jq .isCharging
```

결과가 `null`이면 Android 수정 필요

**해결:** Android Agent 코드 수정 (위의 Kotlin 코드 참조)

---

## 요약

### WPF 변경 완료
1. ? `IsConnected` - Agent 있으면 초록 원
2. ? 이름 우선순위 - Settings > Model > Serial
3. ? 충전 표시 준비 - `BatteryText` 업데이트

### Android 수정 필요
1. ? `/status` API에 `isCharging` 추가
2. ? `/status` API에 `serial` 추가
3. ? 배터리 상태 수집 로직 구현

### 결과
- **초록 원** ?
- **설정된 이름** ? (Android에서 Serial 받으면)
- **충전 표시** ? (Android 수정 후)

---

**? 빌드 성공!**
**? Agent 기기 초록 원 표시!**
**? Settings 이름 우선 적용!**
