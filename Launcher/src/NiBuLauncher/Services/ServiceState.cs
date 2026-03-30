namespace NiBuLauncher.Services;

public readonly record struct ServiceState(bool ProcessRunning, bool HealthOk, string? Details = null);

public sealed record StatusSnapshot(
    ServiceState Caddy,
    ServiceState Php,
    ServiceState Bridge,
    ServiceState Python
);
