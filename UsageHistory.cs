using System.Text.Json;

namespace CodexUsageWidget;

internal sealed record UsageHistoryPoint(long TimestampUtc, string Service, string Key, string Label, int Remaining, long ResetsAt);

internal sealed record UsageHistorySegment(string Service, string Key, string Label, long ResetsAt, IReadOnlyList<UsageHistoryPoint> Points)
{
    internal DateTimeOffset StartedAt => DateTimeOffset.FromUnixTimeSeconds(Points[0].TimestampUtc);
}

internal sealed class UsageHistoryStore
{
    internal static readonly TimeSpan Retention = TimeSpan.FromDays(30);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(6);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private readonly string directory;
    private readonly object gate = new();
    private DateTimeOffset lastCleanup;

    internal UsageHistoryStore(string? directory = null)
    {
        this.directory = directory ?? AppPaths.DataDirectory;
    }

    internal void Record(IEnumerable<UsageHistoryPoint> points)
    {
        var valid = points
            .Where(point => point.TimestampUtc > 0 && !string.IsNullOrWhiteSpace(point.Service) && !string.IsNullOrWhiteSpace(point.Key))
            .Select(point => point with { Remaining = Math.Clamp(point.Remaining, 0, 100) })
            .ToArray();
        lock (gate)
        {
            Directory.CreateDirectory(directory);
            foreach (var group in valid.GroupBy(point => DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(point.TimestampUtc).UtcDateTime)))
            {
                var path = Path.Combine(directory, $"history-{group.Key:yyyyMMdd}.jsonl");
                var lines = group.Select(point => JsonSerializer.Serialize(point, JsonOptions));
                File.AppendAllLines(path, lines);
            }
            CleanupIfNeeded(DateTimeOffset.UtcNow);
        }
    }

    internal IReadOnlyList<UsageHistoryPoint> Load(TimeSpan range, DateTimeOffset now)
    {
        if (range <= TimeSpan.Zero) return Array.Empty<UsageHistoryPoint>();
        var cutoff = now.ToUnixTimeSeconds() - (long)range.TotalSeconds;
        var nowUnix = now.ToUnixTimeSeconds();
        var result = new List<UsageHistoryPoint>();
        lock (gate)
        {
            if (!Directory.Exists(directory)) return result;
            foreach (var path in Directory.EnumerateFiles(directory, "history-*.jsonl"))
            {
                if (!TryReadFileDate(path, out var date) || date < DateOnly.FromDateTime(now.UtcDateTime).AddDays(-(int)Math.Ceiling(range.TotalDays) - 1))
                    continue;
                foreach (var line in File.ReadLines(path))
                {
                    if (line.Length > 65536) continue;
                    try
                    {
                        var point = JsonSerializer.Deserialize<UsageHistoryPoint>(line, JsonOptions);
                        if (point is null || string.IsNullOrWhiteSpace(point.Service) || string.IsNullOrWhiteSpace(point.Key)
                            || point.TimestampUtc < cutoff || point.TimestampUtc > nowUnix) continue;
                        result.Add(point with { Remaining = Math.Clamp(point.Remaining, 0, 100) });
                    }
                    catch (JsonException) { }
                }
            }
        }
        result.Sort((left, right) => left.TimestampUtc.CompareTo(right.TimestampUtc));
        return result;
    }

    internal void DeleteAll()
    {
        lock (gate)
        {
            if (!Directory.Exists(directory)) return;
            foreach (var path in Directory.EnumerateFiles(directory, "history-*.jsonl")) File.Delete(path);
        }
    }

    private void CleanupIfNeeded(DateTimeOffset now)
    {
        if (now - lastCleanup < CleanupInterval) return;
        lastCleanup = now;
        var cutoff = DateOnly.FromDateTime(now.UtcDateTime).AddDays(-(int)Retention.TotalDays);
        foreach (var path in Directory.EnumerateFiles(directory, "history-*.jsonl"))
        {
            if (TryReadFileDate(path, out var date) && date < cutoff) File.Delete(path);
        }
    }

    private static bool TryReadFileDate(string path, out DateOnly date)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return DateOnly.TryParseExact(name.AsSpan("history-".Length), "yyyyMMdd", out date);
    }
}

internal static class UsageHistorySeries
{
    internal static IReadOnlyList<UsageHistorySegment> Build(IReadOnlyList<UsageHistoryPoint> points, TimeSpan range,
        DateTimeOffset now, int maxPoints)
    {
        var cutoff = now.ToUnixTimeSeconds() - (long)range.TotalSeconds;
        var nowUnix = now.ToUnixTimeSeconds();
        var ordered = points
            .Where(point => point.TimestampUtc >= cutoff && point.TimestampUtc <= nowUnix)
            .OrderBy(point => point.Service)
            .ThenBy(point => point.Key)
            .ThenBy(point => point.TimestampUtc)
            .ToArray();
        var groups = new List<List<UsageHistoryPoint>>();
        List<UsageHistoryPoint>? group = null;
        foreach (var point in ordered)
        {
            if (group is null || group[^1].Service != point.Service || group[^1].Key != point.Key
                || group[^1].ResetsAt != point.ResetsAt)
            {
                group = new List<UsageHistoryPoint>();
                groups.Add(group);
            }
            group.Add(point);
        }
        return groups.Select(points =>
        {
            var first = points[0];
            return new UsageHistorySegment(first.Service, first.Key, first.Label, first.ResetsAt,
                Downsample(points.ToArray(), maxPoints));
        }).ToArray();
    }

    internal static IReadOnlyList<UsageHistoryPoint> Downsample(IReadOnlyList<UsageHistoryPoint> points, int maxPoints)
    {
        if (points.Count <= maxPoints) return points;
        if (maxPoints <= 0) return Array.Empty<UsageHistoryPoint>();
        var minimum = 0;
        for (var index = 1; index < points.Count; index++)
            if (points[index].Remaining < points[minimum].Remaining) minimum = index;
        if (maxPoints < 3)
        {
            var selected = new SortedSet<int> { 0 };
            if (maxPoints == 2) selected.Add(minimum);
            return selected.Select(index => points[index]).ToArray();
        }
        var last = points.Count - 1;
        var sampled = new SortedSet<int>();
        for (var slot = 0; slot < maxPoints; slot++)
            sampled.Add((int)((long)slot * last / (maxPoints - 1)));
        if (!sampled.Contains(minimum))
        {
            var replacement = sampled
                .Where(index => index != 0 && index != last)
                .OrderBy(index => Math.Abs(index - minimum))
                .ThenBy(index => index)
                .First();
            sampled.Remove(replacement);
            sampled.Add(minimum);
        }
        return sampled.Select(index => points[index]).ToArray();
    }
}
