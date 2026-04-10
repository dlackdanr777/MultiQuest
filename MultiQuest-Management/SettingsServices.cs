using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace MultiQuest_Management
{
    public class SettingsService
    {
        public static SettingsService Instance { get; } = new SettingsService();

        public ObservableCollection<KeyValueItem> Items { get; } = new();

        // 저장/로드 시 최신 딕셔너리 즉시 알림
        public event Action<IReadOnlyDictionary<string, string>> Changed;

        private readonly string _dir = AppDomain.CurrentDomain.BaseDirectory;
        private readonly string _file;

        private SettingsService()
        {
            Directory.CreateDirectory(_dir);
            _file = Path.Combine(_dir, "settings.json");
            Load();                 // 시작 시 로드
            RaiseChanged();         // 처음 스냅샷도 한 번 쏴줌
        }

        public void Load()
        {
            Items.Clear();
            if (!File.Exists(_file)) { RaiseChanged(); return; }

            try
            {
                var text = File.ReadAllText(_file);
                var data = JsonSerializer.Deserialize<List<KeyValueItem>>(text) ?? new();
                foreach (var kv in data) Items.Add(kv);
            }
            catch { /* 로그 원하면 추가 */ }

            RaiseChanged();
        }

        public void Save()
        {
            // 빈 키 항목을 컬렉션에서도 제거하여 Items와 저장 파일 간 불일치 방지
            var toRemove = Items.Where(kv => string.IsNullOrWhiteSpace(kv.Key)).ToList();
            foreach (var kv in toRemove) Items.Remove(kv);

            foreach (var kv in Items)
                kv.Key = kv.Key.Trim(); // 키 앞뒤 공백 제거

            var json = JsonSerializer.Serialize(new List<KeyValueItem>(Items),
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_file, json);
            RaiseChanged();
        }

        public void ReplaceAll(IEnumerable<KeyValueItem> newItems)
        {
            Items.Clear();
            foreach (var kv in newItems) Items.Add(kv);
            RaiseChanged();
        }

        public void Set(string key, string value)
        {
            var found = Items.FirstOrDefault(i => string.Equals(i.Key, key, StringComparison.Ordinal));
            if (found != null) found.Value = value;
            else Items.Add(new KeyValueItem { Key = key, Value = value });
            // 필요 시 여기서 바로 Save() 하지 않고, SettingWindow에서 Save 버튼 눌렀을 때 Save() 호출
            RaiseChanged();
        }

        // 🔹 핵심: 현재 설정을 Dictionary로 스냅샷으로 제공
        public IReadOnlyDictionary<string, string> Snapshot()
        {
            // 중복 키가 있다면 "마지막 값"을 채택
            var dict = Items
                .Where(kv => !string.IsNullOrWhiteSpace(kv.Key))
                .GroupBy(kv => kv.Key.Trim(), StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Last().Value ?? string.Empty, StringComparer.Ordinal);

            return new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(dict);
        }

        private void RaiseChanged() => Changed?.Invoke(Snapshot());
    }

    public class KeyValueItem : INotifyPropertyChanged
    {
        private string _key;
        private string _value;

        public string Key { get => _key; set { _key = value; OnPropertyChanged(); } }
        public string Value { get => _value; set { _value = value; OnPropertyChanged(); } }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    public class SettingsViewModel
    {
        private readonly ObservableCollection<Device> _devices;
        public ObservableCollection<KeyValueItem> Items { get; } = new();
        public KeyValueItem Selected { get; set; }
        public ObservableCollection<string> AvailableSerials { get; } = new();

        public SettingsViewModel(SettingsService service, ObservableCollection<Device> devices)
        {
            _devices = devices;
            foreach (var kv in service.Items)
                Items.Add(new KeyValueItem { Key = kv.Key, Value = kv.Value });

            RefreshSerials();
            _devices.CollectionChanged += (_, __) => RefreshSerials();
        }

        public void RefreshSerials()
        {
            var serials = _devices.Where(d => d.IsConnected)
                                  .Select(d => string.IsNullOrWhiteSpace(d.Serial) ? d.Name ?? d.Ip : d.Serial)
                                  .Distinct()
                                  .OrderBy(s => s)
                                  .ToList();
            AvailableSerials.Clear();
            foreach (var s in serials) AvailableSerials.Add(s);
        }

        public void AddRowsForConnectedDevices()
        {
            foreach (var s in AvailableSerials)
                if (!Items.Any(i => i.Key == s))
                    Items.Add(new KeyValueItem { Key = s, Value = "" });
        }

        public void AddEmpty() { Items.Add(new KeyValueItem { Key = "", Value = "" }); }
        public void RemoveSelected() { if (Selected != null) Items.Remove(Selected); }
        public void ClearAll() { Items.Clear(); }
        public bool ValidateNoDuplicateKeys()
            => Items.Where(i => !string.IsNullOrWhiteSpace(i.Key))
                    .GroupBy(i => i.Key.Trim())
                    .All(g => g.Count() == 1);
    }
}
