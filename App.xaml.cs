using System;
using System.Windows;
using WinForms = System.Windows.Forms;

namespace inuMixer
{
    public partial class App : Application
    {
        private WinForms.NotifyIcon _notifyIcon;

        private void App_Startup(object sender, StartupEventArgs e)
        {
            InitializeTrayIcon();

            var mainWindow = new MainWindow();
            MainWindow = mainWindow;
            mainWindow.Show();
        }

        private void InitializeTrayIcon()
        {
            _notifyIcon = new WinForms.NotifyIcon();

            // アイコンの設定 (mixer.ico をリソースから読み込む)
            try
            {
                var stream = GetResourceStream(
                    new Uri("pack://application:,,,/inuMixer;component/Resources/mixer.ico"))?.Stream;
                _notifyIcon.Icon = stream != null
                    ? new System.Drawing.Icon(stream)
                    : System.Drawing.SystemIcons.Application;
            }
            catch
            {
                _notifyIcon.Icon = System.Drawing.SystemIcons.Application;
            }

            _notifyIcon.Text = "inuMixer";
            _notifyIcon.Visible = true;

            // コンテキストメニュー
            var menu = new WinForms.ContextMenuStrip();

            var showItem = new WinForms.ToolStripMenuItem("表示 / 非表示");
            showItem.Click += (s, args) => ToggleMainWindow();
            menu.Items.Add(showItem);

            menu.Items.Add(new WinForms.ToolStripSeparator());

            var exitItem = new WinForms.ToolStripMenuItem("終了");
            exitItem.Click += (s, args) => ExitApplication();
            menu.Items.Add(exitItem);

            _notifyIcon.ContextMenuStrip = menu;

            // ダブルクリック: 表示 / 非表示の切り替え
            _notifyIcon.DoubleClick += (s, args) => ToggleMainWindow();
        }

        private void ToggleMainWindow()
        {
            if (MainWindow == null) return;

            if (MainWindow.IsVisible)
            {
                MainWindow.Hide();
            }
            else
            {
                MainWindow.Show();
                if (MainWindow.WindowState == WindowState.Minimized)
                    MainWindow.WindowState = WindowState.Normal;
                MainWindow.Activate();
            }
        }

        /// <summary>
        /// トレイの「終了」から呼び出す。アイコンを解放してウィンドウを閉じる。
        /// </summary>
        public void ExitApplication()
        {
            _notifyIcon?.Dispose();
            _notifyIcon = null;
            // MainWindow.Closed イベントが設定を保存し、OnLastWindowClose でアプリが終了する
            MainWindow?.Close();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _notifyIcon?.Dispose();
            base.OnExit(e);
        }
    }
}
