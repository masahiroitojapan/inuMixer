using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VolMixer
{
    /// <summary>
    /// Windows API (Shell32, Kernel32) を利用して、アプリケーションのアイコンを高精度に取得するヘルパークラス。
    /// 通常の方法では取得できないUWPアプリや管理者権限アプリのアイコン取得をサポートします。
    /// </summary>
    public static class IconHelper
    {
        // ==========================================================
        // Windows API (P/Invoke) 定義
        // ==========================================================

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("kernel32.dll")]
        private static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryFullProcessImageName(IntPtr hProcess, int dwFlags, StringBuilder lpExeName, ref int lpdwSize);

        // API定数
        private const uint SHGFI_ICON = 0x100;
        private const uint SHGFI_LARGEICON = 0x0; // 32x32アイコン
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        // ==========================================================
        // 公開メソッド
        // ==========================================================

        /// <summary>
        /// 指定されたプロセスIDから、関連付けられたアイコン画像を取得します。
        /// </summary>
        /// <param name="pid">プロセスID</param>
        /// <returns>WPFで表示可能なImageSource。取得失敗時はnull。</returns>
        public static ImageSource GetIconFromProcess(int pid)
        {
            try
            {
                // 1. プロセスの実行ファイルパスを取得 (MainModuleより強力なAPIを使用)
                string path = GetProcessPath(pid);
                if (string.IsNullOrEmpty(path)) return null;

                // 2. パスからアイコンを取得 (Shell APIを使用)
                return GetIconFromPath(path);
            }
            catch
            {
                // アイコン取得に失敗してもアプリを落とさない
                return null;
            }
        }

        // ==========================================================
        // 内部ヘルパーメソッド
        // ==========================================================

        /// <summary>
        /// プロセスIDから実行ファイルのフルパスを取得します。
        /// </summary>
        private static string GetProcessPath(int pid)
        {
            try
            {
                // まずは標準的な方法 (.NET) を試す
                var process = Process.GetProcessById(pid);
                try { return process.MainModule.FileName; } catch { }

                // 権限不足などで取得できない場合、Kernel32 APIを使って取得を試みる (ゲーム等に有効)
                return GetProcessPathByApi(pid);
            }
            catch { return null; }
        }

        /// <summary>
        /// QueryFullProcessImageName APIを使用してパスを取得します。
        /// </summary>
        private static string GetProcessPathByApi(int pid)
        {
            var buffer = new StringBuilder(1024);
            int size = buffer.Capacity;

            // プロセスハンドルを開く（情報の参照権限のみ要求）
            IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);

            if (hProcess != IntPtr.Zero)
            {
                try
                {
                    if (QueryFullProcessImageName(hProcess, 0, buffer, ref size))
                    {
                        return buffer.ToString();
                    }
                }
                finally
                {
                    // ハンドルは必ず閉じる
                    CloseHandle(hProcess);
                }
            }
            return null;
        }

        /// <summary>
        /// ファイルパスからアイコンハンドルを取得し、WPFのImageSourceに変換します。
        /// </summary>
        private static ImageSource GetIconFromPath(string path)
        {
            IntPtr hIcon = IntPtr.Zero;
            try
            {
                SHFILEINFO shinfo = new SHFILEINFO();

                // Shell APIを呼び出してアイコンハンドルを取得
                SHGetFileInfo(path, 0, ref shinfo, (uint)Marshal.SizeOf(shinfo), SHGFI_ICON | SHGFI_LARGEICON);

                if (shinfo.hIcon == IntPtr.Zero) return null;
                hIcon = shinfo.hIcon;

                // GDIアイコンハンドルをWPFビットマップに変換
                var imageSource = Imaging.CreateBitmapSourceFromHIcon(
                    hIcon,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());

                // UIスレッド以外でも使用できるようにフリーズ（読み取り専用化）する
                imageSource.Freeze();
                return imageSource;
            }
            catch
            {
                return null;
            }
            finally
            {
                // メモリリーク防止のため、アンマネージドリソース(アイコンハンドル)を必ず破棄
                if (hIcon != IntPtr.Zero) DestroyIcon(hIcon);
            }
        }
    }
}