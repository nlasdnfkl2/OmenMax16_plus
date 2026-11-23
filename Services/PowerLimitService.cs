#nullable enable
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

namespace OmenMax16.Services;

public sealed class PowerLimitService : IDisposable
{
    private CancellationTokenSource? _enforcerCts;
    private readonly object _sync = new();

    private sealed record NvidiaGpu(
        int Index,
        string Uuid,
        string Name,
        float MinWatts,
        float MaxWatts
    );

    public bool IsAdministrator()
    {
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private string GetNvidiaSmiPath()
    {
        string system32 = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "nvidia-smi.exe");

        if (File.Exists(system32))
        {
            return system32;
        }

        string programFiles = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "NVIDIA Corporation",
            "NVSMI",
            "nvidia-smi.exe");

        if (File.Exists(programFiles))
        {
            return programFiles;
        }

        return "nvidia-smi.exe";
    }
    private static (bool Started, int ExitCode, string Stdout, string Stderr) RunNvidiaSmi(
    string smiPath,
    string arguments,
    int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = smiPath,
                Arguments = arguments,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using Process? process = Process.Start(psi);
            if (process is null)
            {
                return (false, -1, "", "nvidia-smi.exe 프로세스를 시작할 수 없습니다.");
            }

            Task<string> outTask = process.StandardOutput.ReadToEndAsync();
            Task<string> errTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return (true, -1, "", $"명령 시간 초과 ({timeoutMs}ms): {arguments}");
            }

            process.WaitForExit();   // 출력 버퍼가 모두 비워질 때까지
            Task.WaitAll(new Task[] { outTask, errTask }, 2000);
            string stdout = outTask.IsCompletedSuccessfully ? outTask.Result : "";
            string stderr = errTask.IsCompletedSuccessfully ? errTask.Result : "";
            return (true, process.ExitCode, stdout.Trim(), stderr.Trim());
        }
        catch (Exception ex)
        {
            return (false, -1, "", ex.Message);
        }
    }

    /// <summary>
    /// 첫 번째 NVIDIA GPU의 실제 인덱스, UUID, 드라이버가 보고하는 전력 제한 최솟값/최댓값을 읽습니다.
    /// </summary>
    private static bool TryGetNvidiaGpu(string smiPath, out NvidiaGpu? gpu, out string error)
    {
        gpu = null;

        var result = RunNvidiaSmi(
            smiPath,
            "--query-gpu=index,uuid,name,power.min_limit,power.max_limit --format=csv,noheader,nounits",
            3000);

        if (!result.Started || result.ExitCode != 0)
        {
            error = string.IsNullOrWhiteSpace(result.Stderr)
                ? $"GPU 정보 조회 실패. ExitCode={result.ExitCode}, stdout={result.Stdout}"
                : $"GPU 정보 조회 실패. ExitCode={result.ExitCode}, stderr={result.Stderr}";
            return false;
        }

        string? firstLine = result.Stdout
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(firstLine))
        {
            error = "nvidia-smi가 NVIDIA GPU 정보를 반환하지 않았습니다.";
            return false;
        }

        string[] values = firstLine.Split(',')
            .Select(v => v.Trim())
            .ToArray();

        if (values.Length < 5 ||
            !int.TryParse(values[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int index) ||
            !float.TryParse(values[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float minWatts) ||
            !float.TryParse(values[4], NumberStyles.Float, CultureInfo.InvariantCulture, out float maxWatts))
        {
            error = $"GPU 정보 파싱 실패: {firstLine}";
            return false;
        }

        gpu = new NvidiaGpu(
            Index: index,
            Uuid: values[1],
            Name: values[2],
            MinWatts: minWatts,
            MaxWatts: maxWatts);

        error = "";
        return true;
    }

    /// <summary>
    /// NVIDIA 드라이버가 노출한 허용 범위 안에서 GPU 전력 제한을 적용합니다.
    /// 노트북 GPU는 보통 -pl을 지원하지 않거나 OEM/펌웨어가 범위를 잠그므로,
    /// 실패했을 때 실제 nvidia-smi 오류를 그대로 반환합니다.
    /// </summary>
    public bool SetGpuPowerLimit(int watts, out string message)
    {
        if (!IsAdministrator())
        {
            message = "GPU 전력 제한은 관리자 권한 실행이 필요합니다.";
            return false;
        }

        string smiPath = GetNvidiaSmiPath();

        lock (_sync)
        {
            _enforcerCts?.Cancel();
            _enforcerCts?.Dispose();
            _enforcerCts = null;
        }

        if (!TryGetNvidiaGpu(smiPath, out NvidiaGpu? gpu, out string gpuError) || gpu is null)
        {
            message = gpuError;
            return false;
        }

        // GPU 인덱스 0을 하드코딩하지 않고, 실제 조회된 dGPU 인덱스를 사용합니다.
        int targetWatts = Math.Clamp(watts, (int)Math.Ceiling(gpu.MinWatts), (int)Math.Floor(gpu.MaxWatts));

        if (targetWatts != watts)
        {
            message =
                $"요청한 {watts}W는 드라이버 허용 범위({gpu.MinWatts:0}~{gpu.MaxWatts:0}W)를 벗어났습니다. " +
                $"{targetWatts}W로 조정했습니다.";
        }

        // Persistence mode는 지원하지 않는 노트북 드라이버도 있으므로, 실패해도 적용 자체는 계속 시도합니다.
        _ = RunNvidiaSmi(smiPath, $"-i {gpu.Index} -pm 1", 1500);

        string applyArguments = $"-i {gpu.Index} -pl {targetWatts}";
        var apply = RunNvidiaSmi(smiPath, applyArguments, 3000);

        if (!apply.Started || apply.ExitCode != 0)
        {
            string detail = !string.IsNullOrWhiteSpace(apply.Stderr)
                ? apply.Stderr
                : (!string.IsNullOrWhiteSpace(apply.Stdout)
                    ? apply.Stdout
                    : $"ExitCode={apply.ExitCode}");

            message =
                $"GPU 전력 제한 적용 실패: {detail}\n" +
                $"대상: GPU {gpu.Index} ({gpu.Name}), 요청: {targetWatts}W, " +
                $"드라이버 허용 범위: {gpu.MinWatts:0}~{gpu.MaxWatts:0}W.";

            return false;
        }

        // 현재 드라이버가 보고하는 최대치 이상이면 제한을 별도로 지속 주입하지 않습니다.
        if (targetWatts >= Math.Floor(gpu.MaxWatts))
        {
            message =
                $"GPU {gpu.Index} ({gpu.Name}) 전력 제한을 드라이버 최대값 {targetWatts}W로 설정했습니다.";
            return true;
        }

        CancellationTokenSource cts;
        lock (_sync)
        {
            _enforcerCts?.Cancel();
            _enforcerCts?.Dispose();

            _enforcerCts = new CancellationTokenSource();
            cts = _enforcerCts;
        }

        CancellationToken token = cts.Token;

        // OEM 펌웨어가 제한을 되돌리는 모델을 위해 주기적으로 다시 적용합니다.
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), token);
                    if (token.IsCancellationRequested)
                    {
                        break;
                    }

                    _ = RunNvidiaSmi(smiPath, applyArguments, 1500);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // 다음 주기에서 재시도
                }
            }
        }, token);

        string adjustment = targetWatts != watts
            ? $" (요청값 {watts}W에서 허용 범위에 맞게 조정)"
            : "";

        message =
            $"GPU {gpu.Index} ({gpu.Name})에 {targetWatts}W 전력 제한을 적용했습니다{adjustment}. " +
            $"드라이버 범위: {gpu.MinWatts:0}~{gpu.MaxWatts:0}W.";

        return true;
    }
    public bool SetGpuClockCap(int? maxMhz, out string message)
    {
        if (!IsAdministrator()) { message = "GPU 클럭 제한은 관리자 권한 실행이 필요합니다."; return false; }

        string smi = GetNvidiaSmiPath();
        if (!TryGetNvidiaGpu(smi, out NvidiaGpu? gpu, out string err) || gpu is null) { message = err; return false; }

        string args = maxMhz is int m ? $"-i {gpu.Index} -lgc 210,{m}" : $"-i {gpu.Index} -rgc";
        var r = RunNvidiaSmi(smi, args, 3000);
        string all = $"{r.Stdout}\n{r.Stderr}";

        if (!r.Started || r.ExitCode != 0 ||
            all.Contains("not supported", StringComparison.OrdinalIgnoreCase) ||
            all.Contains("Treating as warning", StringComparison.OrdinalIgnoreCase))
        {
            message = $"GPU 클럭 상한 적용 실패: {all.Trim()}";
            return false;
        }

        message = maxMhz is int mm ? $"GPU 최대 클럭을 {mm}MHz 로 제한했습니다." : "GPU 클럭 제한을 해제했습니다.";
        return true;
    }
    public void Dispose()
    {
        lock (_sync)
        {
            _enforcerCts?.Cancel();
            _enforcerCts?.Dispose();
            _enforcerCts = null;
        }
    }
}