#nullable enable

using System.Management;
using System.Text;

namespace OmenMax16.Services;

public sealed class HpOmenWmiSchemaDiagnostic
{
    private const string WmiNamespace =
        @"\\.\root\WMI";

    public string ReadReport()
    {
        StringBuilder report =
            new();

        try
        {
            ManagementScope scope =
                new(WmiNamespace);

            scope.Connect();

            report.AppendLine(
                "=== hpqBIntM Instances ===");

            using ManagementObjectSearcher biosSearch =
                new(
                    scope,
                    new ObjectQuery(
                        "SELECT * FROM hpqBIntM"));

            using ManagementObjectCollection biosInstances =
                biosSearch.Get();

            foreach (ManagementObject instance
                     in biosInstances)
            {
                report.AppendLine(
                    "Path: " +
                    instance.Path);

                report.AppendLine(
                    "InstanceName: " +
                    (instance["InstanceName"]?
                     .ToString() ??
                     "(null)"));

                report.AppendLine(
                    "Active: " +
                    (instance["Active"]?
                     .ToString() ??
                     "(unknown)"));

                report.AppendLine();
            }

            report.AppendLine(
                "=== hpqBDataIn Properties ===");

            using ManagementClass dataClass =
                new(
                    scope,
                    new ManagementPath(
                        "hpqBDataIn"),
                    null);

            dataClass.Get();

            foreach (PropertyData property
                     in dataClass.Properties)
            {
                report.AppendLine(
                    property.Name +
                    " : " +
                    property.Type);
            }

            report.AppendLine();

            report.AppendLine(
                "=== hpqBIOSInt128 Contract ===");

            using ManagementClass biosClass =
                new(
                    scope,
                    new ManagementPath(
                        "hpqBIntM"),
                    null);

            biosClass.Get();

            MethodData? method =
                biosClass.Methods[
                    "hpqBIOSInt128"];

            if (method is null)
            {
                report.AppendLine(
                    "hpqBIOSInt128 method not found.");

                return report.ToString();
            }

            report.AppendLine(
                "Input parameters:");

            if (method.InParameters is not null)
            {
                foreach (PropertyData property
                         in method.InParameters
                         .Properties)
                {
                    report.AppendLine(
                        "  " +
                        property.Name +
                        " : " +
                        property.Type);
                }
            }

            report.AppendLine(
                "Output parameters:");

            if (method.OutParameters is not null)
            {
                foreach (PropertyData property
                         in method.OutParameters
                         .Properties)
                {
                    report.AppendLine(
                        "  " +
                        property.Name +
                        " : " +
                        property.Type);
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            report.AppendLine(
                "Access denied.");

            report.AppendLine(
                "Run the application " +
                "as Administrator.");
        }
        catch (ManagementException exception)
        {
            report.AppendLine(
                "WMI error: " +
                exception.Message);
        }
        catch (Exception exception)
        {
            report.AppendLine(
                "Unexpected error: " +
                exception.Message);
        }

        return report.ToString();
    }
}