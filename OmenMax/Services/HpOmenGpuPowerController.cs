#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace OmenMax16.Services;

/// <summary>
/// GPU 전력 모드 (RTX 5070 Ti Laptop GPU 140~175W 대응).
/// Ctgp=커스텀 TGP (140W -> 175W 확장), Ppab=다이내믹 부스트 (+35W),
/// DState=전력 비율(1=100% 2=50% 3=25% 4=12.5%), PeakTemp=슬로다운 온도.
/// </summary>
public readonly record struct GpuPowerMode(bool Ctgp, bool Ppab, byte DState, byte PeakTemp)
{
    public static GpuPowerMode Eco => new(false, false, 1, 75);
    public static GpuPowerMode Base140W => new(false, false, 1, 87);
    public static GpuPowerMode Balanced => new(false, true, 1, 87);
    public static GpuPowerMode Performance => new(true, true, 1, 87);
    public static GpuPowerMode Max175W => Performance;

    public byte[] ToBytes() => new byte[] { (byte)(Ctgp ? 1 : 0), (byte)(Ppab ? 1 : 0), DState, PeakTemp };
    public override string ToString() => $"cTGP={(Ctgp ? "ON" : "OFF")}, PPAB={(Ppab ? "ON" : "OFF")}, dState={DState}, peak={PeakTemp}";
}

public sealed class HpOmenGpuPowerController
{
    private const uint CmdGaming = 0x00020008;   // HPWMI_GM
    private const uint TypeGet = 0x21;           // GPU thermal modes 읽기
    private const uint TypeSet = 0x22;           // GPU thermal modes 쓰기

    private static readonly (int In, int Out)[] GetVariants = { (4, 4), (0, 4), (128, 128) };
    private static readonly (int In, int Out)[] SetVariants = { (4, 4), (128, 128), (4, 0) };

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OmenMax16", "gpu-power.log");

    private readonly HpOmenBiosTransport _transport;
    private (int In, int Out)? _getOk, _setOk;

    public HpOmenGpuPowerController(HpOmenBiosTransport transport) => _transport = transport;

    internal static void Log(string msg)
    {
        Debug.WriteLine($"[GpuPower] {msg}");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}{Environment.NewLine}");
        }
        catch { }
    }

    public bool TryGet(out GpuPowerMode mode, out string error)
    {
        mode = default;
        string lastError = "";
        foreach (var v in Order(GetVariants, _getOk))
        {
            try
            {
                var r = _transport.Invoke(CmdGaming, TypeGet, v.In, v.Out, Array.Empty<byte>());
                if (r.Data.Length < 4)
                {
                    lastError = $"응답 길이 부족({r.Data.Length}B)";
                    Log($"GET in={v.In} out={v.Out} {lastError}");
                    continue;
                }
                mode = new GpuPowerMode(r.Data[0] != 0, r.Data[1] != 0, r.Data[2], r.Data[3]);
                _getOk = v;
                Log($"GET 성공 in={v.In} out={v.Out} raw=[{BitConverter.ToString(r.Data, 0, 4)}] → {mode}");
                error = "";
                return true;
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
                Log($"GET in={v.In} out={v.Out} 실패: {ex.Message}");
            }
        }
        error = $"GPU 전력 모드 읽기 실패: {lastError}";
        Log(error);
        return false;
    }

    public bool TrySet(GpuPowerMode requested, out GpuPowerMode? readback, out string message)
    {
        readback = null;
        requested = requested with { DState = (byte)Math.Clamp((int)requested.DState, 1, 4) };
        string lastError = "";

        foreach (var v in Order(SetVariants, _setOk))
        {
            Log($"SET 시도 in={v.In} out={v.Out} 요청={requested} bytes=[{BitConverter.ToString(requested.ToBytes())}]");

            try
            {
                _ = _transport.Invoke(CmdGaming, TypeSet, v.In, v.Out, requested.ToBytes());
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
                Log($"SET in={v.In} out={v.Out} 실패: {ex.Message}");
                continue;
            }

            if (!TryGet(out GpuPowerMode now, out string getErr))
            {
                lastError = $"명령은 전달됐지만 읽기 확인 실패: {getErr}";
                Log(lastError);
                continue;
            }

            readback = now;

            if (now.Ctgp == requested.Ctgp && now.Ppab == requested.Ppab)
            {
                _setOk = v;
                string note = now.DState != requested.DState
                    ? $" · dState {requested.DState} 요청은 반영되지 않음(현재 {now.DState})" : "";
                string tgpLabel = (now.Ctgp && now.Ppab) ? "최대 175W" : (now.Ppab ? "140W+Boost" : "기본 140W");
                message = $"GPU 전력 모드 적용 완료 ({tgpLabel} · cTGP={(now.Ctgp ? "ON" : "OFF")}, PPAB={(now.Ppab ? "ON" : "OFF")}){note}";
                Log($"SET 성공 in={v.In} out={v.Out} 읽기={now}{note}");
                return true;
            }

            lastError = $"BIOS 가 값을 받았지만 읽기값이 다릅니다 (요청: {requested} / 현재: {now})";
            Log(lastError);
        }

        message = $"GPU 전력 모드 적용 실패: {lastError}";
        Log(message);
        return false;
    }

    private static (int In, int Out)[] Order((int In, int Out)[] all, (int In, int Out)? preferred)
    {
        if (preferred is null) return all;
        var list = new List<(int In, int Out)> { preferred.Value };
        foreach (var v in all) if (v != preferred.Value) list.Add(v);
        return list.ToArray();
    }
}
