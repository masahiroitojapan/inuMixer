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
        // 検索効率のためHashSetに変更
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

        public ICommand RefreshCommand { get; private set; }

        // マスタボリューム関連フィールド
        private float _masterPeakValue;

        public MixerViewModel()
        {
            InitializeAudioSessions();
            SetupPeakMeterTimer();

            _autoRefreshTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(3)
            };
            _autoRefreshTimer.Tick += (s, e) => SyncSessions();
            _autoRefreshTimer.Start();
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

        #endregion

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
            catch (Exception)
            {
                // デバイスが見つからない等の初期化エラー対応
            }
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
            var pendingSessions = new List<(string DisplayName, AudioSessionControl Session)>();

            // アクティブなセッションを収集
            for (int i = 0; i < _sessionManager.Sessions.Count; i++)
            {
                var session = _sessionManager.Sessions[i];
                int pid = (int)session.GetProcessID;

                if (pid > 0 && (session.State == AudioSessionState.AudioSessionStateActive || session.State == AudioSessionState.AudioSessionStateInactive))
                {
                    activePids.Add(pid);
                    string displayName = AudioSessionModel.GetFormattedDisplayName(pid);
                    pendingSessions.Add((displayName, session));
                }
            }

            // 削除フェーズ: 終了したプロセスや非表示設定されたアプリを除去
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

                var existingModel = AudioSessions.FirstOrDefault(m => m.DisplayName == displayName);

                if (existingModel != null)
                {
                    existingModel.AddSession(session);
                }
                else
                {
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
                // LINQの結果を直接格納
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

                // 表示順の並び替えロジック
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

                    // マスターミュート時、またはボリュームがほぼゼロの場合はピークをゼロにする
                    if (MasterIsMuted || MasterVolume < 0.01f)
                    {
                        MasterPeakValue = 0f;
                    }
                    else
                    {
                        // ボリュームを乗算し、実際に聞こえる音量レベルを反映させる
                        MasterPeakValue = masterMaxPeak * MasterVolume;
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