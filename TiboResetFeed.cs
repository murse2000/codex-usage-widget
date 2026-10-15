using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexUsageWidget;

internal sealed record TiboNotice(string Id, string Text, Uri Url, DateTimeOffset AnnouncedAt, DateTimeOffset? ReferenceAt)
{
    public string AlertKey => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{Id}\n{Text}\n{ReferenceAt?.ToUniversalTime():O}")));

    public string NotificationText
    {
        get
        {
            var preview = Text.Replace('\r', ' ').Replace('\n', ' ');
            if (preview.Length > 96) preview = preview[..96] + "…";
            return $"{preview}\n게시: {AnnouncedAt.LocalDateTime:M/d HH:mm}\n{Url}\n출처: codex-resets.com (비공식)";
        }
    }
}

internal sealed record TiboSnapshot(DateTimeOffset GeneratedAt, TiboNotice? Notice, bool ReferenceTimePassed);
internal sealed record TiboFetchResult(TiboSnapshot? Snapshot, DateTimeOffset RetryAt);

internal static class TiboResetFeed
{
    public const string SourceUrl = "https://codex-resets.com/";
    private const string Endpoint = SourceUrl + "api/v1/status";

    public static async Task<TiboFetchResult> ReadAsync(HttpClient client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
        request.Headers.UserAgent.ParseAdd("CodexUsageWidget/1.0 (+https://github.com/murse2000)");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var retryAt = now.AddMinutes(2);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var requiredRetry = response.Headers.RetryAfter?.Date ?? now + (response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(2));
            return new(null, requiredRetry > retryAt ? requiredRetry : retryAt);
        }
        response.EnsureSuccessStatusCode();
        return new(Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false), now), retryAt);
    }

    public static TiboSnapshot Parse(string json, DateTimeOffset now)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var meta = root.GetProperty("meta");
        if (meta.GetProperty("api_version").GetString() != "v1") throw new InvalidDataException("지원하지 않는 공지 자료 형식");
        var generatedAt = meta.GetProperty("generated_at").GetDateTimeOffset();
        if (generatedAt < now.AddMinutes(-15) || generatedAt > now.AddMinutes(5))
            throw new InvalidDataException("공지 자료 갱신 지연 · 재확인 필요");

        var scheduled = root.GetProperty("data").GetProperty("scheduled_reset");
        if (scheduled.ValueKind == JsonValueKind.Null) return new(generatedAt, null, false);
        var source = scheduled.GetProperty("source");
        if (scheduled.GetProperty("status").GetString() != "scheduled"
            || source.GetProperty("type").GetString() != "x_post"
            || source.GetProperty("author").GetString() != "thsottiaux")
            throw new InvalidDataException("Tibo의 X 리셋 예고를 확인할 수 없음");

        var id = scheduled.GetProperty("id").GetString()!;
        if (string.IsNullOrEmpty(id) || !id.All(char.IsAsciiDigit)
            || !Uri.TryCreate(source.GetProperty("url").GetString(), UriKind.Absolute, out var url)
            || url.Scheme != Uri.UriSchemeHttps || !url.IsDefaultPort || url.UserInfo.Length != 0
            || (url.Host != "x.com" && url.Host != "twitter.com")
            || url.AbsolutePath != $"/thsottiaux/status/{id}" || url.Query.Length != 0 || url.Fragment.Length != 0)
            throw new InvalidDataException("Tibo 공지 원문 링크를 확인할 수 없음");

        var text = scheduled.GetProperty("text").GetString();
        var announcedAt = scheduled.GetProperty("announced_at").GetDateTimeOffset();
        if (string.IsNullOrWhiteSpace(text) || announcedAt > now.AddMinutes(5))
            throw new InvalidDataException("Tibo 공지 내용 또는 게시 시각을 확인할 수 없음");
        var reference = scheduled.GetProperty("scheduled_for");
        DateTimeOffset? referenceAt = reference.ValueKind == JsonValueKind.Null ? null : reference.GetDateTimeOffset();
        // 참고 기한 경과는 리셋 완료를 뜻하지 않으며, 지난 예고를 새 알림으로 보내지 않습니다.
        return new(generatedAt, new(id, text, url, announcedAt, referenceAt), referenceAt <= now);
    }
}
