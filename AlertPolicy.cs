namespace CodexUsageWidget;

internal sealed record AlertPolicy(bool Enabled, int WarningPercent, int CriticalPercent, int RepeatMinutes)
{
    internal static AlertPolicy Default { get; } = new(true, 30, 10, 0);

    internal AlertPolicy Normalize()
    {
        var warning = Math.Clamp(WarningPercent, 1, 100);
        var critical = Math.Clamp(CriticalPercent, 0, warning - 1);
        return new AlertPolicy(Enabled, warning, critical, Math.Clamp(RepeatMinutes, 0, 1440));
    }
}

internal sealed class AlertPolicySettings
{
    public Dictionary<string, AlertPolicy> Policies { get; set; } = new();

    internal AlertPolicy Get(string key)
        => Policies is not null && Policies.TryGetValue(key, out var policy) ? policy.Normalize() : AlertPolicy.Default;

    internal void Set(string key, AlertPolicy policy)
    {
        Policies ??= new();
        Policies[key] = policy.Normalize();
    }

    internal AlertPolicySettings Clone()
    {
        var clone = new AlertPolicySettings();
        if (Policies is not null)
            foreach (var pair in Policies) clone.Policies[pair.Key] = pair.Value;
        return clone;
    }

    internal void Normalize()
    {
        Policies ??= new();
        foreach (var key in Policies.Keys.ToArray()) Policies[key] = Policies[key].Normalize();
    }
}

internal sealed class AlertQuietHours
{
    public bool Enabled { get; set; }
    public int StartHour { get; set; } = 22;
    public int EndHour { get; set; } = 8;

    internal AlertQuietHours Clone() => new() { Enabled = Enabled, StartHour = StartHour, EndHour = EndHour };

    internal void Normalize()
    {
        StartHour = Math.Clamp(StartHour, 0, 23);
        EndHour = Math.Clamp(EndHour, 0, 23);
    }

    internal bool IsQuiet(DateTimeOffset now)
    {
        if (!Enabled || StartHour == EndHour) return false;
        var hour = now.Hour;
        return StartHour < EndHour
            ? hour >= StartHour && hour < EndHour
            : hour >= StartHour || hour < EndHour;
    }
}

internal sealed class AlertState
{
    public long ResetsAt { get; set; }
    public int Level { get; set; }
    public long LastAlertAtUnix { get; set; }
}

internal sealed record AlertEvaluation(AlertState State, int Threshold);

internal sealed record AlertStateChange(string Key, AlertState? Previous, AlertState Next);

internal static class AlertTracker
{
    internal static AlertEvaluation Evaluate(AlertState state, AlertPolicy policy, int remaining, long resetsAt,
        DateTimeOffset now, bool quiet)
    {
        policy = policy.Normalize();
        var sameCycle = state.ResetsAt == resetsAt;
        var next = new AlertState
        {
            ResetsAt = resetsAt,
            Level = sameCycle ? Math.Clamp(state.Level, 0, 2) : 0,
            LastAlertAtUnix = sameCycle ? state.LastAlertAtUnix : 0
        };
        if (!policy.Enabled)
        {
            next.Level = 0;
            return new AlertEvaluation(next, 0);
        }
        if (quiet) return new AlertEvaluation(next, 0);

        var level = remaining < policy.CriticalPercent ? 2 : remaining < policy.WarningPercent ? 1 : 0;
        if (level == 0)
        {
            next.Level = 0;
            return new AlertEvaluation(next, 0);
        }

        var threshold = level == 2 ? policy.CriticalPercent : policy.WarningPercent;
        var crossed = level > next.Level;
        var repeated = level == next.Level && policy.RepeatMinutes > 0
            && (next.LastAlertAtUnix == 0 || now.ToUnixTimeSeconds() - next.LastAlertAtUnix >= policy.RepeatMinutes * 60L);
        next.Level = level;
        if (!crossed && !repeated) return new AlertEvaluation(next, 0);
        next.LastAlertAtUnix = now.ToUnixTimeSeconds();
        return new AlertEvaluation(next, threshold);
    }
}

internal static class AlertStateTransaction
{
    internal static bool Apply(Dictionary<string, AlertState> alerts, IReadOnlyList<AlertStateChange> changes, Func<bool> save)
    {
        foreach (var change in changes) alerts[change.Key] = change.Next;
        try
        {
            if (save()) return true;
        }
        catch
        {
            Rollback(alerts, changes);
            throw;
        }
        Rollback(alerts, changes);
        return false;
    }

    private static void Rollback(Dictionary<string, AlertState> alerts, IReadOnlyList<AlertStateChange> changes)
    {
        for (var index = changes.Count - 1; index >= 0; index--)
        {
            var change = changes[index];
            if (change.Previous is null) alerts.Remove(change.Key);
            else alerts[change.Key] = change.Previous;
        }
    }
}
