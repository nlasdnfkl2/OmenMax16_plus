#nullable enable

namespace OmenMax16.Models;

public sealed record HpBiosDataOut(
    string OriginalDataType,
    bool? Active,
    byte[] Data,
    string InstanceName,
    uint RwReturnCode,
    byte[] Sign);