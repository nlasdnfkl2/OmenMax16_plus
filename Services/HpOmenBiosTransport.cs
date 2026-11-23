#nullable enable
using System;
using System.Management;

namespace OmenMax16.Services;

/// <summary>
/// BIOS WMI 호출 결과. HpOmenFanController 는 Invoke(...).Data 형태로 사용합니다.
/// </summary>
public readonly struct HpOmenBiosResult
{
    public byte[] Data { get; }
    public HpOmenBiosResult(byte[] data) => Data = data;
}

/// <summary>
/// HP OMEN BIOS WMI 저수준 전송 계층 (최종 수정본 v2).
///
/// v2 에서 추가로 해결한 문제: "상태 읽기 실패(잘못된 매개변수)" / ManagementStatus.InvalidParameter
///
///   원인: hpqBData 입력 배열을 항상 128바이트로 고정 생성했는데, Size 필드는 호출마다
///   다른 값(예: GetMaxFan 호출 시 4)을 넣고 있었습니다. 즉 "Size=4" 라고 선언하면서
///   실제 hpqBData 배열 길이는 128바이트인 불일치가 발생해서 BIOS 가 요청을 "잘못된
///   매개변수"로 거부했습니다. GetFanLevel(outDataSize=128) 호출은 우연히 Size 와 배열
///   길이가 둘 다 128 이라 통과했지만, GetMaxFan(outDataSize=4) 에서 드러났습니다.
///
///   해결: hpqBData 배열 길이를 outDataSize 에 정확히 맞춰서 동적으로 생성합니다.
///   Size 필드도 그 실제 배열 길이와 항상 일치시킵니다.
///
/// v1 에서 해결한 근본 원인 (유지):
///   HP BIOS 는 WMI 요청의 hpqBDataIn 인스턴스에 "Sign" = {0x53,0x45,0x43,0x55}("SECU")
///   서명값이 정확히 설정되어야만 요청을 인가된 것으로 인정합니다.
///
/// 확인된 실제 WMI 스키마 (PowerShell Get-CimClass 로 직접 검증):
///   - 네임스페이스: root\wmi
///   - 입력 클래스: hpqBDataIn { Active, Command(UInt32), CommandType(UInt32),
///                              hpqBData(UInt8Array), InstanceName, Sign(UInt8Array), Size(UInt32) }
///   - 출력 클래스: hpqBDataOut{size} { Active, Data(UInt8Array), InstanceName, rwReturnCode(UInt32), Sign }
///   - 메서드 클래스: hpqBIntM, 인스턴스 경로 "ACPI\PNP0C14\0_0" (실제 기기에서 확인됨)
///   - 메서드 이름: hpqBIOSInt{size} 예) hpqBIOSInt128, hpqBIOSInt4
/// </summary>
public sealed class HpOmenBiosTransport
{
    private const string Namespace = @"root\wmi";
    private const string DataInClass = "hpqBDataIn";
    private const string DataInField = "hpqBData";      // 입력 페이로드 필드명 (Data 아님!)
    private const string DataOutField = "Data";          // 출력 페이로드 필드명
    private const string MethodClass = "hpqBIntM";
    private const string MethodNamePrefix = "hpqBIOSInt";
    private const string MethodInstancePath = @"ACPI\PNP0C14\0_0";

    // HP BIOS 가 요청을 인가된 것으로 인정하기 위해 반드시 필요한 서명값 ("SECU")
    private static readonly byte[] Sign = { 0x53, 0x45, 0x43, 0x55 };

    private readonly object _connectSync = new();
    private ManagementObject? _methodObject;
    private ManagementScope? _scope;

    /// <summary>
    /// BIOS WMI 메서드를 호출합니다.
    /// </summary>
    /// <param name="command">최상위 커맨드 (예: 0x00020008)</param>
    /// <param name="commandType">세부 커맨드 타입 (예: 0x2D)</param>
    /// <param name="outDataSize">기대하는 응답 크기 (0, 4, 128, 1024, 4096 중 하나) - hpqBData 배열 길이도 이 값과 일치시킵니다.</param>
    /// <param name="data">입력 페이로드 (outDataSize 길이로 잘리거나 0으로 채워짐)</param>
    public HpOmenBiosResult Invoke(uint command, uint commandType, int outDataSize, byte[] data)
    => Invoke(command, commandType, outDataSize, outDataSize, data);

    public HpOmenBiosResult Invoke(uint command, uint commandType, int inDataSize, int outDataSize, byte[] data)
    {
        try
        {
            using ManagementClass dataInClass = new ManagementClass(GetScope(), new ManagementPath(DataInClass), null);
            using ManagementObject inParam = dataInClass.CreateInstance()
                ?? throw new InvalidOperationException($"{DataInClass} 인스턴스를 생성할 수 없습니다.");

            inParam["Command"] = command;
            inParam["CommandType"] = commandType;
            inParam["Sign"] = Sign;

            // hpqBData 배열 길이 = Size 필드 = inDataSize (불일치하면 InvalidParameter)
            byte[] payload = new byte[inDataSize];
            if (data is not null)
            {
                Array.Copy(data, payload, Math.Min(data.Length, payload.Length));
            }
            inParam["Size"] = (uint)payload.Length;
            inParam[DataInField] = payload;

            ManagementObject method = GetMethodObject();
            string methodName = MethodNamePrefix + outDataSize;

            using ManagementBaseObject methodParams = method.GetMethodParameters(methodName);
            methodParams["InData"] = inParam;

            using ManagementBaseObject outParams = method.InvokeMethod(methodName, methodParams, null);

            if (outParams is null)
            {
                throw new InvalidOperationException($"{methodName} 호출이 null 결과를 반환했습니다.");
            }

            using ManagementBaseObject? outData = outParams["OutData"] as ManagementBaseObject;
            if (outData is null)
            {
                throw new InvalidOperationException($"{methodName} 응답에 OutData 가 없습니다.");
            }

            uint returnCode = outData["rwReturnCode"] is uint rc ? rc : 0;
            byte[] resultData = outData[DataOutField] as byte[] ?? Array.Empty<byte>();

            if (returnCode != 0)
            {
                throw new InvalidOperationException(
                    $"BIOS 가 커맨드를 거부했습니다 (rwReturnCode=0x{returnCode:X8}). " +
                    $"command=0x{command:X8}, commandType=0x{commandType:X2}, in={inDataSize}, out={outDataSize}");
            }

            return new HpOmenBiosResult(resultData);
        }
        catch (ManagementException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not ManagementException)
        {
            throw new InvalidOperationException(
                $"BIOS Invoke 실패 (command=0x{command:X8}, type=0x{commandType:X2}, in={inDataSize}, out={outDataSize}): {ex.Message}", ex);
        }
    }
    private ManagementScope GetScope()
    {
        lock (_connectSync)
        {
            if (_scope is { IsConnected: true }) return _scope;

            _scope = new ManagementScope(Namespace, new ConnectionOptions
            {
                Impersonation = ImpersonationLevel.Impersonate,
                EnablePrivileges = true
            });
            _scope.Connect();
            return _scope;
        }
    }

    private ManagementObject GetMethodObject()
    {
        lock (_connectSync)
        {
            if (_methodObject is not null) return _methodObject;

            ManagementPath path = new ManagementPath($"{MethodClass}.InstanceName='{MethodInstancePath}'");
            _methodObject = new ManagementObject(GetScope(), path, null);
            _methodObject.Get(); // 인스턴스가 실제로 존재하는지 즉시 검증
            return _methodObject;
        }
    }

    /// <summary>
    /// 연결을 재설정합니다 (앱 종료 시 또는 오류 복구 시 호출).
    /// </summary>
    public void ResetConnection()
    {
        lock (_connectSync)
        {
            _methodObject?.Dispose();
            _methodObject = null;
            _scope = null;
        }
    }
}