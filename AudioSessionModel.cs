using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Interop;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace inuMixer
{
    /// <summary>
    /// 個々のアプリケーション（オーディオセッション）のデータと状態を表すモデルクラス。
    /// 音量、ミュート、ピーク値、アイコンなどの情報を保持・管理します。
    /// </summary>
    public class AudioSessionModel : INotifyPropertyChanged, IDisposable
    {
        // CoreAudio API のセッションコントロール
        private readonly AudioSessionControl _sessionControl;
        // 音量メーター情報
        private readonly AudioMeterInformation _meterInformation;

        /// <summary>
        /// コンストラクタ。CoreAudioセッションからモデルを生成します。
        /// </summary>
        public AudioSessionModel(AudioSessionControl session)
        {
            _sessionControl = session;
            _meterInformation = session.AudioMeterInformation;

            // プロセス情報の初期化
            ProcessId = (int)session.GetProcessID;
            DisplayName = GetProcessName(ProcessId);
            Icon = GetProcessIcon(ProcessId);
        }

        // ==========================================================
        // プロパティ (UIバインディング用)
        // ==========================================================

        public int ProcessId { get; }
        public string DisplayName { get; }
        public ImageSource Icon { get; }

        /// <summary>
        /// マスター音量 (0.0 - 1.0)。変更時はシステムに反映します。
        /// </summary>
        public float Volume
        {
            get => _sessionControl.SimpleAudioVolume.Volume;
            set
            {
                if (_sessionControl.SimpleAudioVolume.Volume != value)
                {
                    _sessionControl.SimpleAudioVolume.Volume = value;
                    OnPropertyChanged(nameof(Volume));
                    OnPropertyChanged(nameof(VolumePercent));
                }
            }
        }

        /// <summary>
        /// 音量のパーセント表示 (0 - 100)
        /// </summary>
        public int VolumePercent => (int)(Volume * 100);

        /// <summary>
        /// ミュート状態。変更時はシステムに反映します。
        /// </summary>
        public bool IsMuted
        {
            get => _sessionControl.SimpleAudioVolume.Mute;
            set
            {
                if (_sessionControl.SimpleAudioVolume.Mute != value)
                {
                    _sessionControl.SimpleAudioVolume.Mute = value;
                    OnPropertyChanged(nameof(IsMuted));
                }
            }
        }

        // 内部で使用するピーク値フィールド
        private float _peakValue;

        /// <summary>
        /// 現在の音量ピーク値 (0.0 - 1.0)。メーター表示用。
        /// </summary>
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

        // ==========================================================
        // メソッド
        // ==========================================================

        /// <summary>
        /// 現在のピーク値を取得し、プロパティを更新します。
        /// Timerから定期的に呼び出されます。
        /// </summary>
        public void UpdatePeakValue()
        {
            try
            {
                PeakValue = _meterInformation.MasterPeakValue;
            }
            catch
            {
                // セッションが切断されている場合などは無視
            }
        }

        /// <summary>
        /// リソースを解放します。
        /// </summary>
        public void Dispose()
        {
            // AudioSessionControl自体は明示的なDisposeが必要なケースは少ないが、
            // 参照を外すなどのクリーンアップを行う場所として用意
            // NAudioのオブジェクトはガベージコレクションに任せても概ね問題ない
            _sessionControl?.Dispose();
        }

        // ==========================================================
        // 内部ヘルパー (プロセス情報の取得)
        // ==========================================================

        private string GetProcessName(int pid)
        {
            try
            {
                var process = Process.GetProcessById(pid);
                return process.ProcessName;
            }
            catch
            {
                return "不明なアプリ";
            }
        }

        private ImageSource GetProcessIcon(int pid)
        {
            // IconHelperクラスを使用して強力にアイコンを取得
            return IconHelper.GetIconFromProcess(pid);
        }

        // ==========================================================
        // INotifyPropertyChanged 実装
        // ==========================================================

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}