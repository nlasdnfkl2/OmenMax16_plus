#nullable enable

using System.Management;

namespace OmenMax16.Services;

/*
 * Read-only HP OMEN BIOS WMI capability probe.
 *
 * Add a System.Management package reference when the project does not
 * already reference it:
 *   <PackageReference Include="System.Management" Version="8.*" />
 *
 * This type deliberately does NOT send any BIOS write command. Its output
 * is the model-specific contract needed before enabling FAN AUTO/QUIET/MAX
 * or a custom fan curve.
 */
public sealed class HpOmenFanProbeService
{
    private const string WmiNamespace = @"\\.\root\WMI";

    public HpOmenFanProbeResult Probe()
    {
        List<string> classes = new();

        List<string> methods = new();

        List<string> diagnostics = new();

        try
        {
            ManagementScope scope =
                new(WmiNamespace);

            scope.Connect();

            /*
             * HP OMEN / Victus에서 가장 우선 확인할
             * BIOS WMI 클래스 목록.
             *
             * hpqBIntM 클래스 내부에 hpqBIOSInt128 등의
             * BIOS 호출 메서드가 존재하는 구조다.
             */
            string[] candidateClasses =
            {
            "hpqBIntM",
            "hpqBIntM128",
            "hpqBIOSInt",
            "HPQ_BIntM"
        };

            foreach (string candidate
                     in candidateClasses)
            {
                try
                {
                    using ManagementClass managementClass =
                        new(
                            scope,
                            new ManagementPath(
                                candidate),
                            null);

                    managementClass.Get();

                    classes.Add(candidate);

                    foreach (MethodData method
                             in managementClass.Methods)
                    {
                        methods.Add(
                            candidate +
                            "." +
                            method.Name);
                    }
                }
                catch (ManagementException)
                {
                    /*
                     * 해당 클래스가 없을 수 있으므로
                     * 후보별 예외는 무시하고 다음 후보를 검사.
                     */
                }
            }

            /*
             * 직접 후보 목록에서 발견하지 못한 경우,
             * root\\WMI의 HP 관련 WMI 클래스를 추가 탐색.
             */
            if (classes.Count == 0)
            {
                using ManagementObjectSearcher classSearch =
                    new(
                        scope,
                        new ObjectQuery(
                            "SELECT * FROM meta_class " +
                            "WHERE __CLASS LIKE 'hpqBInt%'"));

                foreach (ManagementObject item
                         in classSearch.Get())
                {
                    string? className =
                        item["__CLASS"]?
                        .ToString();

                    if (string.IsNullOrWhiteSpace(
                            className))
                    {
                        continue;
                    }

                    if (classes.Contains(
                            className,
                            StringComparer
                            .OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    try
                    {
                        using ManagementClass managementClass =
                            new(
                                scope,
                                new ManagementPath(
                                    className),
                                null);

                        managementClass.Get();

                        classes.Add(className);

                        foreach (MethodData method
                                 in managementClass.Methods)
                        {
                            methods.Add(
                                className +
                                "." +
                                method.Name);
                        }
                    }
                    catch (ManagementException)
                    {
                    }
                }
            }

            if (classes.Count == 0)
            {
                diagnostics.Add(
                    "No HP BIOS WMI class was found " +
                    "in root\\WMI.");

                diagnostics.Add(
                    "Run as Administrator and verify " +
                    "that HP System Event Utility, " +
                    "HP OMEN Gaming Hub components, " +
                    "and HP BIOS WMI support are installed.");
            }
            else
            {
                diagnostics.Add(
                    "HP BIOS WMI class detected: " +
                    string.Join(
                        ", ",
                        classes));
            }
        }
        catch (UnauthorizedAccessException)
        {
            diagnostics.Add(
                "Access denied. Run the application " +
                "as Administrator.");
        }
        catch (ManagementException exception)
        {
            diagnostics.Add(
                "WMI connection failed: " +
                exception.Message);
        }
        catch (Exception exception)
        {
            diagnostics.Add(
                "Unexpected probe failure: " +
                exception.Message);
        }

        bool hasBiosInt128 =
            methods.Any(
                method =>
                    method.EndsWith(
                        ".hpqBIOSInt128",
                        StringComparison
                        .OrdinalIgnoreCase));

        return new HpOmenFanProbeResult(
            classes
                .OrderBy(
                    value => value)
                .ToArray(),

            methods
                .OrderBy(
                    value => value)
                .ToArray(),

            hasBiosInt128,

            diagnostics.ToArray());
    }
}

public sealed record HpOmenFanProbeResult(
    IReadOnlyList<string> Classes,
    IReadOnlyList<string> Methods,
    bool HasBiosInt128,
    IReadOnlyList<string> Diagnostics)
{
    public string ToDisplayText()
    {
        List<string> lines = new();

        lines.Add(
            HasBiosInt128
                ? "HP BIOS WMI transport: detected"
                : "HP BIOS WMI transport: not detected");

        foreach (string diagnostic in Diagnostics)
        {
            lines.Add(diagnostic);
        }

        lines.Add("Classes:");

        lines.AddRange(
            Classes.Count == 0
                ? new[] { "  (none)" }
                : Classes.Select(value => "  " + value));

        lines.Add("Methods:");

        lines.AddRange(
            Methods.Count == 0
                ? new[] { "  (none)" }
                : Methods.Select(value => "  " + value));

        return string.Join(
            Environment.NewLine,
            lines);
    }
}
