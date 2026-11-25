using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CodexUsageWidget;

internal sealed record PerformanceDiagnosticsEntry(
    long WorkingSet,
    long PrivateBytes,
    int ThreadCount,
    int HandleCount,
    long GcHeapBytes,
    long RefreshMilliseconds,
    long? UiaMilliseconds,
    int? CodexProcessId,
    bool CodexOk,
    bool GoOk,
    long TimestampUtc,
    string? Kind = null,
    string? Message = null)
{
    internal bool IsRefresh => Kind is null;
}

internal sealed class PerformanceDiagnostics
{
    internal static readonly TimeSpan Retention = TimeSpan.FromDays(7);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(6);
    private const int MaxLineLength = 16_384;
    private const int MaxKindLength = 64;
    private const int MaxMessageLength = 512;
    private const string FilePrefix = "diagnostics-";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private static readonly Regex[] SensitivePatterns =
    {
        new(@"(?i)\b(?:proxy-)?authorization\s*:\s*[^\r\n;]+", RegexOptions.Compiled | RegexOptions.CultureInvariant),
        new(@"(?i)\bbearer\s+[A-Za-z0-9._~+/=-]+", RegexOptions.Compiled | RegexOptions.CultureInvariant),
        new(@"(?i)\b(?:api[-_ ]?key|access[-_ ]?token|refresh[-_ ]?token|client[-_ ]?secret|password|passwd|pwd|secret|token|credential|cipher(?:text)?)\b\s*(?::|=|\bis\b)?\s*[^\s;,]+",
            RegexOptions.Compiled | RegexOptions.CultureInvariant),
        new(@"(?i)\b(?:(?:oc_)?sk|pk|rk|ghp|github_pat|xox[baprs]|go)[-_][A-Za-z0-9][A-Za-z0-9._-]{7,}\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant),
        new(@"(?i)\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\b", RegexOptions.Compiled | RegexOptions.CultureInvariant),
        new(@"(?i)\b[0-9a-f]{32,}\b", RegexOptions.Compiled | RegexOptions.CultureInvariant),
        new(@"-----BEGIN [^-]*PRIVATE KEY-----.*?-----END [^-]*PRIVATE KEY-----", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline)
    };

    private readonly string? directory;
    private readonly object gate = new();
    private DateTimeOffset lastCleanup;

    internal PerformanceDiagnostics(string? directory = null)
    {
        this.directory = directory;
    }

    internal void RecordRefresh(TimeSpan elapsed, long? uiaMilliseconds, int? codexProcessId, bool codexOk, bool goOk)
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var entry = new PerformanceDiagnosticsEntry(
            process.WorkingSet64,
            process.PrivateMemorySize64,
            process.Threads.Count,
            process.HandleCount,
            GC.GetTotalMemory(forceFullCollection: false),
            Math.Max(0, (long)elapsed.TotalMilliseconds),
            uiaMilliseconds is { } uia ? Math.Max(0, uia) : null,
            codexProcessId is > 0 ? codexProcessId : null,
            codexOk,
            goOk,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        Append(entry);
    }

    internal void RecordEvent(string kind, string message)
    {
        Append(new PerformanceDiagnosticsEntry(
            0, 0, 0, 0, 0, 0, null, null, false, false,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Limit(Redact(kind), MaxKindLength),
            Limit(Redact(message), MaxMessageLength)));
    }

    internal IReadOnlyList<PerformanceDiagnosticsEntry> Load(TimeSpan range, DateTimeOffset now)
    {
        if (range <= TimeSpan.Zero) return Array.Empty<PerformanceDiagnosticsEntry>();
        var cutoff = now.ToUnixTimeSeconds() - (long)range.TotalSeconds;
        var nowUnix = now.ToUnixTimeSeconds();
        var result = new List<PerformanceDiagnosticsEntry>();
        lock (gate)
        {
            var root = RootDirectory;
            if (!Directory.Exists(root)) return result;
            var firstDate = DateOnly.FromDateTime(now.UtcDateTime).AddDays(-(int)Math.Ceiling(range.TotalDays) - 1);
            foreach (var path in Directory.EnumerateFiles(root, FilePrefix + "*.jsonl"))
            {
                if (!TryReadFileDate(path, out var date) || date < firstDate) continue;
                foreach (var line in File.ReadLines(path))
                {
                    if (line.Length > MaxLineLength) continue;
                    try
                    {
                        var entry = JsonSerializer.Deserialize<PerformanceDiagnosticsEntry>(line, JsonOptions);
                        if (!IsUsable(entry, cutoff, nowUnix)) continue;
                        result.Add(entry!.Kind is null ? entry : entry with
                        {
                            Kind = Limit(Redact(entry.Kind), MaxKindLength),
                            Message = Limit(Redact(entry.Message), MaxMessageLength)
                        });
                    }
                    catch (JsonException) { }
                }
            }
        }
        result.Sort((left, right) => left.TimestampUtc.CompareTo(right.TimestampUtc));
        return result;
    }

    private void Append(PerformanceDiagnosticsEntry entry)
    {
        var date = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(entry.TimestampUtc).UtcDateTime);
        var line = JsonSerializer.Serialize(entry, JsonOptions);
        lock (gate)
        {
            Directory.CreateDirectory(RootDirectory);
            File.AppendAllText(PathFor(date), line + Environment.NewLine, new UTF8Encoding(false));
            CleanupIfNeeded(DateTimeOffset.UtcNow);
        }
    }

    private void CleanupIfNeeded(DateTimeOffset now)
    {
        if (now - lastCleanup < CleanupInterval) return;
        lastCleanup = now;
        var cutoff = DateOnly.FromDateTime(now.UtcDateTime).AddDays(-(int)Retention.TotalDays);
        foreach (var path in Directory.EnumerateFiles(RootDirectory, FilePrefix + "*.jsonl"))
        {
            if (TryReadFileDate(path, out var date) && date < cutoff) File.Delete(path);
        }
    }

    private string RootDirectory => directory ?? AppPaths.DataDirectory;

    private string PathFor(DateOnly date) => directory is null
        ? AppPaths.DiagnosticsPath(date)
        : Path.Combine(directory, $"{FilePrefix}{date:yyyyMMdd}.jsonl");

    private static bool IsUsable(PerformanceDiagnosticsEntry? entry, long cutoff, long now)
        => entry is not null
            && entry.TimestampUtc >= cutoff
            && entry.TimestampUtc <= now
            && entry.WorkingSet >= 0
            && entry.PrivateBytes >= 0
            && entry.ThreadCount >= 0
            && entry.HandleCount >= 0
            && entry.GcHeapBytes >= 0
            && entry.RefreshMilliseconds >= 0
            && (entry.UiaMilliseconds is null || entry.UiaMilliseconds >= 0)
            && (entry.CodexProcessId is null || entry.CodexProcessId > 0)
            && (!entry.IsRefresh || (entry.ThreadCount > 0 && entry.HandleCount > 0));

    private static bool TryReadFileDate(string path, out DateOnly date)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return DateOnly.TryParseExact(name.AsSpan(FilePrefix.Length), "yyyyMMdd", out date);
    }

    private static string Redact(string? value)
    {
        var text = value ?? string.Empty;
        foreach (var pattern in SensitivePatterns) text = pattern.Replace(text, "[redacted]");
        return text;
    }

    private static string Limit(string value, int maximum)
    {
        if (value.Length <= maximum) return value;
        var end = maximum;
        if (char.IsHighSurrogate(value[end - 1]) && end < value.Length && char.IsLowSurrogate(value[end])) end--;
        return value[..end];
    }
}
