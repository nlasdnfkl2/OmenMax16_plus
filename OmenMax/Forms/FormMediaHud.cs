#nullable enable
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace OmenMax16.Forms;

public sealed class FormMediaHud : Form
{
    private enum ScrollPhase
    {
        Idle,
        PauseStart,
        Scrolling,
        PauseEnd
    }

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(4)
    };

    private bool _isDark;
    private readonly PictureBox _picThumb;
    private readonly MarqueeLabel _lblTitle;
    private readonly MarqueeLabel _lblArtist;
    private readonly Button _btnPrev;
    private readonly Button _btnPlayPause;
    private readonly Button _btnNext;
    private readonly System.Windows.Forms.Timer _pollTimer;
    private readonly System.Windows.Forms.Timer _marqueeTimer;

    private GlobalSystemMediaTransportControlsSessionManager? _smtcManager;
    private GlobalSystemMediaTransportControlsSession? _smtcSession;
    private bool _smtcCheckedOnce;
    private bool _smtcAvailable;

    private string _lastLookupKey = "";
    private bool _isFetchingMeta;
    private bool _manualPlayingState;

    private const int CornerRadius = 22;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private const byte VK_MEDIA_NEXT_TRACK = 0xB0;
    private const byte VK_MEDIA_PREV_TRACK = 0xB1;
    private const byte VK_MEDIA_PLAY_PAUSE = 0xB3;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    #region Core Audio Session

    private enum EDataFlow { eRender = 0, eCapture = 1, eAll = 2 }
    private enum ERole { eConsole = 0, eMultimedia = 1, eCommunications = 2 }
    private enum AudioSessionState { AudioSessionStateInactive = 0, AudioSessionStateActive = 1, AudioSessionStateExpired = 2 }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorCom { }

    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(EDataFlow dataFlow, uint dwStateMask, out IntPtr ppDevices);
        int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice ppEndpoint);
    }

    [Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, uint dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
    }

    [Guid("77AA99A0-1BD6-484F-8BC0-2C6566038437"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        int NotImpl1();
        int NotImpl2();
        int GetSessionEnumerator(out IAudioSessionEnumerator SessionEnum);
    }

    [Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        int GetCount(out int SessionCount);
        int GetSession(int SessionCount, out IAudioSessionControl Session);
    }

    [Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl
    {
        int GetState(out AudioSessionState pRetVal);
    }

    [Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        int GetState(out AudioSessionState pRetVal);
        int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
        int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string Value, ref Guid EventContext);
        int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
        int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string Value, ref Guid EventContext);
        int GetGroupingParam(out Guid pRetVal);
        int SetGroupingParam(ref Guid Override, ref Guid EventContext);
        int RegisterAudioSessionNotification(IntPtr NewNotifications);
        int UnregisterAudioSessionNotification(IntPtr NewNotifications);
        int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
        int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
        int GetProcessId(out uint pRetVal);
        int IsSystemSoundsSession();
        int SetDuckingPreference(bool optOut);
    }

    private static bool? IsAnyActiveAudioSession(string targetProcName)
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IAudioSessionManager2? mgr = null;
        IAudioSessionEnumerator? sessionEnum = null;

        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            if (enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out device) != 0 || device == null)
                return null;

            Guid iidMgr = typeof(IAudioSessionManager2).GUID;
            if (device.Activate(ref iidMgr, 23, IntPtr.Zero, out object objMgr) != 0 || objMgr is not IAudioSessionManager2 m)
                return null;
            mgr = m;

            if (mgr.GetSessionEnumerator(out sessionEnum) != 0 || sessionEnum == null)
                return null;

            sessionEnum.GetCount(out int count);
            for (int i = 0; i < count; i++)
            {
                if (sessionEnum.GetSession(i, out IAudioSessionControl ctrl) != 0) continue;
                ctrl.GetState(out AudioSessionState state);
                if (state != AudioSessionState.AudioSessionStateActive) continue;

                if (string.IsNullOrWhiteSpace(targetProcName))
                    return true;

                if (ctrl is IAudioSessionControl2 ctrl2)
                {
                    ctrl2.GetProcessId(out uint pid);
                    if (pid > 0)
                    {
                        try
                        {
                            string name = Process.GetProcessById((int)pid).ProcessName;
                            if (string.Equals(name, targetProcName, StringComparison.OrdinalIgnoreCase))
                                return true;
                        }
                        catch { }
                    }
                }
            }

            return false;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (sessionEnum != null) Marshal.ReleaseComObject(sessionEnum);
            if (mgr != null) Marshal.ReleaseComObject(mgr);
            if (device != null) Marshal.ReleaseComObject(device);
            if (enumerator != null) Marshal.ReleaseComObject(enumerator);
        }
    }

    #endregion

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= WS_EX_NOACTIVATE;
            return cp;
        }
    }

    public FormMediaHud(bool isDark)
    {
        _isDark = isDark;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(300, 116);

        BackColor = Color.FromArgb(255, 1, 254);
        TransparencyKey = BackColor;
        Font = new Font("Segoe UI", 9f);

        Rectangle wa = Screen.PrimaryScreen?.WorkingArea ?? Screen.FromControl(this).WorkingArea;
        Location = new Point(wa.Right - Width - 24, wa.Top + 276);

        _picThumb = new PictureBox
        {
            Bounds = new Rectangle(14, 18, 80, 80),
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.FromArgb(30, 32, 42)
        };

        _lblTitle = new MarqueeLabel
        {
            Bounds = new Rectangle(104, 17, 182, 22),
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            BackColor = Color.Transparent,
            DisplayText = "미디어 대기 중"
        };

        _lblArtist = new MarqueeLabel
        {
            Bounds = new Rectangle(104, 41, 182, 18),
            Font = new Font("Segoe UI", 8.2f),
            BackColor = Color.Transparent,
            DisplayText = "-"
        };

        _btnPrev = MakeControlButton(new Rectangle(104, 68, 56, 28), "⏮");
        _btnPrev.Click += async (_, _) => await SendPrevAsync();

        _btnPlayPause = MakeControlButton(new Rectangle(166, 68, 62, 28), "▶");
        _btnPlayPause.Click += async (_, _) => await SendPlayPauseAsync();

        _btnNext = MakeControlButton(new Rectangle(234, 68, 52, 28), "⏭");
        _btnNext.Click += async (_, _) => await SendNextAsync();

        Controls.AddRange(new Control[]
        {
            _picThumb, _lblTitle, _lblArtist, _btnPrev, _btnPlayPause, _btnNext
        });

        ApplyRoundedRegion();
        ApplyThemeVisuals();

        Load += async (_, _) =>
        {
            ApplyAcrylic();
            await TryInitSmtcOnceAsync();
            await RefreshMediaWindowTitleAsync();
        };

        _pollTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _pollTimer.Tick += async (_, _) => await RefreshMediaWindowTitleAsync();
        _pollTimer.Start();

        _marqueeTimer = new System.Windows.Forms.Timer { Interval = 30 };
        _marqueeTimer.Tick += (_, _) =>
        {
            if (!Visible) return;
            _lblTitle.StepMarquee(_marqueeTimer.Interval);
            _lblArtist.StepMarquee(_marqueeTimer.Interval);
        };
        _marqueeTimer.Start();
    }

    private async Task TryInitSmtcOnceAsync()
    {
        if (_smtcCheckedOnce) return;
        _smtcCheckedOnce = true;

        try
        {
            var reqTask = GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask();
            var done = await Task.WhenAny(reqTask, Task.Delay(700));
            if (done == reqTask)
            {
                _smtcManager = await reqTask;
                _smtcAvailable = _smtcManager != null;
            }
        }
        catch
        {
            _smtcAvailable = false;
        }
    }

    private static void PressMediaKeyFallback(byte vk)
    {
        keybd_event(vk, 0, KEYEVENTF_EXTENDEDKEY, UIntPtr.Zero);
        keybd_event(vk, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    private async Task SendPrevAsync()
    {
        try
        {
            if (_smtcAvailable && _smtcManager != null)
            {
                var session = _smtcSession ?? _smtcManager.GetCurrentSession();
                if (session != null)
                {
                    var t = session.TrySkipPreviousAsync().AsTask();
                    if (await Task.WhenAny(t, Task.Delay(500)) == t && await t)
                        return;
                }
            }
        }
        catch { }

        PressMediaKeyFallback(VK_MEDIA_PREV_TRACK);
    }

    private async Task SendPlayPauseAsync()
    {
        try
        {
            if (_smtcAvailable && _smtcManager != null)
            {
                var session = _smtcSession ?? _smtcManager.GetCurrentSession();
                if (session != null)
                {
                    var t = session.TryTogglePlayPauseAsync().AsTask();
                    if (await Task.WhenAny(t, Task.Delay(500)) == t && await t)
                    {
                        await Task.Delay(150);
                        await RefreshMediaWindowTitleAsync();
                        return;
                    }
                }
            }
        }
        catch { }

        PressMediaKeyFallback(VK_MEDIA_PLAY_PAUSE);
        _manualPlayingState = !_manualPlayingState;
        _btnPlayPause.Text = _manualPlayingState ? "⏸" : "▶";
    }

    private async Task SendNextAsync()
    {
        try
        {
            if (_smtcAvailable && _smtcManager != null)
            {
                var session = _smtcSession ?? _smtcManager.GetCurrentSession();
                if (session != null)
                {
                    var t = session.TrySkipNextAsync().AsTask();
                    if (await Task.WhenAny(t, Task.Delay(500)) == t && await t)
                        return;
                }
            }
        }
        catch { }

        PressMediaKeyFallback(VK_MEDIA_NEXT_TRACK);
    }

    private static string CleanMediaTitle(string rawTitle)
    {
        string cleaned = Regex.Replace(rawTitle, @"^\(\d+\)\s*", "");
        cleaned = Regex.Replace(cleaned, @"\s*-\s*(YouTube|NAVER Whale|웨일|Google Chrome|Microsoft Edge).*$", "", RegexOptions.IgnoreCase);
        return cleaned.Trim();
    }

    private async Task RefreshMediaWindowTitleAsync()
    {
        try
        {
            bool handledBySmtc = false;
            if (_smtcAvailable && _smtcManager != null)
            {
                try
                {
                    _smtcSession = _smtcManager.GetCurrentSession();
                    if (_smtcSession != null)
                    {
                        var info = _smtcSession.GetPlaybackInfo();
                        if (info != null)
                        {
                            bool playing = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                            _btnPlayPause.Text = playing ? "⏸" : "▶";
                            _manualPlayingState = playing;
                        }

                        var propTask = _smtcSession.TryGetMediaPropertiesAsync().AsTask();
                        if (await Task.WhenAny(propTask, Task.Delay(500)) == propTask)
                        {
                            var props = await propTask;
                            if (props != null && !string.IsNullOrWhiteSpace(props.Title))
                            {
                                string title = props.Title.Trim();
                                string artist = !string.IsNullOrWhiteSpace(props.Artist) ? props.Artist.Trim() : (props.AlbumTitle ?? "-");
                                string key = $"smtc|{title}|{artist}";
                                if (key != _lastLookupKey)
                                {
                                    _lastLookupKey = key;
                                    _lblTitle.DisplayText = title;
                                    _lblArtist.DisplayText = string.IsNullOrWhiteSpace(artist) ? "-" : artist;
                                    if (props.Thumbnail != null)
                                        _ = LoadSmtcThumbnailAsync(props.Thumbnail, key);
                                }
                                handledBySmtc = true;
                            }
                        }
                    }
                }
                catch { }
            }

            if (handledBySmtc) return;

            string? detectedTitle = null;
            string detectedProc = "";
            bool isYouTube = false;

            EnumWindows((hWnd, _) =>
            {
                if (!IsWindowVisible(hWnd)) return true;

                var sb = new StringBuilder(256);
                if (GetWindowText(hWnd, sb, sb.Capacity) <= 0) return true;
                string title = sb.ToString().Trim();
                if (string.IsNullOrWhiteSpace(title)) return true;

                GetWindowThreadProcessId(hWnd, out uint pid);
                string procName = "";
                try { procName = Process.GetProcessById((int)pid).ProcessName.ToLowerInvariant(); }
                catch { }

                bool hasYouTube = title.Contains("YouTube", StringComparison.OrdinalIgnoreCase);
                bool isDedicatedPlayer = procName is "spotify" or "foobar2000" or "musicbee" or "aimp" or "vlc" or "wmplayer";
                bool isMusicService =
                    hasYouTube ||
                    title.Contains("Spotify", StringComparison.OrdinalIgnoreCase) ||
                    title.Contains("멜론", StringComparison.OrdinalIgnoreCase) ||
                    title.Contains("벅스", StringComparison.OrdinalIgnoreCase) ||
                    title.Contains("지니", StringComparison.OrdinalIgnoreCase) ||
                    title.Contains("SoundCloud", StringComparison.OrdinalIgnoreCase);

                if (isDedicatedPlayer || isMusicService)
                {
                    detectedTitle = CleanMediaTitle(title);
                    detectedProc = procName;
                    isYouTube = hasYouTube;
                    return false;
                }

                return true;
            }, IntPtr.Zero);

            bool? procAudioPlaying = IsAnyActiveAudioSession(detectedProc);
            bool? anyAudioPlaying = IsAnyActiveAudioSession("");

            if (procAudioPlaying == true || anyAudioPlaying == true)
            {
                _btnPlayPause.Text = "⏸";
                _manualPlayingState = true;
            }
            else if (procAudioPlaying == false && anyAudioPlaying == false)
            {
                _btnPlayPause.Text = _manualPlayingState ? "⏸" : "▶";
            }

            if (!string.IsNullOrWhiteSpace(detectedTitle))
            {
                string lookupKey = $"{detectedTitle}|{(isYouTube ? "yt" : "other")}";
                if (lookupKey != _lastLookupKey)
                {
                    _lastLookupKey = lookupKey;
                    _lblTitle.DisplayText = detectedTitle;
                    _lblArtist.DisplayText = isYouTube ? "채널 확인 중..." : "미디어 플레이어";

                    if (isYouTube && !_isFetchingMeta)
                    {
                        _ = UpdateYouTubeMetaAsync(detectedTitle, lookupKey);
                    }
                    else if (!isYouTube)
                    {
                        ClearThumbnail();
                    }
                }
            }
            else if (_lastLookupKey != "")
            {
                _lastLookupKey = "";
                _lblTitle.DisplayText = "재생 중인 미디어 없음";
                _lblArtist.DisplayText = "-";
                _btnPlayPause.Text = "▶";
                _manualPlayingState = false;
                ClearThumbnail();
            }
        }
        catch { }
    }

    private async Task LoadSmtcThumbnailAsync(IRandomAccessStreamReference thumbRef, string key)
    {
        try
        {
            using IRandomAccessStreamWithContentType stream = await thumbRef.OpenReadAsync();
            using Stream netStream = stream.AsStreamForRead();
            using var ms = new MemoryStream();
            await netStream.CopyToAsync(ms);
            ms.Position = 0;
            using var loaded = Image.FromStream(ms);
            var bmp = new Bitmap(loaded);

            if (key != _lastLookupKey || IsDisposed)
            {
                bmp.Dispose();
                return;
            }

            var old = _picThumb.Image;
            _picThumb.Image = bmp;
            old?.Dispose();
        }
        catch { }
    }

    private void ClearThumbnail()
    {
        var old = _picThumb.Image;
        _picThumb.Image = null;
        old?.Dispose();
    }

    private async Task UpdateYouTubeMetaAsync(string queryTitle, string lookupKey)
    {
        if (_isFetchingMeta || IsDisposed) return;
        _isFetchingMeta = true;

        try
        {
            string? videoId = await TryFindYouTubeVideoIdAsync(queryTitle);
            if (string.IsNullOrWhiteSpace(videoId) || lookupKey != _lastLookupKey) return;

            string? channelName = null;
            string? oembedTitle = null;

            try
            {
                string watchUrl = Uri.EscapeDataString($"https://www.youtube.com/watch?v={videoId}");
                string json = await Http.GetStringAsync($"https://www.youtube.com/oembed?url={watchUrl}&format=json");
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("author_name", out var authorProp))
                    channelName = authorProp.GetString();
                if (doc.RootElement.TryGetProperty("title", out var titleProp))
                    oembedTitle = titleProp.GetString();
            }
            catch { }

            string thumbUrl = $"https://i.ytimg.com/vi/{videoId}/hqdefault.jpg";
            byte[] bytes = await Http.GetByteArrayAsync(thumbUrl);
            if (lookupKey != _lastLookupKey || IsDisposed) return;

            using var ms = new MemoryStream(bytes);
            using var loaded = Image.FromStream(ms);
            var bmp = new Bitmap(loaded);

            if (IsHandleCreated && !IsDisposed)
            {
                BeginInvoke(new Action(() =>
                {
                    if (lookupKey != _lastLookupKey || IsDisposed)
                    {
                        bmp.Dispose();
                        return;
                    }

                    if (!string.IsNullOrWhiteSpace(oembedTitle))
                        _lblTitle.DisplayText = oembedTitle!;

                    _lblArtist.DisplayText = !string.IsNullOrWhiteSpace(channelName)
                        ? channelName!
                        : "YouTube";

                    var old = _picThumb.Image;
                    _picThumb.Image = bmp;
                    old?.Dispose();
                }));
            }
            else
            {
                bmp.Dispose();
            }
        }
        catch { }
        finally
        {
            _isFetchingMeta = false;
        }
    }

    private static async Task<string?> TryFindYouTubeVideoIdAsync(string title)
    {
        try
        {
            string encoded = Uri.EscapeDataString(title);
            string html = await Http.GetStringAsync($"https://www.youtube.com/results?search_query={encoded}");
            var match = Regex.Match(html, @"""videoId"":""([a-zA-Z0-9_-]{11})""");
            return match.Success ? match.Groups[1].Value : null;
        }
        catch
        {
            return null;
        }
    }

    private Button MakeControlButton(Rectangle bounds, string text)
    {
        var btn = new Button
        {
            Bounds = bounds,
            Text = text,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleCenter,
            UseCompatibleTextRendering = true,
            Cursor = Cursors.Hand,
            TabStop = false,
            Padding = Padding.Empty
        };
        btn.FlatAppearance.BorderSize = 1;
        return btn;
    }

    public void SetDarkTheme(bool isDark)
    {
        if (_isDark == isDark) return;
        _isDark = isDark;
        ApplyThemeVisuals();
        if (IsHandleCreated)
        {
            ApplyRoundedRegion();
            ApplyAcrylic();
        }
        Invalidate();
    }

    private void ApplyThemeVisuals()
    {
        Color titleColor = _isDark ? Color.FromArgb(245, 247, 255) : Color.FromArgb(24, 28, 38);
        Color artistColor = _isDark ? Color.FromArgb(200, 210, 230) : Color.FromArgb(75, 85, 105);
        Color btnBg = _isDark ? Color.FromArgb(36, 40, 54) : Color.FromArgb(232, 236, 246);
        Color btnBorder = _isDark ? Color.FromArgb(72, 78, 102) : Color.FromArgb(190, 198, 218);

        _lblTitle.ForeColor = titleColor;
        _lblArtist.ForeColor = artistColor;
        _picThumb.BackColor = _isDark ? Color.FromArgb(28, 30, 42) : Color.FromArgb(224, 229, 240);

        foreach (var btn in new[] { _btnPrev, _btnPlayPause, _btnNext })
        {
            btn.BackColor = btnBg;
            btn.ForeColor = titleColor;
            btn.FlatAppearance.BorderColor = btnBorder;
        }
    }

    private sealed class MarqueeLabel : Control
    {
        private const int StartPauseMs = 1300;
        private const int EndPauseMs = 1100;
        private const float ScrollSpeedPxPerSec = 38f;
        private const int EndGapPx = 18;

        private string _displayText = "";
        private ScrollPhase _phase = ScrollPhase.Idle;
        private int _phaseElapsedMs;
        private float _offsetPx;
        private int _textWidthPx;
        private int _maxScrollPx;

        public MarqueeLabel()
        {
            SetStyle(
                ControlStyles.UserPaint |
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.SupportsTransparentBackColor,
                true);
        }

        public string DisplayText
        {
            get => _displayText;
            set
            {
                string next = value ?? "";
                if (_displayText == next) return;
                _displayText = next;
                RecalculateMetrics();
                Invalidate();
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            RecalculateMetrics();
            Invalidate();
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            RecalculateMetrics();
            Invalidate();
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            RecalculateMetrics();
            Invalidate();
        }

        private void RecalculateMetrics()
        {
            if (string.IsNullOrEmpty(_displayText) || ClientSize.Width <= 0)
            {
                _textWidthPx = 0;
                _maxScrollPx = 0;
                _phase = ScrollPhase.Idle;
                _phaseElapsedMs = 0;
                _offsetPx = 0f;
                return;
            }

            Size measured = TextRenderer.MeasureText(
                _displayText,
                Font,
                new Size(int.MaxValue, ClientSize.Height),
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);

            _textWidthPx = measured.Width;
            int overflow = _textWidthPx - ClientSize.Width;

            if (overflow > 2)
            {
                _maxScrollPx = overflow + EndGapPx;
                _phase = ScrollPhase.PauseStart;
                _phaseElapsedMs = 0;
                _offsetPx = 0f;
            }
            else
            {
                _maxScrollPx = 0;
                _phase = ScrollPhase.Idle;
                _phaseElapsedMs = 0;
                _offsetPx = 0f;
            }
        }

        public void StepMarquee(int deltaMs)
        {
            if (_phase == ScrollPhase.Idle || !IsHandleCreated || !Visible) return;

            _phaseElapsedMs += deltaMs;

            switch (_phase)
            {
                case ScrollPhase.PauseStart:
                    if (_phaseElapsedMs >= StartPauseMs)
                    {
                        _phase = ScrollPhase.Scrolling;
                        _phaseElapsedMs = 0;
                    }
                    break;

                case ScrollPhase.Scrolling:
                    _offsetPx += ScrollSpeedPxPerSec * (deltaMs / 1000f);
                    if (_offsetPx >= _maxScrollPx)
                    {
                        _offsetPx = _maxScrollPx;
                        _phase = ScrollPhase.PauseEnd;
                        _phaseElapsedMs = 0;
                    }
                    Invalidate();
                    break;

                case ScrollPhase.PauseEnd:
                    if (_phaseElapsedMs >= EndPauseMs)
                    {
                        _offsetPx = 0f;
                        _phase = ScrollPhase.PauseStart;
                        _phaseElapsedMs = 0;
                        Invalidate();
                    }
                    break;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            if (string.IsNullOrEmpty(_displayText)) return;

            int x = -(int)Math.Round(_offsetPx);
            var drawRect = new Rectangle(x, 0, Math.Max(ClientSize.Width, _textWidthPx) + _maxScrollPx + 16, ClientSize.Height);

            TextRenderer.DrawText(
                e.Graphics,
                _displayText,
                Font,
                drawRect,
                ForeColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        }
    }

    private void ApplyRoundedRegion()
    {
        using var path = new GraphicsPath();
        int r = CornerRadius;
        var rect = new Rectangle(0, 0, Width, Height);
        path.AddArc(rect.X, rect.Y, r, r, 180, 90);
        path.AddArc(rect.Right - r, rect.Y, r, r, 270, 90);
        path.AddArc(rect.Right - r, rect.Bottom - r, r, r, 0, 90);
        path.AddArc(rect.X, rect.Bottom - r, r, r, 90, 90);
        path.CloseFigure();
        var oldRegion = Region;
        Region = new Region(path);
        oldRegion?.Dispose();
    }

    #region Acrylic Blur

    private enum AccentState
    {
        ACCENT_ENABLE_ACRYLICBLURBEHIND = 4
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public AccentState AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    private const int WCA_ACCENT_POLICY = 19;

    private void ApplyAcrylic()
    {
        try
        {
            byte a = _isDark ? (byte)75 : (byte)165;
            byte r = _isDark ? (byte)10 : (byte)248;
            byte g = _isDark ? (byte)10 : (byte)250;
            byte b = _isDark ? (byte)18 : (byte)254;
            int gradientColor = (a << 24) | (b << 16) | (g << 8) | r;

            var accent = new AccentPolicy
            {
                AccentState = AccentState.ACCENT_ENABLE_ACRYLICBLURBEHIND,
                GradientColor = gradientColor
            };
            int size = Marshal.SizeOf(accent);
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(accent, ptr, false);
                var data = new WindowCompositionAttributeData
                {
                    Attribute = WCA_ACCENT_POLICY,
                    Data = ptr,
                    SizeOfData = size
                };
                SetWindowCompositionAttribute(Handle, ref data);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }
        catch { }
    }

    #endregion

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _pollTimer.Stop();
        _pollTimer.Dispose();
        _marqueeTimer.Stop();
        _marqueeTimer.Dispose();
        _picThumb.Image?.Dispose();
        base.OnFormClosed(e);
    }
}