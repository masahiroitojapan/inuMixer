using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Windows.Media;

namespace inuMixer
{
    public class AudioSessionModel : INotifyPropertyChanged, IDisposable
    {
        private readonly Dictionary<int, AudioSessionControl> _sessions = new Dictionary<int, AudioSessionControl>();
        private AudioSessionControl _primarySession;

        // 状態管理用フィールド
        private float _lastVolume = -1;
        private bool _lastMuteState = false;
        private float _peakValue;
        private string _selectedDeviceId = string.Empty;

        // ピークホールド
        private float _peakHoldValue;
        private int _peakHoldFrames;
        private const int PeakHoldDurationFrames = 90;   // ~1.5 秒 (60fps 想定)
        private const float PeakHoldDecayRate = 0.008f;  // 1フレームごとの減衰量

        public int ProcessId { get; private set; }
        public string DisplayName { get; private set; }
        public ImageSource Icon { get; private set; }
        public bool IsEmpty => _sessions.Count == 0;

        public AudioSessionModel(AudioSessionControl primarySession)
        {
            AddSession(primarySession);
        }

        public void AddSession(AudioSessionControl session)
        {
            int pid = (int)session.GetProcessID;
            if (!_sessions.ContainsKey(pid))
            {
                _sessions.Add(pid, session);

                // 既にカスタムデバイスが設定されている場合、新しいプロセスにも適用
                if (!string.IsNullOrEmpty(_selectedDeviceId))
                    AudioPolicyConfigService.TrySetEndpointForProcess((uint)pid, _selectedDeviceId);

                if (_primarySession == null)
                {
                    _primarySession = session;
                    ProcessId = pid;
                    DisplayName = GetFormattedDisplayName(pid);
                    Icon = GetProcessIcon(pid);
                }
            }
        }

        /// <summary>
        /// 指定セッションで既存の PID セッション参照を更新、なければ新規追加する。
        /// デバイス切り替え後に SyncSessions() から呼ばれ、新しいデバイスのセッションに
        /// 切り替えることでピークメーターとミュートが正しく動作するようになる。
        /// </summary>
        public void UpdateOrAddSession(AudioSessionControl newSession)
        {
            int pid = (int)newSession.GetProcessID;

            if (_sessions.TryGetValue(pid, out var oldSession))
            {
                // 同じオブジェクトでなければ新しいセッション参照に差し替え
                if (!ReferenceEquals(oldSession, newSession))
                {
                    _sessions[pid] = newSession;

                    if (ReferenceEquals(_primarySession, oldSession))
                        _primarySession = newSession;

                    try { oldSession.Dispose(); } catch { }
                }
            }
            else
            {
                AddSession(newSession);
            }
        }

        public void RemoveDeadSessions(HashSet<int> activePids)
        {
            var keysToRemove = _sessions.Keys.Where(pid => !activePids.Contains(pid)).ToList();
            foreach (var pid in keysToRemove)
            {
                var session = _sessions[pid];
                session.Dispose();
                _sessions.Remove(pid);
            }
        }

        public float Volume
        {
            get => _primarySession?.SimpleAudioVolume.Volume ?? 0;
            set
            {
                foreach (var session in _sessions.Values)
                {
                    // 浮動小数点の比較誤差を考慮
                    if (Math.Abs(session.SimpleAudioVolume.Volume - value) > 0.001f)
                    {
                        session.SimpleAudioVolume.Volume = value;
                    }
                }
                if (Math.Abs(_lastVolume - value) > 0.001f)
                {
                    _lastVolume = value;
                    OnPropertyChanged(nameof(Volume));
                    OnPropertyChanged(nameof(VolumePercent));
                }
            }
        }

        public int VolumePercent => (int)(Volume * 100);

        public bool IsMuted
        {
            get => _primarySession?.SimpleAudioVolume.Mute ?? false;
            set
            {
                foreach (var session in _sessions.Values)
                {
                    if (session.SimpleAudioVolume.Mute != value)
                    {
                        session.SimpleAudioVolume.Mute = value;
                    }
                }
                if (_lastMuteState != value)
                {
                    _lastMuteState = value;
                    OnPropertyChanged(nameof(IsMuted));
                }
            }
        }

        public float PeakValue
        {
            get => _peakValue;
            private set
            {
                if (_peakValue != value)
                {
                    _peakValue = value;
                    OnPropertyChanged(nameof(PeakValue));
                }
            }
        }

        public float PeakHoldValue
        {
            get => _peakHoldValue;
            private set
            {
                if (Math.Abs(_peakHoldValue - value) > 0.0005f)
                {
                    _peakHoldValue = value;
                    OnPropertyChanged(nameof(PeakHoldValue));
                }
            }
        }

        /// <summary>選択中の出力デバイスID。空文字列はシステム規定デバイスを意味する。</summary>
        public string SelectedDeviceId
        {
            get => _selectedDeviceId;
            set
            {
                if (_selectedDeviceId != value)
                {
                    _selectedDeviceId = value;
                    ApplyDeviceRouting();
                    OnPropertyChanged(nameof(SelectedDeviceId));
                }
            }
        }

        private void ApplyDeviceRouting()
        {
            foreach (var pid in _sessions.Keys)
            {
                if (string.IsNullOrEmpty(_selectedDeviceId))
                    AudioPolicyConfigService.TryResetEndpointForProcess((uint)pid);
                else
                    AudioPolicyConfigService.TrySetEndpointForProcess((uint)pid, _selectedDeviceId);
            }
        }

        public void UpdateState()
        {
            float maxPeak = 0f;
            foreach (var session in _sessions.Values)
            {
                try
                {
                    float p = session.AudioMeterInformation.MasterPeakValue;
                    if (p > maxPeak) maxPeak = p;
                }
                catch { /* AudioMeterの値の取得失敗は無視 */ }
            }

            PeakValue = maxPeak * Volume;

            // ピークホールドロジック
            float currentPeak = PeakValue;
            if (currentPeak >= _peakHoldValue)
            {
                // 新しいピークが現在のホールド値以上 — 更新してカウンターリセット
                PeakHoldValue = currentPeak;
                _peakHoldFrames = PeakHoldDurationFrames;
            }
            else if (_peakHoldFrames > 0)
            {
                _peakHoldFrames--; // ホールド中
            }
            else
            {
                PeakHoldValue = Math.Max(0f, _peakHoldValue - PeakHoldDecayRate); // 減衰
            }

            float currentVol = _primarySession?.SimpleAudioVolume.Volume ?? 0;
            bool currentMute = _primarySession?.SimpleAudioVolume.Mute ?? false;

            if (Math.Abs(_lastVolume - currentVol) > 0.001f)
            {
                _lastVolume = currentVol;
                OnPropertyChanged(nameof(Volume));
                OnPropertyChanged(nameof(VolumePercent));
            }

            if (_lastMuteState != currentMute)
            {
                _lastMuteState = currentMute;
                OnPropertyChanged(nameof(IsMuted));
            }
        }

        /// <summary>
        /// プロセスIDから表示名を取得 (例: chrome.exe -> Google Chrome)
        /// </summary>
        public static string GetFormattedDisplayName(int pid)
        {
            try
            {
                var process = Process.GetProcessById(pid);

                // (1) FileDescriptionの取得を試みるブロック
                try
                {
                    if (process.MainModule?.FileVersionInfo != null)
                    {
                        string description = process.MainModule.FileVersionInfo.FileDescription;
                        if (!string.IsNullOrWhiteSpace(description))
                        {
                            return description; // 成功: FileDescriptionを返す
                        }
                    }
                }
                catch { /* アクセス拒否などでMainModuleの取得失敗時はここでcatchし次の行へ進む */ }

                // (2) FileDescriptionが取得できなかった場合、または何らかのエラーが発生した場合
                return process.ProcessName; // プロセス名 (例: obs64) を返す
            }
            catch // (3) Process.GetProcessById 自体が失敗した場合
            {
                return "Unknown";
            }
        }

        private ImageSource GetProcessIcon(int pid)
        {
            return IconHelper.GetIconFromProcess(pid);
        }

        public void Dispose()
        {
            foreach (var session in _sessions.Values)
            {
                session.Dispose();
            }
            _sessions.Clear();
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}