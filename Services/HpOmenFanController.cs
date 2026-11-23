#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Threading;
using OmenMax16.Models;

namespace OmenMax16.Services;

public enum HpOmenPerformanceMode : byte
{
    Default = 0x30,
    Performance = 0x31,
    Cool = 0x50
}

public enum HpOmenMaxFanMode : byte { Off = 0, On = 1 }
public enum HpOmenThermalPolicyVersion : byte { V0 = 0, V1 = 1 }
public enum HpOmenFanPreset { Auto, Quiet, Performance, Custom }

/// <summary>
/// RawNotebookFanLevel = CPU 팬의 raw 레벨(0~60), RawNotebookFanLevelGpu = GPU 팬의 raw 레벨.
/// v6 에서 GPU raw 값을 추가해 CPU/GPU 를 완전히 대등하게 다룰 수 있도록 했습니다.
/// </summary>
public sealed record HpOmenFanReadings(
    byte CpuLevel,
    byte GpuLevel,
    HpOmenMaxFanMode MaxFanMode,
    HpOmenThermalPolicyVersion ThermalPolicyVersion,
    int RawNotebookFanLevel,
    int RawNotebookFanLevelGpu,
    int? CpuRpm,
    int? GpuRpm
);

/// <summary>
/// HP OMEN MAX 16 팬 컨트롤러 (v10 - AUTO 를 "레벨 0" 이 아니라 "안전 레벨 인계" 로 변경).
///
/// 확정된 사실 (ohman 리버스엔지니어링 문서 기준):
///  - 0x2E(SetFanLevel) 의 레벨 0 은 "자동" 이 아니라 "팬 꺼짐" 입니다.
///  - 펌웨어에는 "자동 복귀" 명령이 없습니다. 0x10 쿼리가 사용자 정의 팬 상태를 켜고 갱신하며,
///    마지막 쿼리 후 약 120초가 지나면 상태가 만료됩니다.
///  - 만료되면 펌웨어는 자체 커브로 돌아가기 전에 "마지막으로 쓴 레벨" 을 먼저 다시 적용합니다.
///    따라서 낮은 레벨(특히 0)을 남긴 채 손을 떼면 그 값이 한동안 유지됩니다.
///  - 0x1A 는 팬 모드가 아니라 "성능 모드"(0x30/0x31/0x50) 명령입니다. 팬 자동 복귀용이 아닙니다.
///
/// v9 까지의 문제:
///  - TrySetAuto 가 FanLevel 0 을 쓰고 0x10 으로 그 상태를 갱신해서, AUTO 로 바꿔도(그리고 앱 종료 시에도)
///    팬이 꺼진 채로 유지됐습니다.
///
/// v10 동작:
///  - AUTO 전환 시 arm → MaxFan Off → 안전 인계 레벨(AutoHandoffRawLevel) 1회 쓰기 후 팬 제어에서 손을 뗍니다.
///    이후 상태가 만료되면 펌웨어가 방금 쓴 안전 레벨부터 시작해 자체 커브를 이어받습니다.
///  - AUTO 상태에서는 이 클래스가 더 이상 0x10/0x2E/0x27 을 보내지 않아야 합니다
///    (FormMain.ReapplyFanControlIfNeeded 는 AUTO 에서 즉시 return 하므로 이미 그렇게 동작합니다).
/// </summary>
public sealed class HpOmenFanController
{
    private const uint DefaultCommand = 0x00020008;
    private const uint GetFanLevelCommandType = 0x2D;
    private const uint SetFanLevelCommandType = 0x2E;
    private const uint GetMaxFanCommandType = 0x26;
    private const uint SetMaxFanCommandType = 0x27;

    // "fan count" 조회. 사용자 정의 팬 제어 상태를 활성화("arm")하고 만료 타이머를 갱신합니다.
    // 이게 없으면 SetFanLevel(0x2E)/SetMaxFan(0x27) 쓰기가 EC 펌웨어에서 무시됩니다.
    private const uint ArmFanControlCommandType = 0x10;

    private const int RawLevelMax = 60;

    // AUTO 로 넘길 때 마지막으로 남기는 안전 레벨(raw, rpm/100 단위).
    // 펌웨어는 상태 만료 후 "마지막으로 쓴 레벨"을 먼저 재적용하므로, 0 이나 낮은 값이면 안 됩니다.
    // 23 은 OGH 저장 커브의 최저값이며 약 38% 입니다. 소음이 크면 낮추되, 팬이 꺼지지 않도록
    // 너무 낮은 값은 피하세요.
    private const byte AutoHandoffRawLevel = 14;

    private const int HResultAccessDenied = unchecked((int)0x80070005);
    private const int HResultWbemAccessDenied = unchecked((int)0x80041003);
    private const int HResultCorSystem = unchecked((int)0x80131501);
    private const int HResultCorException = unchecked((int)0x80131500);

    // 40% 이상이면 소음으로 체감된다는 사용자 피드백을 반영한 QUIET 목표값/상한.
    private const byte QuietMinPercent = 0;
    private const byte QuietMaxPercent = 28;
    private const byte QuietCpuTargetPercent = 16;
    private const byte QuietGpuTargetPercent = 14;

    private const int SettleDelayMs = 600;
    private const int VerifyTolerancePercent = 20;

    // MaxFan Off 쓰기가 실패했을 때 재시도할 횟수와 간격
    private const int MaxFanOffRetryCount = 3;
    private const int MaxFanOffRetryDelayMs = 150;

    private readonly object _sync = new();
    private readonly HpOmenBiosTransport _bios;
    private readonly HpOmenEcTransport? _ec;

    public HpOmenFanController(HpOmenBiosTransport bios, HpOmenEcTransport? ec = null)
    {
        _bios = bios ?? throw new ArgumentNullException(nameof(bios));
        _ec = ec;
    }

    public bool IsAvailable(out string message)
    {
        try
        {
            byte[] raw = _bios.Invoke(DefaultCommand, GetFanLevelCommandType, 128, new byte[] { 0, 0, 0, 0 }).Data;
            if (raw.Length == 0)
            {
                message = "HP BIOS 팬 레벨을 읽을 수 없습니다. (빈 응답)";
                return false;
            }
            message = "HP BIOS 팬 제어가 준비되었습니다.";
            return true;
        }
        catch (Exception ex)
        {
            message = "HP BIOS 팬 제어 실패: " + DiagnoseError(ex);
            return false;
        }
    }

    public bool TryReadState(out HpOmenFanReadings? readings, out string message)
    {
        lock (_sync)
        {
            readings = null;
            try
            {
                byte[] raw = _bios.Invoke(DefaultCommand, GetFanLevelCommandType, 128, new byte[] { 0, 0, 0, 0 }).Data;
                if (raw.Length == 0)
                {
                    message = "팬 레벨 데이터가 비어 있습니다.";
                    return false;
                }

                byte[] max = _bios.Invoke(DefaultCommand, GetMaxFanCommandType, 4, new byte[] { 0, 0, 0, 0 }).Data;
                HpOmenMaxFanMode maxMode = max.Length > 0 && max[0] == 1 ? HpOmenMaxFanMode.On : HpOmenMaxFanMode.Off;

                // raw[0] = CPU(1번) 팬, raw[1] = GPU(2번) 팬 - BIOS 가 독립적으로 리턴함
                int rawCpu = DecodeNotebookFanLevel(raw[0]);
                int rawGpu = raw.Length > 1 ? DecodeNotebookFanLevel(raw[1]) : rawCpu;

                byte cpuLevel = maxMode == HpOmenMaxFanMode.On ? (byte)100 : ConvertNotebookLevelToPercent(rawCpu);
                byte gpuLevel = maxMode == HpOmenMaxFanMode.On ? (byte)100 : ConvertNotebookLevelToPercent(rawGpu);

                int? cpuRpm = null; int? gpuRpm = null;
                if (_ec is not null && _ec.TryReadFanRpm(out int? readCpu, out int? readGpu, out _))
                {
                    cpuRpm = readCpu; gpuRpm = readGpu;
                }

                readings = new HpOmenFanReadings(cpuLevel, gpuLevel, maxMode, HpOmenThermalPolicyVersion.V1, rawCpu, rawGpu, cpuRpm, gpuRpm);
                message = $"BIOS target CPU={cpuLevel}%, GPU={gpuLevel}%, Max={maxMode}, raw(0~{RawLevelMax}) CPU={rawCpu} GPU={rawGpu}";
                return true;
            }
            catch (Exception ex)
            {
                message = "상태 읽기 실패: " + DiagnoseError(ex);
                return false;
            }
        }
    }

    /// <summary>
    /// AUTO: 펌웨어에는 "자동 복귀" 명령이 없으므로, 안전한 인계 레벨을 한 번 쓰고 손을 뗍니다.
    /// 약 120초 후 사용자 정의 팬 상태가 만료되면 펌웨어가 이 레벨부터 이어서 자체 커브로 복귀합니다.
    /// 레벨 0 은 팬 꺼짐이므로 절대 쓰지 않습니다.
    /// </summary>
    public bool TrySetAuto(out HpOmenFanReadings? readings, out string message)
    {
        lock (_sync)
        {
            readings = null;
            var softErrors = new List<string>();

            // 쓰기 직전 arm (이 보드는 arm 없이 온 쓰기를 무시).
            ArmFanControl(softErrors);

            // 최대 팬 플래그가 켜져 있으면 해제합니다.
            bool maxFanOffOk = EnsureMaxFanOff(softErrors);
            Thread.Sleep(80);

            // 마지막으로 남기는 레벨이 곧 만료 후 재적용되는 레벨이므로 안전 레벨을 씁니다.
            bool handoffOk = TryWriteTolerant(
                SetFanLevelCommandType,
                new byte[] { AutoHandoffRawLevel, AutoHandoffRawLevel, 0, 0 },
                softErrors,
                $"FanLevel 인계(raw={AutoHandoffRawLevel})");
            Thread.Sleep(SettleDelayMs);

            bool readOk = TryReadState(out readings, out string readMsg);

            if (!handoffOk)
            {
                message = $"AUTO 인계 레벨 쓰기 실패: {string.Join("; ", softErrors)}" + (readOk ? $" / 현재상태: {readMsg}" : "");
                return false;
            }

            string maxFanWarning = maxFanOffOk ? "" : $" · (MaxFan Off 경고: {string.Join("; ", softErrors.Where(s => s.Contains("MaxFan")))})";
            string stateText = readOk ? readMsg : $"상태 확인 실패: {readMsg}";

            message =
                $"AUTO 전환: 안전 레벨(raw={AutoHandoffRawLevel})을 남기고 팬 제어를 놓았습니다. " +
                $"약 2분 후 펌웨어 자체 커브가 이어받습니다. ({stateText}){maxFanWarning}";
            return true;
        }
    }
    public bool TrySetAuto(out string message) => TrySetAuto(out _, out message);

    public bool TrySetQuiet(out HpOmenFanReadings? readings, out string message)
    {
        return TrySetCustomLevels(QuietCpuTargetPercent, QuietGpuTargetPercent, out readings, out message, isQuietCurve: true);
    }
    public bool TrySetQuiet(out string message) => TrySetQuiet(out _, out message);

    /// <summary>
    /// CPU/GPU 팬 레벨을 독립적으로 적용합니다. cpuPercent 와 gpuPercent 는 서로 다른 값이어도 됩니다.
    /// 검증도 CPU/GPU 를 각각 독립적으로 판정해서, 한쪽만 목표에 도달해도 그 팬은 "완료"로,
    /// 다른 쪽은 "반영 중"으로 구분해서 보고합니다.
    ///
    /// 쓰기 전에 반드시 ArmFanControl() 을 먼저 호출합니다. 이 arm 쿼리가 없으면 이 보드는
    /// FanLevel 쓰기를 WMI 레벨에서는 성공으로 처리하지만 EC 펌웨어가 실제로는 무시합니다.
    /// </summary>
    public bool TrySetCustomLevels(int cpuPercent, int gpuPercent, out HpOmenFanReadings? readings, out string message, bool isQuietCurve = false)
    {
        readings = null;

        if (cpuPercent is < 0 or > 100 || gpuPercent is < 0 or > 100)
        {
            message = "팬 요청은 0~100 사이여야 합니다.";
            return false;
        }

        if (isQuietCurve)
        {
            cpuPercent = Math.Clamp(cpuPercent, QuietMinPercent, QuietMaxPercent);
            gpuPercent = Math.Clamp(gpuPercent, QuietMinPercent, QuietMaxPercent);
        }

        lock (_sync)
        {
            var softErrors = new List<string>();

            ArmFanControl(softErrors);

            // MaxFan Off 쓰기를 시도하되, 실패해도 FanLevel 전송을 막지 않습니다.
            bool maxFanOffOk = EnsureMaxFanOff(softErrors);
            Thread.Sleep(80);

            byte cpuLevelByte = ConvertPercentToNotebookLevel(cpuPercent);
            byte gpuLevelByte = ConvertPercentToNotebookLevel(gpuPercent);

            bool levelWriteOk = TryWriteTolerant(
                SetFanLevelCommandType,
                new byte[] { cpuLevelByte, gpuLevelByte, 0, 0 },
                softErrors,
                $"FanLevel(CPU={cpuLevelByte},GPU={gpuLevelByte})");
            Thread.Sleep(SettleDelayMs);

            bool readOk = TryReadState(out readings, out string readMsg);

            if (!levelWriteOk)
            {
                message = $"팬 레벨 명령이 BIOS 에 거부됨: {string.Join("; ", softErrors)}";
                return false;
            }

            if (!readOk)
            {
                message = $"레벨 명령은 전송했지만 상태 확인 실패: {readMsg}";
                return false;
            }

            bool cpuMatches = readings is not null && Math.Abs(readings.CpuLevel - cpuPercent) <= VerifyTolerancePercent;
            bool gpuMatches = readings is not null && Math.Abs(readings.GpuLevel - gpuPercent) <= VerifyTolerancePercent;

            string cpuStatus = cpuMatches ? "완료" : "반영 중";
            string gpuStatus = gpuMatches ? "완료" : "반영 중";

            string maxFanWarning = maxFanOffOk ? "" : $" · (MaxFan Off 경고: {string.Join("; ", softErrors.Where(s => s.Contains("MaxFan")))})";

            if (cpuMatches && gpuMatches)
            {
                message = (isQuietCurve
                    ? $"QUIET 커브 적용 완료: CPU={readings!.CpuLevel}%, GPU={readings!.GpuLevel}% (0~{QuietMaxPercent}% 제한)"
                    : $"CUSTOM 적용 완료: CPU={readings!.CpuLevel}% GPU={readings!.GpuLevel}%") + maxFanWarning;
                return true;
            }

            message = $"팬 레벨 요청 전송됨: CPU 목표={cpuPercent}% 현재={readings?.CpuLevel}% [{cpuStatus}] · " +
                      $"GPU 목표={gpuPercent}% 현재={readings?.GpuLevel}% [{gpuStatus}] · 팬이 전환 중일 수 있습니다.{maxFanWarning}";
            return true;
        }
    }
    public bool TrySetCustomLevels(int cpuPercent, int gpuPercent, out string message)
        => TrySetCustomLevels(cpuPercent, gpuPercent, out _, out message, isQuietCurve: false);

    /// <summary>
    /// SetFanLevel/SetMaxFan 쓰기 직전에 반드시 호출해야 합니다. 팬 개수 조회(0x10)는
    /// 사용자 정의 팬 상태를 켜고 만료 타이머(약 120초)를 갱신합니다.
    /// 실패해도 쓰기 자체를 막지는 않고(진단 목적으로만 기록), 이후 쓰기는 그대로 시도합니다.
    /// </summary>
    private bool ArmFanControl(List<string> softErrors)
    {
        try
        {
            _bios.Invoke(DefaultCommand, ArmFanControlCommandType, 4, new byte[] { 0, 0, 0, 0 });
            return true;
        }
        catch (Exception ex)
        {
            softErrors.Add($"Arm(0x10): {DiagnoseError(ex)}");
            return false;
        }
    }

    /// <summary>
    /// MaxFan Off 쓰기를 시도하고, 실패하면 짧은 간격으로 최대 MaxFanOffRetryCount 번 재시도합니다.
    /// 실패 여부는 FanLevel 전송을 막지 않고 진단 목적으로만 호출자에게 전달됩니다.
    /// </summary>
    private bool EnsureMaxFanOff(List<string> softErrors)
    {
        for (int attempt = 1; attempt <= MaxFanOffRetryCount; attempt++)
        {
            bool ok = TryWriteTolerant(SetMaxFanCommandType, new byte[] { (byte)HpOmenMaxFanMode.Off, 0, 0, 0 }, softErrors, $"MaxFan Off (시도 {attempt}/{MaxFanOffRetryCount})");
            if (ok) return true;
            if (attempt < MaxFanOffRetryCount) Thread.Sleep(MaxFanOffRetryDelayMs);
        }
        return false;
    }

    private static int DecodeNotebookFanLevel(byte value) => value == 0 || value == 128 ? 0 : (value > 128 ? -(value - 128) : value);

    private static byte ConvertNotebookLevelToPercent(int level) =>
        (byte)Math.Clamp((int)Math.Round(Math.Abs(level) * 100d / RawLevelMax, MidpointRounding.AwayFromZero), 0, 100);

    private static byte ConvertPercentToNotebookLevel(int percent) =>
        (byte)Math.Clamp((int)Math.Round(percent * (double)RawLevelMax / 100d, MidpointRounding.AwayFromZero), 0, RawLevelMax);

    private bool TryWriteTolerant(uint commandType, byte[] request, List<string> softErrors, string label)
    {
        try
        {
            Write(commandType, request);
            return true;
        }
        catch (Exception ex)
        {
            softErrors.Add($"{label}: {DiagnoseError(ex)}");
            return false;
        }
    }

    private void Write(uint commandType, byte[] request)
    {
        byte[] payload = new byte[128];
        Array.Copy(request, payload, Math.Min(request.Length, payload.Length));
        _bios.Invoke(DefaultCommand, commandType, 128, payload);
    }

    private static string DiagnoseError(Exception ex)
    {
        ManagementException? mgmtEx = FindManagementException(ex);
        if (mgmtEx is not null)
        {
            string detail = $"ManagementStatus.{mgmtEx.ErrorCode} (0x{(int)mgmtEx.ErrorCode:X8})";
            string extra = "";
            try
            {
                if (mgmtEx.ErrorInformation is not null)
                {
                    extra = " · " + string.Join(", ",
                        mgmtEx.ErrorInformation.Properties.Cast<PropertyData>()
                            .Select(p => $"{p.Name}={p.Value}"));
                }
            }
            catch { }
            return $"ManagementException: {mgmtEx.Message} [{detail}]{extra}";
        }

        Exception? informative = FindInformativeException(ex);
        if (informative is not null)
        {
            int hr = informative.HResult;
            if (hr == HResultAccessDenied || hr == HResultWbemAccessDenied)
                return $"{informative.GetType().Name}: {informative.Message} [HRESULT 0x{hr:X8}] · 액세스 거부.";
            return $"{informative.GetType().Name}: {informative.Message} [HRESULT 0x{hr:X8}]";
        }

        return BuildFullChainMessage(ex);
    }

    private static ManagementException? FindManagementException(Exception ex)
    {
        Exception? current = ex;
        while (current is not null)
        {
            if (current is ManagementException mgmtEx) return mgmtEx;
            current = current.InnerException;
        }
        return null;
    }

    private static Exception? FindInformativeException(Exception ex)
    {
        Exception? current = ex;
        while (current is not null)
        {
            if (current.HResult != HResultCorSystem && current.HResult != HResultCorException)
                return current;
            current = current.InnerException;
        }
        return null;
    }

    private static string BuildFullChainMessage(Exception ex)
    {
        var parts = new List<string>();
        Exception? current = ex;
        int depth = 0;
        while (current is not null && depth < 6)
        {
            parts.Add($"{current.GetType().Name}: {current.Message}");
            current = current.InnerException;
            depth++;
        }
        return string.Join(" → ", parts) + $" [HRESULT 0x{ex.HResult:X8}]";
    }
}