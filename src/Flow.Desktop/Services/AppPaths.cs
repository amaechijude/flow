using System;
using System.IO;

namespace Flow.Desktop.Services;

/// <summary>Single source of truth for the local database path used by the UI.</summary>
public static class AppPaths
{
    public const string DatabasePathVariable = "Flow__DatabasePath";
    public const string ExportDirectoryVariable = "Flow__ExportDirectory";

    public static string DatabasePath()
        => Environment.GetEnvironmentVariable(DatabasePathVariable)
           ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "flow", "flow.db");
}
