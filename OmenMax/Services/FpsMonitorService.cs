#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace OmenMax16.Forms;

/// <summary>
/// PresentMon.exe(콘솔 버전, --output_stdout)를 백그라운드로 실행해 현재 포그라운드(활성) 창을
/// 그리고 있는 프로세스의 실시간 FPS를 측정합니다. RTSS 처럼 게임에 직접 후킹하지 않고,
/// ETW의 Present() 이벤트를 구독하는 PresentMon 표준 방식을 그대로 사용합니다.
/// PresentMon.exe는 이 앱의 실행 파일과 같은 폴더에 미리 복사돼 있어야 합니다.
/// 참고: ETW 세션 생성은 기본적으로 관리자 권한(또는 "Performance Log Users" 그룹)이 필요하므로,
/// 앱을 관리자 권한으로 실행하지 않으면 PresentMon 프로세스는 시작되지만 데이터를 못 받을 수 있습니다.
/// </summary>
public static class FpsMonitorService
{
    private static readonly object Sync = new();
    private static Process? _process;
    private static uint _currentTargetPid;
    private static string? _exePath;
    private static int _msBetweenPresentsIndex = -1;
    private static bool _headerParsed;

    private static readonly object FpsSync = new();
    private static readonly Queue<float> _recentFps = new();
    private static float? _currentFps;

    private const int SmoothingWindow = 20; // 최근 20프레임 평균으로 튐 완화

    public static float? CurrentFps
    {
        get { lock (FpsSync) { return _currentFps; } }
    }

    private static readonly string LogPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OmenMax16", "presentmon.log");

    // 이 프로세스들이 포그라운드일 때는 "게임"이 아니므로 FPS 측정을 하지 않음
    private static readonly string[] ExcludedProcessNames =
    {
        "explorer", "SearchHost", "ApplicationFrameHost", "TextInputHost",
        "ShellExperienceHost", "StartMenuExperienceHost", "OmenMax16"
    };

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    /// <summary>
    /// 1초 주기로 호출됩니다. 포그라운드 프로세스가 바뀌었으면 PresentMon 대상을 다시 시작하고,
    /// 게임이 아닌 창(탐색기 등)이 활성화되면 측정을 중단합니다.
    /// </summary>
    public static void Tick()
    {
        try
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) { StopInternal(); return; }

            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) { StopInternal(); return; }

            string? name = TryGetProcessName(pid);
            if (name == null || IsExcluded(name)) { StopInternal(); return; }

            lock (Sync)
            {
                if (_process != null && !_process.HasExited && _currentTargetPid == pid) return; // 이미 이 대상 추적 중
            }

            StartFor(pid);
        }
        catch (Exception ex)
        {
            Log($"[Tick 예외] {ex.Message}");
        }
    }

    private static bool IsExcluded(string name)
    {
        foreach (string ex in ExcludedProcessNames)
            if (name.Equals(ex, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string? TryGetProcessName(uint pid)
    {
        try { using Process p = Process.GetProcessById((int)pid); return p.ProcessName; }
        catch { return null; }
    }

    private static void StartFor(uint pid)
    {
        StopInternal();

        string? exe = ResolveExePath();
        if (exe == null) return;

        // --no_csv: 파일로 저장하지 않음, --output_stdout: 표준출력으로 CSV 스트림 전송,
        // --v1_metrics: "MsBetweenPresents" 컬럼을 포함하는 안정적인 구버전 스키마 사용,
        // --stop_existing_session: 이전에 남아있을 수 있는 세션을 정리하고 새로 시작
        string args = $"--process_id {pid} --output_stdout " +
              $"--session_name OmenMax16_{Environment.ProcessId} --stop_existing_session";
        // --no_csv 는 제거

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                WorkingDirectory = System.IO.Path.GetDirectoryName(exe) ?? "",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _headerParsed = false;
            _msBetweenPresentsIndex = -1;
            proc.OutputDataReceived += OnOutputLine;
            proc.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log($"[stderr] {e.Data}"); };
            proc.Exited += (_, _) => { lock (FpsSync) { _currentFps = null; _recentFps.Clear(); } };

            if (!proc.Start())
            {
                Log("[오류] PresentMon.exe 프로세스를 시작할 수 없습니다.");
                return;
            }

            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            lock (Sync)
            {
                _process = proc;
                _currentTargetPid = pid;
            }

            Log($"[시작] PresentMon → PID {pid}");
        }
        catch (Exception ex)
        {
            Log($"[예외] PresentMon 시작 실패: {ex.Message}");
        }
    }

    private static void OnOutputLine(object? sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Data)) return;
        string[] cols = e.Data.Split(',');

        if (!_headerParsed)
        {
            // v1_metrics CSV 헤더에서 "MsBetweenPresents" 컬럼 위치를 찾음
            _msBetweenPresentsIndex = Array.FindIndex(cols, c => c.Trim().Equals("MsBetweenPresents", StringComparison.OrdinalIgnoreCase));
            _headerParsed = true;
            if (_msBetweenPresentsIndex < 0)
            {
                Log($"[진단] MsBetweenPresents 컬럼을 찾지 못했습니다. 헤더: {e.Data}");
            }
            return;
        }

        int idx = _msBetweenPresentsIndex;
        if (idx < 0 || idx >= cols.Length) return;

        if (float.TryParse(cols[idx], NumberStyles.Float, CultureInfo.InvariantCulture, out float ms) && ms > 0.01f)
        {
            float fps = 1000f / ms;
            lock (FpsSync)
            {
                _recentFps.Enqueue(fps);
                while (_recentFps.Count > SmoothingWindow) _recentFps.Dequeue();
                float sum = 0f;
                foreach (float f in _recentFps) sum += f;
                _currentFps = sum / _recentFps.Count;
            }
        }
    }

    private static void StopInternal()
    {
        Process? proc;
        lock (Sync)
        {
            proc = _process;
            _process = null;
            _currentTargetPid = 0;
        }

        if (proc == null) return;

        try
        {
            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(1500);      // 종료될 때까지 잠깐 기다림
            }
        }
        catch { /* 이미 종료된 경우 등 - 무시 */ }
        finally
        {
            proc.Dispose();
        }

        lock (FpsSync)
        {
            _currentFps = null;
            _recentFps.Clear();
        }
    }

    /// <summary>앱 종료 시 호출해서 PresentMon 프로세스가 트레이에 남아 있지 않도록 정리합니다.</summary>
    public static void Shutdown() => StopInternal();

    private static string? ResolveExePath()
    {
        if (_exePath != null && System.IO.File.Exists(_exePath)) return _exePath;
        string candidate = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PresentMon.exe");
        if (System.IO.File.Exists(candidate)) { _exePath = candidate; return _exePath; }
        Log($"[오류] PresentMon.exe 를 찾을 수 없습니다: {candidate}");
        return null;
    }

    private static void Log(string message)
    {
        Debug.WriteLine(message);
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(LogPath)!);
            System.IO.File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch { }
    }
}
