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
    /// <summary>
    /// アプリケーションのメインロジックを担うViewModel。
    /// オーディオセッションの管理、更新、タイマー制御を行います。
    /// </summary>
    public class MixerViewModel : INotifyPropertyChanged, IDisposable
    {
        // UIにバインドされるセッションリスト
        public ObservableCollection<AudioSessionModel> AudioSessions { get; set; } = new ObservableCollection<AudioSessionModel>();

        // システム音など、除外したいセッション名
        private static readonly string[] ExcludedNames = { "System Sounds", "System Idle Process" };

        // NAudio CoreAudioApi オブジェクト
        private MMDevice _device;
        private AudioSessionManager _sessionManager;

        // ピークメーター更新用タイマー
        private DispatcherTimer _peakMeterTimer;

        // 更新コマンド
        public ICommand RefreshCommand { get; private set; }

        /// <summary>
        /// コンストラクタ。初期化処理を行います。
        /// </summary>
        public MixerViewModel()
        {
            // コマンドの初期化
            RefreshCommand = new RelayCommand(ExecuteRefresh);

            // セッションの初期ロード
            InitializeAudioSessions();

            // メーター更新タイマーの開始
            SetupPeakMeterTimer();
        }

        /// <summary>
        /// オーディオデバイスとセッションマネージャーを初期化し、セッションをロードします。
        /// </summary>
        private void InitializeAudioSessions()
        {
            try
            {
                var deviceEnumerator = new MMDeviceEnumerator();
                // 既定の再生デバイス（マルチメディア）を取得
                _device = deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                _sessionManager = _device.AudioSessionManager;

                LoadSessions();
            }
            catch
            {
                // デバイスが見つからない場合の処理 (エラー表示など本来は必要)
            }
        }

        /// <summary>
        /// 現在のアクティブなセッションを取得し、リストを更新します。
        /// </summary>
        public void LoadSessions()
        {
            if (_sessionManager == null) return;

            // 既存のセッションを破棄してからクリア
            foreach (var session in AudioSessions)
            {
                session.Dispose();
            }
            AudioSessions.Clear();

            // セッションリストを最新化
            _sessionManager.RefreshSessions();

            // 重複チェック用セット (PID)
            var trackedPids = new System.Collections.Generic.HashSet<int>();

            for (int i = 0; i < _sessionManager.Sessions.Count; i++)
            {
                var session = _sessionManager.Sessions[i];
                int pid = (int)session.GetProcessID;

                // フィルタリング: 有効なPID かつ (Active または Inactive)
                if (pid > 0 &&
                   (session.State == AudioSessionState.AudioSessionStateActive ||
                    session.State == AudioSessionState.AudioSessionStateInactive))
                {
                    // 重複排除
                    if (trackedPids.Contains(pid)) continue;

                    var model = new AudioSessionModel(session);

                    // 名前フィルタリング
                    if (string.IsNullOrWhiteSpace(model.DisplayName) || ExcludedNames.Contains(model.DisplayName))
                    {
                        model.Dispose(); // 使わないので破棄
                        continue;
                    }

                    // リストに追加
                    AudioSessions.Add(model);
                    trackedPids.Add(pid);
                }
            }
        }

        /// <summary>
        /// ピークメーターのアニメーションタイマーを設定します。
        /// </summary>
        private void SetupPeakMeterTimer()
        {
            _peakMeterTimer = new DispatcherTimer();
            // 約60FPS (16ms) で更新
            _peakMeterTimer.Interval = TimeSpan.FromMilliseconds(16);
            _peakMeterTimer.Tick += PeakMeterTimer_Tick;
            _peakMeterTimer.Start();
        }

        /// <summary>
        /// タイマーイベント。全セッションのピーク値を更新します。
        /// </summary>
        private void PeakMeterTimer_Tick(object sender, EventArgs e)
        {
            foreach (var session in AudioSessions)
            {
                session.UpdatePeakValue();
            }
        }

        /// <summary>
        /// 更新ボタン押下時の処理
        /// </summary>
        private void ExecuteRefresh(object parameter)
        {
            LoadSessions();
        }

        /// <summary>
        /// リソースの解放
        /// </summary>
        public void Dispose()
        {
            _peakMeterTimer?.Stop();

            foreach (var session in AudioSessions)
            {
                session.Dispose();
            }

            if (_device != null)
            {
                _device.Dispose();
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}