#nullable enable

using System;

namespace OmenMax16.Services;

/// <summary>
/// Placeholder EC transport.
///
/// This type makes HpOmenFanController compile immediately, but it does not
/// claim that EC access is available. Actual RPM reading requires a verified
/// kernel/driver-backed EC implementation for the specific 8DD5 board.
/// </summary>
public sealed class HpOmenEcTransport
{
    public bool IsAvailable(
        out string message)
    {
        message =
            "HP EC RPM reader is not configured. BIOS target fan levels remain available.";

        return false;
    }

    public bool TryReadFanRpm(
        out int? cpuRpm,
        out int? gpuRpm,
        out string message)
    {
        cpuRpm = null;
        gpuRpm = null;

        message =
            "HP EC RPM reader is not configured; actual RPM is unavailable.";

        return false;
    }
}
