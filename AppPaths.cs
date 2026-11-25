namespace CodexUsageWidget;

internal static class AppPaths
{
    internal const string DataDirectoryEnvironmentVariable = "CODEX_USAGE_WIDGET_DATA_DIR";

    internal static string DataDirectory
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable(DataDirectoryEnvironmentVariable);
            return string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexUsageWidget")
                : Path.GetFullPath(configured);
        }
    }

    internal static string SettingsPath => Path.Combine(DataDirectory, "settings.json");
    internal static string HistoryPath(DateOnly date) => Path.Combine(DataDirectory, $"history-{date:yyyyMMdd}.jsonl");
    internal static string DiagnosticsPath(DateOnly date) => Path.Combine(DataDirectory, $"diagnostics-{date:yyyyMMdd}.jsonl");
}
