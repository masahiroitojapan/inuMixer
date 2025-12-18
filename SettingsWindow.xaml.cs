using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;

namespace inuMixer
{
    public partial class SettingsWindow : Window
    {
        public class AppVisibilityItem
        {
            public string Name { get; set; }
            public bool IsVisible { get; set; }
        }

        public List<AppVisibilityItem> AppItems { get; set; }

        public SettingsWindow(List<string> currentActiveApps)
        {
            InitializeComponent();

            // アセンブリからバージョン情報を取得して表示
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            VersionText.Text = $"v{version.Major}.{version.Minor}.{version.Build}";

            AppItems = new List<AppVisibilityItem>();

            // 設定読み込み
            string hiddenAppsStr = "";
            try { hiddenAppsStr = global::inuMixer.Properties.Settings.Default.HiddenApps; } catch { }

            var hiddenApps = string.IsNullOrEmpty(hiddenAppsStr)
                ? new HashSet<string>()
                : new HashSet<string>(hiddenAppsStr.Split(','));

            var allApps = new HashSet<string>(currentActiveApps);
            foreach (var hidden in hiddenApps) { if (!string.IsNullOrWhiteSpace(hidden)) allApps.Add(hidden); }

            foreach (var appName in allApps.OrderBy(x => x))
            {
                AppItems.Add(new AppVisibilityItem { Name = appName, IsVisible = !hiddenApps.Contains(appName) });
            }
            AppListControl.ItemsSource = AppItems;
        }

        private void CheckUpdate_Click(object sender, RoutedEventArgs e)
        {
            // アップデート確認のロジック (例: GitHubのReleasesページを開く)
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "https://github.com/masahiroitojapan/inuMixer/releases",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show("ブラウザを開けませんでした: " + ex.Message);
            }
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            var hiddenList = AppItems.Where(x => !x.IsVisible).Select(x => x.Name).ToList();
            string saveStr = string.Join(",", hiddenList);

            try
            {
                global::inuMixer.Properties.Settings.Default.HiddenApps = saveStr;
                global::inuMixer.Properties.Settings.Default.Save();
            }
            catch { }

            this.DialogResult = true;
            this.Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = false;
            this.Close();
        }
    }
}