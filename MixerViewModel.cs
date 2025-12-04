using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows.Threading;
using System.Windows.Input;
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

        public ICommand RefreshCommand { get; private set; }

        public MixerViewModel()
        {
            RefreshCommand = new RelayCommand(ExecuteRefresh);
            InitializeAudioSessions();
            SetupPeakMeterTimer();
        }

        // ==========================================================
        // ★ マスターボリューム制御用プロパティ ★
        // ==========================================================

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

        private float _masterPeakValue;
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

        // ==========================================================
        // 初期化・ロード処理
        // ==========================================================

        private void InitializeAudioSessions()
        {
            try
            {
                var deviceEnumerator = new MMDeviceEnumerator();
                _device = deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                _sessionManager = _device.AudioSessionManager;

                // マスターボリューム初期値通知
                OnPropertyChanged(nameof(MasterVolume));
                OnPropertyChanged(nameof(MasterIsMuted));

                LoadSessions();
            }
            catch { }
        }

        public void LoadSessions()
        {
            if (_sessionManager == null) return;

            foreach (var session in AudioSessions) session.Dispose();
            AudioSessions.Clear();

            _sessionManager.RefreshSessions();

            var trackedPids = new System.Collections.Generic.HashSet<int>();

            for (int i = 0; i < _sessionManager.Sessions.Count; i++)
            {
                var session = _sessionManager.Sessions[i];
                int pid = (int)session.GetProcessID;

                if (pid > 0 && (session.State == AudioSessionState.AudioSessionStateActive || session.State == AudioSessionState.AudioSessionStateInactive))
                {
                    if (trackedPids.Contains(pid)) continue;

                    var model = new AudioSessionModel(session);
                    if (string.IsNullOrWhiteSpace(model.DisplayName) || ExcludedNames.Contains(model.DisplayName))
                    {
                        model.Dispose();
                        continue;
                    }

                    AudioSessions.Add(model);
                    trackedPids.Add(pid);
                }
            }
        }

        // ==========================================================
        // タイマー処理
        // ==========================================================

        private void SetupPeakMeterTimer()
        {
            _peakMeterTimer = new DispatcherTimer();
            _peakMeterTimer.Interval = TimeSpan.FromMilliseconds(16);
            _peakMeterTimer.Tick += PeakMeterTimer_Tick;
            _peakMeterTimer.Start();
        }

        private void PeakMeterTimer_Tick(object sender, EventArgs e)
        {
            // 各アプリのピーク更新
            foreach (var session in AudioSessions)
            {
                session.UpdatePeakValue();
            }

            // マスターのピーク更新
            if (_device != null)
            {
                try
                {
                    MasterPeakValue = _device.AudioMeterInformation.MasterPeakValue;
                }
                catch { }
            }
        }

        private void ExecuteRefresh(object parameter)
        {
            LoadSessions();
        }

        public void Dispose()
        {
            _peakMeterTimer?.Stop();
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