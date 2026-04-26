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
        // ���������̂���HashSet�ɕύX
        private static readonly HashSet<string> ExcludedNames = new HashSet<string>
        {
            "System Sounds",
            "System Idle Process"
        };

        private MMDevice _device;
        private AudioSessionManager _sessionManager;
        private DispatcherTimer _peakMeterTimer;
        private DispatcherTimer _autoRefreshTimer;

        // コレクション
        public ObservableCollection<AudioSessionModel> AudioSessions { get; set; } = new ObservableCollection<AudioSessionModel>();

        // 利用可能なオーディオ出力デバイス一覧 (先頭は "デフォルト" = Id が空文字)
        public ObservableCollection<AudioDeviceModel> AvailableDevices { get; } = new ObservableCollection<AudioDeviceModel>();

        // アプリ名 → 選択デバイスID の永続化マップ
        private Dictionary<string, string> _appDeviceMap = new();

        public ICommand RefreshCommand { get; private set; }

        // マスターピーク関連フィールド
        private float _masterPeakValue;
        private float _masterPeakHoldValue;
        private int _masterPeakHoldFrames;
        private const int MasterPeakHoldDurationFrames = 90;
        private const float MasterPeakHoldDecayRate = 0.008f;

        public MixerViewModel()
        {
            InitializeAudioSessions();
            SetupPeakMeterTimer();

            _autoRefreshTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(3)
            };
            _autoRefreshTimer.Tick += AutoRefreshTimer_Tick;
            _autoRefreshTimer.Start();
        }

        private void AutoRefreshTimer_Tick(object sender, EventArgs e)
        {
            // AutoDetectDevices が有効な場合はデバイス一覧も更新する
            if (global::inuMixer.Properties.Settings.Default.AutoDetectDevices)
                InitializeDevices();

            SyncSessions();
        }

        #region Master Volume Properties

        public float MasterVolume
        {
            get => _device?.AudioEndpointVolume?.MasterVolumeLevelScalar ?? 0.0f;
            set
            {
                if (_device?.AudioEndpointVolume != null)
                {
                    _device.AudioEndpointVolume.MasterVolumeLevelScalar = value;
                    OnPropertyChanged(nameof(MasterVolume));
                    OnPropertyChanged(nameof(MasterVolumePercent));
                }
            }
        }

        public int MasterVolumePercent => (int)(MasterVolume * 100);

        public bool MasterIsMuted
        {
            get => _device?.AudioEndpointVolume?.Mute ?? false;
            set
            {
                if (_device?.AudioEndpointVolume != null)
                {
                    _device.AudioEndpointVolume.Mute = value;
                    OnPropertyChanged(nameof(MasterIsMuted));
                }
            }
        }

        public float MasterPeakValue
        {
            get => _masterPeakValue;
            private set
            {
                if (_masterPeakValue != value)
                {
                    _masterPeakValue = value;
                    OnPropertyChanged(nameof(MasterPeakValue));
                }
            }
        }

        public float MasterPeakHoldValue
        {
            get => _masterPeakHoldValue;
            private set
            {
                if (Math.Abs(_masterPeakHoldValue - value) > 0.0005f)
                {
                    _masterPeakHoldValue = value;
                    OnPropertyChanged(nameof(MasterPeakHoldValue));
                }
            }
        }

        #endregion

        private void InitializeAudioSessions()
        {
            try
            {
                var deviceEnumerator = new MMDeviceEnumerator();
                _device = deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                _sessionManager = _device.AudioSessionManager;

                InitializeDevices();
                LoadAppDevices();

                OnPropertyChanged(nameof(MasterVolume));
                OnPropertyChanged(nameof(MasterIsMuted));

                SyncSessions();
            }
            catch (Exception)
            {
                //�f�o�C�X��������Ȃ����̏������G���[�Ή�
            }
        }

        private void InitializeDevices()
        {
            AvailableDevices.Clear();
            AvailableDevices.Add(new AudioDeviceModel { Id = string.Empty, FriendlyName = "デフォルト (システム規定)" });

            try
            {
                var enumerator = new MMDeviceEnumerator();
                var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                foreach (var device in devices)
                {
                    AvailableDevices.Add(new AudioDeviceModel { Id = device.ID, FriendlyName = device.FriendlyName });
                }
            }
            catch { }
        }

        private void LoadAppDevices()
        {
            _appDeviceMap.Clear();
            try
            {
                string saved = global::inuMixer.Properties.Settings.Default.AppDevices;
                if (string.IsNullOrEmpty(saved)) return;

                foreach (var entry in saved.Split(';'))
                {
                    var parts = entry.Split('|');
                    if (parts.Length == 2 && !string.IsNullOrEmpty(parts[0]))
                    {
                        string appName = parts[0];
                        string deviceId = parts[1];
                        if (AvailableDevices.Any(d => d.Id == deviceId))
                            _appDeviceMap[appName] = deviceId;
                    }
                }
            }
            catch { }
        }

        public void SaveAppDevices()
        {
            try
            {
                var entries = AudioSessions
                    .Where(s => !string.IsNullOrEmpty(s.SelectedDeviceId))
                    .Select(s => $"{s.DisplayName}|{s.SelectedDeviceId}");
                global::inuMixer.Properties.Settings.Default.AppDevices = string.Join(";", entries);
                global::inuMixer.Properties.Settings.Default.Save();
            }
            catch { }
        }

        public void SyncSessions()
        {
            string hiddenAppsStr = global::inuMixer.Properties.Settings.Default.HiddenApps;
            var hiddenApps = string.IsNullOrEmpty(hiddenAppsStr)
                ? new HashSet<string>()
                : new HashSet<string>(hiddenAppsStr.Split(','));

            // 現在の各モデルのルーティング先デバイスIDを記録
            // (デバイス切り替え後、正しいデバイスのセッションを優先するために使用)
            var modelTargetDeviceIds = AudioSessions.ToDictionary(
                m => m.DisplayName,
                m => m.SelectedDeviceId ?? string.Empty);

            // ── Step 1: 全アクティブレンダーデバイスからセッションを収集 ──────
            // PID → (セッション, デバイスID) のリスト
            var sessionsByPid = new Dictionary<int, List<(AudioSessionControl Session, string DeviceId)>>();

            try
            {
                var enumerator = new MMDeviceEnumerator();
                var allDevices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);

                foreach (var device in allDevices)
                {
                    try
                    {
                        var sm = device.AudioSessionManager;
                        sm.RefreshSessions();

                        for (int i = 0; i < sm.Sessions.Count; i++)
                        {
                            var session = sm.Sessions[i];
                            int pid = (int)session.GetProcessID;

                            if (pid > 0 && session.State != AudioSessionState.AudioSessionStateExpired)
                            {
                                if (!sessionsByPid.ContainsKey(pid))
                                    sessionsByPid[pid] = new List<(AudioSessionControl, string)>();
                                sessionsByPid[pid].Add((session, device.ID));
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }

            // ── Step 2: 各PIDの「最適」セッションを決定 ──────────────────────
            // 優先順: ① ルーティング先デバイス > ② デフォルトデバイス > ③ 最初のデバイス
            var activePids = new HashSet<int>(sessionsByPid.Keys);
            var pendingSessions = new List<(string DisplayName, AudioSessionControl Session)>();
            string defaultDeviceId = _device?.ID ?? string.Empty;

            foreach (var (pid, sessions) in sessionsByPid)
            {
                string displayName = AudioSessionModel.GetFormattedDisplayName(pid);
                if (ExcludedNames.Contains(displayName) || hiddenApps.Contains(displayName)) continue;

                modelTargetDeviceIds.TryGetValue(displayName, out string targetDeviceId);

                AudioSessionControl bestSession = null;

                // ① ルーティング先デバイスのセッション
                if (!string.IsNullOrEmpty(targetDeviceId))
                    bestSession = sessions.FirstOrDefault(s => s.DeviceId == targetDeviceId).Session;

                // ② デフォルトデバイスのセッション
                if (bestSession == null && !string.IsNullOrEmpty(defaultDeviceId))
                    bestSession = sessions.FirstOrDefault(s => s.DeviceId == defaultDeviceId).Session;

                // ③ 最初のセッション (フォールバック)
                bestSession ??= sessions[0].Session;

                pendingSessions.Add((displayName, bestSession));
            }

            // ── Step 3: 削除フェーズ ──────────────────────────────────────────
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

            // ── Step 4: 追加・更新フェーズ ───────────────────────────────────
            foreach (var (displayName, session) in pendingSessions)
            {
                var existingModel = AudioSessions.FirstOrDefault(m => m.DisplayName == displayName);

                if (existingModel != null)
                {
                    // デバイス切り替え後のセッション参照を更新
                    existingModel.UpdateOrAddSession(session);
                }
                else
                {
                    var newModel = new AudioSessionModel(session);
                    if (!string.IsNullOrWhiteSpace(newModel.DisplayName) && !hiddenApps.Contains(newModel.DisplayName))
                    {
                        if (_appDeviceMap.TryGetValue(newModel.DisplayName, out string? savedDeviceId))
                            newModel.SelectedDeviceId = savedDeviceId;

                        AudioSessions.Add(newModel);
                    }
                }
            }
        }

        public void SaveOrder()
        {
            try
            {
                // LINQ�̌��ʂ𒼐ڊi�[
                global::inuMixer.Properties.Settings.Default.AppOrder =
                    string.Join(",", AudioSessions.Select(x => x.DisplayName));
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

                // �\�����̕��ёւ����W�b�N
                var sortedItems = AudioSessions.OrderBy(x =>
                {
                    int index = orderList.IndexOf(x.DisplayName);
                    return index == -1 ? 999 : index;
                }).ToList();

                for (int i = 0; i < sortedItems.Count; i++)
                {
                    int oldIndex = AudioSessions.IndexOf(sortedItems[i]);
                    if (oldIndex != i)
                    {
                        AudioSessions.Move(oldIndex, i);
                    }
                }
            }
            catch { }
        }

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
                    names.Add(AudioSessionModel.GetFormattedDisplayName(pid));
                }
            }
            return names.ToList();
        }

        private void SetupPeakMeterTimer()
        {
            _peakMeterTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(16)
            };
            _peakMeterTimer.Tick += PeakMeterTimer_Tick;
            _peakMeterTimer.Start();
        }

        private void PeakMeterTimer_Tick(object sender, EventArgs e)
        {
            foreach (var session in AudioSessions)
            {
                session.UpdateState();
            }

            if (_device != null)
            {
                try
                {
                    float masterMaxPeak = _device.AudioMeterInformation.MasterPeakValue;
                    MasterPeakValue = masterMaxPeak * MasterVolume;

                    // ピークホールド
                    float masterCurrent = MasterPeakValue;
                    if (masterCurrent >= _masterPeakHoldValue)
                    {
                        MasterPeakHoldValue = masterCurrent;
                        _masterPeakHoldFrames = MasterPeakHoldDurationFrames;
                    }
                    else if (_masterPeakHoldFrames > 0)
                    {
                        _masterPeakHoldFrames--;
                    }
                    else
                    {
                        MasterPeakHoldValue = Math.Max(0f, _masterPeakHoldValue - MasterPeakHoldDecayRate);
                    }
                }
                catch { }
            }
        }

        public void Dispose()
        {
            _peakMeterTimer?.Stop();
            _autoRefreshTimer?.Stop();

            foreach (var session in AudioSessions)
            {
                session.Dispose();
            }

            _device?.Dispose();
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}