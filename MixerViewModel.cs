using inuMixer;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows.Input;
using System.Windows.Threading;

namespace inuMixer
{
    public class MixerViewModel : INotifyPropertyChanged, IDisposable
    {
        public ObservableCollection<AudioSessionModel> AudioSessions { get; set; } = new ObservableCollection<AudioSessionModel>();

        private static readonly string[] ExcludedNames = { "System Sounds", "System Idle Process" };
        private MMDevice _device;
        private AudioSessionManager _sessionManager;

        private DispatcherTimer _peakMeterTimer;
        private DispatcherTimer _autoRefreshTimer;

        public ICommand RefreshCommand { get; private set; }

        public MixerViewModel()
        {
            InitializeAudioSessions();
            SetupPeakMeterTimer();

            _autoRefreshTimer = new DispatcherTimer();
            _autoRefreshTimer.Interval = TimeSpan.FromSeconds(3);
            _autoRefreshTimer.Tick += (s, e) => SyncSessions();
            _autoRefreshTimer.Start();
        }

        // ... (MasterVolume関連は変更なし) ...
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

        public void SyncSessions()
        {
            if (_sessionManager == null) return;

            _sessionManager.RefreshSessions();

            string hiddenAppsStr = global::inuMixer.Properties.Settings.Default.HiddenApps;
            var hiddenApps = string.IsNullOrEmpty(hiddenAppsStr)
                ? new HashSet<string>()
                : new HashSet<string>(hiddenAppsStr.Split(','));

            var activePids = new HashSet<int>();

            // プロセス名ごとにセッションを一時保管
            // 【変更】Tupleの第一引数を「表示名(Google Chrome)」に変更
            var pendingSessions = new List<(string DisplayName, AudioSessionControl Session)>();

            for (int i = 0; i < _sessionManager.Sessions.Count; i++)
            {
                var session = _sessionManager.Sessions[i];
                int pid = (int)session.GetProcessID;

                if (pid > 0 && (session.State == AudioSessionState.AudioSessionStateActive || session.State == AudioSessionState.AudioSessionStateInactive))
                {
                    activePids.Add(pid);

                    // 【変更】AudioSessionModelの静的メソッドを使って「正式名称」を取得する
                    string displayName = AudioSessionModel.GetFormattedDisplayName(pid);

                    pendingSessions.Add((displayName, session));
                }
            }

            // 削除フェーズ
            for (int i = AudioSessions.Count - 1; i >= 0; i--)
            {
                var model = AudioSessions[i];
                model.RemoveDeadSessions(activePids);

                if (model.IsEmpty || hiddenApps.Contains(model.DisplayName))
                {
                    model.Dispose();
                    AudioSessions.RemoveAt(i);
                }
            }

            // 追加・統合フェーズ
            foreach (var item in pendingSessions)
            {
                string displayName = item.DisplayName;
                var session = item.Session;

                if (ExcludedNames.Contains(displayName) || hiddenApps.Contains(displayName)) continue;

                // 【変更】正式名称(DisplayName)で既存モデルを探す
                var existingModel = AudioSessions.FirstOrDefault(m => m.DisplayName == displayName);

                if (existingModel != null)
                {
                    existingModel.AddSession(session);
                }
                else
                {
                    // 新規作成時も内部で GetFormattedDisplayName が呼ばれるので名前は一致する
                    var newModel = new AudioSessionModel(session);
                    if (!string.IsNullOrWhiteSpace(newModel.DisplayName) && !hiddenApps.Contains(newModel.DisplayName))
                    {
                        AudioSessions.Add(newModel);
                    }
                }
            }
        }

        public void SaveOrder()
        {
            try
            {
                var orderList = AudioSessions.Select(x => x.DisplayName).ToList();
                string orderString = string.Join(",", orderList);
                global::inuMixer.Properties.Settings.Default.AppOrder = orderString;
                global::inuMixer.Properties.Settings.Default.Save();
            }
            catch { }
        }

        public void LoadOrder()
        {
            try
            {
                string orderString = global::inuMixer.Properties.Settings.Default.AppOrder;
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

        // 設定画面用のアクティブアプリ一覧取得
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
                    // 【変更】ここでも正式名称を使うように修正
                    names.Add(AudioSessionModel.GetFormattedDisplayName(pid));
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
            foreach (var session in AudioSessions) session.UpdateState(); // UpdateStateに変更済み
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
        protected virtual void OnPropertyChanged(string propertyName) { PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName)); }
    }
}