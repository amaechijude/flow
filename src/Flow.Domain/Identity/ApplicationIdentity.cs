namespace Flow.Domain.Identity;

public sealed record ApplicationIdentity(
    string ApplicationKey,
    string DisplayName,
    string? DesktopFileId = null,
    string? ExecutablePath = null,
    string? IconRef = null);
