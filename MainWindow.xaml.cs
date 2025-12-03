using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace VolMixer
{
    /// <summary>
    /// MainWindow.xaml の相互作用ロジック。
    /// UIイベント処理、設定のロード/保存、ウィンドウ操作を担当します。
    /// </summary>
    public partial class MainWindow : Window
    {
        private MixerViewModel _viewModel;

        public MainWindow()
        {
            InitializeComponent();

            // アプリ設定（ウィンドウ位置・透明度など）のロード
            LoadSettings();

            // ViewModelの初期化とバインド
            _viewModel = new MixerViewModel();
            DataContext = _viewModel;

            // イベント購読
            this.Closed += MainWindow_Closed;
            this.MouseDown += MainWindow_MouseDown;
        }

        /// <summary>
        /// 設定ファイルからウィンドウの状態を復元します。
        /// </summary>
        private void LoadSettings()
        {
            try
            {
                // namespaceの競合を避けるため global:: を使用してアクセス
                var settings = global::VolMixer.Properties.Settings.Default;

                // 位置とサイズが有効な値であれば復元
                if (settings.WindowLeft > -1 && settings.WindowHeight > 0)
                {
                    this.Left = settings.WindowLeft;
                    this.Top = settings.WindowTop;
                    this.Height = settings.WindowHeight;
                }

                this.Topmost = settings.IsAlwaysOnTop;

                // 透明度の安全策（見えなくならないように0.2を下限とする）
                if (settings.WindowOpacity < 0.2) settings.WindowOpacity = 1.0;
                this.Opacity = settings.WindowOpacity;
            }
            catch
            {
                // 初回起動時や設定破損時はデフォルト値を使用するため、エラーは無視して続行
            }
        }

        /// <summary>
        /// アプリ終了時に呼ばれます。リソース解放と設定保存を行います。
        /// </summary>
        private void MainWindow_Closed(object sender, EventArgs e)
        {
            // ViewModelのリソース（CoreAudioなど）を解放
            _viewModel?.Dispose();

            // 現在のウィンドウ状態を保存
            try
            {
                var settings = global::VolMixer.Properties.Settings.Default;

                settings.WindowLeft = this.Left;
                settings.WindowTop = this.Top;
                settings.WindowHeight = this.Height;
                settings.IsAlwaysOnTop = this.Topmost;
                settings.WindowOpacity = this.Opacity;
                settings.Save();
            }
            catch { }
        }

        /// <summary>
        /// タイトルバーがないため、ウィンドウ上の任意の場所でのドラッグ移動を可能にします。
        /// </summary>
        private void MainWindow_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                try { this.DragMove(); } catch { }
            }
        }

        // --- ウィンドウ操作ボタン ---

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = WindowState.Minimized;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        // --- マウスホイール/ボタン操作 (UIロジック) ---

        /// <summary>
        /// オーディオセッション上でのマウスホイール操作（音量調整）
        /// </summary>
        private void Border_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is Border border && border.DataContext is AudioSessionModel session)
            {
                // 5% 単位で増減
                float change = 0.05f;
                if (e.Delta > 0)
                {
                    if (session.Volume + change <= 1.0f) session.Volume += change;
                    else session.Volume = 1.0f;
                }
                else
                {
                    if (session.Volume - change >= 0.0f) session.Volume -= change;
                    else session.Volume = 0.0f;
                }
                e.Handled = true; // イベント伝播を停止
            }
        }

        /// <summary>
        /// 透明度スライダー上でのマウスホイール操作
        /// </summary>
        private void OpacitySlider_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is Slider slider)
            {
                // 2% 単位で増減
                double change = 0.02;
                double currentValue = slider.Value;

                if (e.Delta > 0)
                {
                    slider.Value = Math.Min(slider.Maximum, currentValue + change);
                }
                else
                {
                    slider.Value = Math.Max(slider.Minimum, currentValue - change);
                }
                e.Handled = true;
            }
        }

        /// <summary>
        /// マウス中クリックでのミュート切り替え
        /// </summary>
        private void Border_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Middle)
            {
                if (sender is Border border && border.DataContext is AudioSessionModel session)
                {
                    session.IsMuted = !session.IsMuted;
                }
            }
        }
    }
}