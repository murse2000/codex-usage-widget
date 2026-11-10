using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace CodexUsageWidget;

internal sealed record GoUsageWindow(string Key, string Label, int UsedPercent, int WindowMinutes, long ResetsAt);

internal sealed class OpenCodeGoAuthException : Exception
{
    internal OpenCodeGoAuthException() : base("OpenCode Go API 키가 인증되지 않았습니다.") { }
}

internal sealed class OpenCodeGoNotConfiguredException : Exception
{
    internal OpenCodeGoNotConfiguredException() : base("OpenCode Go API 키가 설정되지 않았습니다.") { }
}

internal sealed class OpenCodeGoRateLimitException : Exception
{
    internal OpenCodeGoRateLimitException() : base("OpenCode Go 요청 제한 · 잠시 후 재확인") { }
}

/// <summary>
/// OpenCode Go 구독의 5시간(rolling)·주간·월간 사용률을 조회한다.
/// 응답의 percent는 사용률이므로 화면에는 100 - percent를 잔여량으로 표시한다.
/// </summary>
internal static class OpenCodeGoUsage
{
    internal const string Endpoint = "https://opencode.ai/zen/go/v1/usage";
    internal const int MaximumResponseSize = 262144;

    private static readonly (string Key, string Field, string Label, int Minutes)[] Definitions =
    {
        ("rolling", "rolling", "5시간 한도", 300),
        ("weekly", "weekly", "주간 한도", 10080),
        ("monthly", "monthly", "월간 한도", 0)
    };

    internal static async Task<IReadOnlyList<GoUsageWindow>> ReadAsync(HttpClient client, string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new OpenCodeGoNotConfiguredException();
        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.UserAgent.ParseAdd($"CodexUsageWidget/{AppUpdate.CurrentVersion.ToString(3)}");
        request.Headers.TryAddWithoutValidation("x-opencode-session", "codex-usage-widget");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new OpenCodeGoAuthException();
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new OpenCodeGoRateLimitException();
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaximumResponseSize)
            throw new InvalidDataException("OpenCode Go 응답이 허용 크기를 초과했습니다.");
        return Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
    }

    internal static IReadOnlyList<GoUsageWindow> Parse(string json)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(json); }
        catch (JsonException) { throw new InvalidDataException("OpenCode Go 응답을 확인할 수 없습니다."); }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("usage", out var usage)
                || usage.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("OpenCode Go 응답을 확인할 수 없습니다.");

            var windows = new List<GoUsageWindow>(Definitions.Length);
            foreach (var (key, field, label, minutes) in Definitions)
            {
                if (!usage.TryGetProperty(field, out var item) || item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("percent", out var percent) || percent.ValueKind != JsonValueKind.Number
                    || !percent.TryGetDouble(out var used)) continue;
                if (!item.TryGetProperty("resetsAt", out var reset) || reset.ValueKind != JsonValueKind.String
                    || !reset.TryGetDateTimeOffset(out var resetsAt)) continue;
                windows.Add(new GoUsageWindow(key, label, (int)Math.Round(Math.Clamp(used, 0, 100)), minutes, resetsAt.ToUnixTimeSeconds()));
            }
            if (windows.Count == 0) throw new InvalidDataException("OpenCode Go 사용량을 확인할 수 없습니다.");
            return windows;
        }
    }
}
