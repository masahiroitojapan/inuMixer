using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows.Threading;
using System.Collections.Generic;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace VolMixer
{
    public class MixerViewModel : INotifyPropertyChanged, IDisposable
    {
        public ObservableCollection<AudioSessionModel> AudioSessions { get; set; } = new ObservableCollection<AudioSessionModel>();

        private static readonly string[] ExcludedNames = { "System Sounds", "System Idle Process" };
        private MMDevice _device;
        private AudioSessionManager _sessionManager;

        private DispatcherTimer _peakMeterTimer;
        private DispatcherTimer _autoRefreshTimer;

        public MixerViewModel()
        {
            InitializeAudioSessions();
            SetupPeakMeterTimer();

            // 自動更新タイマー (3秒ごとにチェック)
            _autoRefreshTimer = new DispatcherTimer();
            _autoRefreshTimer.Interval = TimeSpan.FromSeconds(3);
            _autoRefreshTimer.Tick += (s, e) => SyncSessions();
            _autoRefreshTimer.Start();
        }

        // --- マスターボリューム関連 ---
        public float MasterVolume { get => _device?.AudioEndpointVolume?.MasterVolumeLevelScalar ?? 0.0f; set { if (_device?.AudioEndpointVolume != null) { _device.AudioEndpointVolume.MasterVolumeLevelScalar = value; OnPropertyChanged(nameof(MasterVolume)); OnPropertyChanged(nameof(MasterVolumePercent)); } } }
        public int MasterVolumePercent => (int)(MasterVolume * 100);
        public bool MasterIsMuted { get => _device?.AudioEndpointVolume?.Mute ?? false; set { if (_device?.AudioEndpointVolume != null) { _device.AudioEndpointVolume.Mute = value; OnPropertyChanged(nameof(MasterIsMuted)); } } }
        private float _masterPeakValue;
        public float MasterPeakValue { get => _masterPeakValue; private set { if (_masterPeakValue != value) { _masterPeakValue = value; OnPropertyChanged(nameof(MasterPeakValue)); } } }

        private void InitializeAudioSessions()
        {
            try
            {
                var deviceEnumerator = new MMDeviceEnumerator();
                _device = deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                _sessionManager = _device.AudioSessionManager;

                OnPropertyChanged(nameof(MasterVolume));
                OnPropertyChanged(nameof(MasterIsMuted));

                SyncSessions();
            }
            catch { }
        }

        // --- スマート更新ロジック (差分更新) ---
        public void SyncSessions()
        {
            if (_sessionManager == null) return;

            _sessionManager.RefreshSessions();

            // 設定から非表示リスト読み込み
            string hiddenAppsStr = "";
            try { hiddenAppsStr = global::VolMixer.Properties.Settings.Default.HiddenApps; } catch { }

            var hiddenApps = string.IsNullOrEmpty(hiddenAppsStr)
                ? new HashSet<string>()
                : new HashSet<string>(hiddenAppsStr.Split(','));

            var currentPids = new HashSet<int>();
            var newSessions = new List<AudioSessionControl>();

            // 1. アクティブなセッションをリストアップ
            for (int i = 0; i < _sessionManager.Sessions.Count; i++)
            {
                var session = _sessionManager.Sessions[i];
                int pid = (int)session.GetProcessID;

                if (pid > 0 && (session.State == AudioSessionState.AudioSessionStateActive || session.State == AudioSessionState.AudioSessionStateInactive))
                {
                    if (!currentPids.Contains(pid))
                    {
                        currentPids.Add(pid);
                        newSessions.Add(session);
                    }
                }
            }

            // 2. 終了したアプリ、または非表示設定されたアプリを削除
            for (int i = AudioSessions.Count - 1; i >= 0; i--)
            {
                var model = AudioSessions[i];
                if (!currentPids.Contains(model.ProcessId) || hiddenApps.Contains(model.DisplayName))
                {
                    model.Dispose();
                    AudioSessions.RemoveAt(i);
                }
            }

            // 3. 新しいアプリを追加
            foreach (var session in newSessions)
            {
                if (AudioSessions.Any(x => x.ProcessId == (int)session.GetProcessID)) continue;

                var model = new AudioSessionModel(session);

                // 除外チェック
                if (string.IsNullOrWhiteSpace(model.DisplayName) || ExcludedNames.Contains(model.DisplayName) || hiddenApps.Contains(model.DisplayName))
                {
                    model.Dispose();
                    continue;
                }
                AudioSessions.Add(model);
            }
        }

        // --- 並び順の保存・復元 ---
        public void SaveOrder()
        {
            try
            {
                var orderList = AudioSessions.Select(x => x.DisplayName).ToList();
                string orderString = string.Join(",", orderList);
                global::VolMixer.Properties.Settings.Default.AppOrder = orderString;
                global::VolMixer.Properties.Settings.Default.Save();
            }
            catch { }
        }

        public void LoadOrder()
        {
            try
            {
                string orderString = global::VolMixer.Properties.Settings.Default.AppOrder;
                if (string.IsNullOrEmpty(orderString)) return;

                var orderList = orderString.Split(',').ToList();
                var sortedItems = AudioSessions.OrderBy(x =>
                {
                    int index = orderList.IndexOf(x.DisplayName);
                    return index == -1 ? 999 : index;
                }).ToList();

                for (int i = 0; i < sortedItems.Count; i++)
                {
                    int oldIndex = AudioSessions.IndexOf(sortedItems[i]);
                    if (oldIndex != i) AudioSessions.Move(oldIndex, i);
                }
            }
            catch { }
        }

        // --- 設定画面用データ取得 ---
        public List<string> GetActiveAppNames()
        {
            if (_sessionManager == null) return new List<string>();
            _sessionManager.RefreshSessions();

            var names = new HashSet<string>();
            for (int i = 0; i < _sessionManager.Sessions.Count; i++)
            {
                var session = _sessionManager.Sessions[i];
                int pid = (int)session.GetProcessID;
                if (pid > 0)
                {
                    try
                    {
                        var process = System.Diagnostics.Process.GetProcessById(pid);
                        if (!string.IsNullOrEmpty(process.ProcessName)) names.Add(process.ProcessName);
                    }
                    catch { }
                }
            }
            return names.ToList();
        }

        private void SetupPeakMeterTimer()
        {
            _peakMeterTimer = new DispatcherTimer();
            _peakMeterTimer.Interval = TimeSpan.FromMilliseconds(16);
            _peakMeterTimer.Tick += PeakMeterTimer_Tick;
            _peakMeterTimer.Start();
        }

        private void PeakMeterTimer_Tick(object sender, EventArgs e)
        {
            foreach (var session in AudioSessions) session.UpdatePeakValue();
            if (_device != null) { try { MasterPeakValue = _device.AudioMeterInformation.MasterPeakValue; } catch { } }
        }

        public void Dispose()
        {
            _peakMeterTimer?.Stop();
            _autoRefreshTimer?.Stop();
            foreach (var session in AudioSessions) session.Dispose();
            if (_device != null) _device.Dispose();
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}