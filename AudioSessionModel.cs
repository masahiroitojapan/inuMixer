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

                if (_primarySession == null)
                {
                    _primarySession = session;
                    ProcessId = pid;
                    DisplayName = GetFormattedDisplayName(pid);
                    Icon = GetProcessIcon(pid);
                }
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
                catch { /* AudioMeter情報の取得失敗は無視 */ }
            }

            // ミュート状態 (IsMuted) のチェックを外し、常に生ピーク値 * ボリュームで計算する。
            // ただし、ボリュームがゼロに近い場合は、念のためゼロにする。
            if (Volume < 0.01f) // ボリュームがほぼゼロの場合
            {
                PeakValue = 0f;
            }
            else // ミュート時: ボリュームの影響を受けず、生ピーク値のみ表示
            {
                // 音は鳴らないが、アプリが音源を出力していることを示すために生ピーク値をそのまま表示
                PeakValue = maxPeak * Volume;
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
        /// プロセスIDから「製品名/説明」を取得 (例: chrome.exe -> Google Chrome)
        /// </summary>
        public static string GetFormattedDisplayName(int pid)
        {
            try
            {
                var process = Process.GetProcessById(pid);

                // MainModuleへのアクセスは権限が必要な場合があるためtryで囲む
                try
                {
                    if (process.MainModule?.FileVersionInfo != null)
                    {
                        string description = process.MainModule.FileVersionInfo.FileDescription;
                        if (!string.IsNullOrWhiteSpace(description))
                        {
                            return description;
                        }
                    }
                }
                catch { /* 権限不足時は無視 */ }

                return process.ProcessName;
            }
            catch
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