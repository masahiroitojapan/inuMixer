using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace inuMixer
{
    /// <summary>
    /// Windows 未公開 API (IAudioPolicyConfig) でアプリごとの出力デバイスを設定。
    ///
    /// EarTrumpet (https://github.com/File-New-Project/EarTrumpet) の実装を参考に
    /// V2 インターフェース (build 22000+) の正しい vtable レイアウトを使用。
    ///
    /// V2 (ab3d4648 / Windows 21H2+) vtable レイアウト:
    ///   [0-2]  IUnknown
    ///   [3-5]  IInspectable
    ///   [6-24] 19 個のプレースホルダーメソッド
    ///   [25]   SetPersistedDefaultAudioEndpoint(pThis, pid, flow, role, hDeviceId)
    ///   [26]   GetPersistedDefaultAudioEndpoint
    ///   [27]   ClearAllPersistedApplicationDefaultEndpoints
    ///
    /// V1 (2a59116d / それ以前) vtable レイアウト:
    ///   [0-5]  IUnknown + IInspectable
    ///   [6-12] 旧メソッド群
    ///   [13]   SetAudioEndpointForProcess(pThis, pid, hDeviceId, role)
    ///
    /// デバイスID形式 (EarTrumpet と同じ):
    ///   \\?\SWD#MMDEVAPI#{NAudio device.ID}#{e6327cad-dcec-4949-ae8a-991e976a79d2}
    /// </summary>
    internal static class AudioPolicyConfigService
    {
        private const string AudioPolicyConfigClass = "Windows.Media.Internal.AudioPolicyConfig";

        // デバイスID変換定数 (EarTrumpet AudioPolicyConfig.cs より)
        private const string MMDEVAPI_TOKEN = @"\\?\SWD#MMDEVAPI#";
        private const string DEVINTERFACE_AUDIO_RENDER = "#{e6327cad-dcec-4949-ae8a-991e976a79d2}";

        // ── インターフェース GUID ─────────────────────────────────────────
        private static readonly Guid GuidV2 = new("ab3d4648-e242-459f-b02f-541c70306324");
        private static readonly Guid GuidV1 = new("2a59116d-6c4f-45e0-a74f-707e3fef9258");

        // ── 関数ポインタ デリゲート ──────────────────────────────────────
        // V2 slot 25: SetPersistedDefaultAudioEndpoint(pThis, pid, flow:EDataFlow, role:ERole, hDeviceId:HSTRING)
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int SetV2Delegate(IntPtr pThis, uint pid, int flow, int role, IntPtr hDeviceId);

        // V1 slot 13: SetAudioEndpointForProcess(pThis, pid, hDeviceId:HSTRING, role:ERole)
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int SetV1Delegate(IntPtr pThis, uint pid, IntPtr hDeviceId, int role);

        // ── HSTRING / COM P/Invoke ────────────────────────────────────────
        [DllImport("combase.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int WindowsCreateString(
            [MarshalAs(UnmanagedType.LPWStr)] string src, int length, out IntPtr hstr);

        [DllImport("combase.dll", ExactSpelling = true)]
        private static extern int WindowsDeleteString(IntPtr hstr);

        [DllImport("combase.dll", ExactSpelling = true)]
        private static extern int RoGetActivationFactory(
            IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

        // ── 状態 ─────────────────────────────────────────────────────────
        private static IntPtr _ptr = IntPtr.Zero;
        private static bool   _isV2;
        private static bool   _initialized;

        // ── ユーティリティ ────────────────────────────────────────────────
        private static IntPtr MakeHString(string s)
        {
            if (string.IsNullOrEmpty(s)) return IntPtr.Zero;
            WindowsCreateString(s, s.Length, out IntPtr h);
            return h;
        }

        private static IntPtr ReadVtableSlot(IntPtr pObj, int slot) =>
            Marshal.ReadIntPtr(Marshal.ReadIntPtr(pObj) + slot * IntPtr.Size);

        /// <summary>vtable アドレスを安全に読み取りログに出力 (関数は呼ばない)。</summary>
        private static void DumpVtable(IntPtr pObj)
        {
            try
            {
                IntPtr vtable = Marshal.ReadIntPtr(pObj);
                Debug.WriteLine($"[AudioPolicy] vtable base = 0x{vtable:X16}");
                for (int i = 22; i <= 30; i++)
                {
                    IntPtr fn = Marshal.ReadIntPtr(vtable + i * IntPtr.Size);
                    Debug.WriteLine($"[AudioPolicy]   slot[{i}] = 0x{fn:X16}");
                }
            }
            catch (Exception ex) { Debug.WriteLine($"[AudioPolicy] vtable read error: {ex.Message}"); }
        }

        /// <summary>NAudio の device.ID を IAudioPolicyConfig が期待する形式に変換。</summary>
        private static string WrapDeviceId(string deviceId) =>
            $"{MMDEVAPI_TOKEN}{deviceId}{DEVINTERFACE_AUDIO_RENDER}";

        // ── 初期化 ────────────────────────────────────────────────────────
        private static void EnsureInitialized()
        {
            if (_initialized) return;
            _initialized = true;

            Debug.WriteLine($"[AudioPolicy] OS Build: {Environment.OSVersion.Version.Build}");

            IntPtr hName = MakeHString(AudioPolicyConfigClass);
            if (hName == IntPtr.Zero) { Debug.WriteLine("[AudioPolicy] HSTRING 作成失敗"); return; }

            try
            {
                // V2 を優先 (Windows 21H2+ / build 22000+)
                var iid = GuidV2;
                int hr = RoGetActivationFactory(hName, ref iid, out IntPtr p);
                Debug.WriteLine($"[AudioPolicy] V2 hr=0x{hr:X8}  ptr=0x{p:X16}");
                if (hr == 0 && p != IntPtr.Zero)
                {
                    _ptr = p;
                    _isV2 = true;
                    DumpVtable(p);
                    Debug.WriteLine("[AudioPolicy] → V2 使用 (SetPersistedDefaultAudioEndpoint slot=25)");
                    return;
                }

                // V1 フォールバック (旧 Windows)
                iid = GuidV1;
                hr = RoGetActivationFactory(hName, ref iid, out p);
                Debug.WriteLine($"[AudioPolicy] V1 hr=0x{hr:X8}  ptr=0x{p:X16}");
                if (hr == 0 && p != IntPtr.Zero)
                {
                    _ptr = p;
                    _isV2 = false;
                    Debug.WriteLine("[AudioPolicy] → V1 使用 (SetAudioEndpointForProcess slot=13)");
                    return;
                }

                Debug.WriteLine("[AudioPolicy] ★ 初期化失敗");
            }
            catch (Exception ex) { Debug.WriteLine($"[AudioPolicy] 初期化例外: {ex.Message}"); }
            finally { WindowsDeleteString(hName); }
        }

        public static bool IsSupported
        {
            get { EnsureInitialized(); return _ptr != IntPtr.Zero; }
        }

        /// <summary>
        /// 指定プロセスの出力デバイスを設定する。
        /// deviceId が空の場合はシステム規定デバイスに戻す (EarTrumpet 方式)。
        /// </summary>
        public static bool TrySetEndpointForProcess(uint pid, string deviceId)
        {
            EnsureInitialized();
            if (_ptr == IntPtr.Zero) return false;

            // deviceId が空 → HSTRING null = システム規定に戻す (EarTrumpet と同じ方式)
            IntPtr hDev = IntPtr.Zero;
            if (!string.IsNullOrEmpty(deviceId))
                hDev = MakeHString(WrapDeviceId(deviceId));

            Debug.WriteLine($"[AudioPolicy] TrySet pid={pid}  v2={_isV2}  hasDevice={hDev != IntPtr.Zero}");

            try
            {
                if (_isV2)
                {
                    // V2: SetPersistedDefaultAudioEndpoint(pThis, pid, flow=eRender(0), role, hDeviceId)
                    var fn = Marshal.GetDelegateForFunctionPointer<SetV2Delegate>(ReadVtableSlot(_ptr, 25));
                    int hr0 = fn(_ptr, pid, 0 /*eRender*/, 0 /*eConsole*/,     hDev);
                    int hr1 = fn(_ptr, pid, 0 /*eRender*/, 1 /*eMultimedia*/,  hDev);
                    Debug.WriteLine($"[AudioPolicy] Set V2: eConsole=0x{hr0:X8}  eMultimedia=0x{hr1:X8}");
                    return hr0 >= 0 && hr1 >= 0;
                }
                else
                {
                    // V1: SetAudioEndpointForProcess(pThis, pid, hDeviceId, role)
                    var fn = Marshal.GetDelegateForFunctionPointer<SetV1Delegate>(ReadVtableSlot(_ptr, 13));
                    int hr0 = fn(_ptr, pid, hDev, 0 /*eConsole*/);
                    int hr1 = fn(_ptr, pid, hDev, 1 /*eMultimedia*/);
                    int hr2 = fn(_ptr, pid, hDev, 2 /*eCommunications*/);
                    Debug.WriteLine($"[AudioPolicy] Set V1: C=0x{hr0:X8}  M=0x{hr1:X8}  Cm=0x{hr2:X8}");
                    return hr1 >= 0;
                }
            }
            catch (Exception ex) { Debug.WriteLine($"[AudioPolicy] TrySet 例外: {ex.Message}"); }
            finally { if (hDev != IntPtr.Zero) WindowsDeleteString(hDev); }
            return false;
        }

        /// <summary>指定プロセスの出力デバイスをシステム規定に戻す。</summary>
        public static bool TryResetEndpointForProcess(uint pid)
        {
            // EarTrumpet と同じ方式: deviceId = null (HSTRING null) で Set → デフォルトに戻る
            Debug.WriteLine($"[AudioPolicy] TryReset pid={pid}");
            return TrySetEndpointForProcess(pid, string.Empty);
        }
    }
}
