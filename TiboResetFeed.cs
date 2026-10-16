using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexUsageWidget;

internal sealed record TiboNotice(string Id, string Text, Uri Url, DateTimeOffset AnnouncedAt, DateTimeOffset? ReferenceAt, bool IsWatch = false, int? ChancePercent = null)
{
    public string AlertKey => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{(IsWatch ? "watch\n" : "")}{Id}\n{Text}\n{ReferenceAt?.ToUniversalTime():O}")));
    public string NotificationTitle => IsWatch ? "Tibo · 리셋 가능성 관찰" : "Tibo · Codex 리셋 예고";

    public string NotificationText
    {
        get
        {
            var preview = Text.Replace('\r', ' ').Replace('\n', ' ');
            var length = IsWatch ? 64 : 96;
            if (preview.Length > length) preview = preview[..length] + "…";
            var caution = IsWatch ? "확정 예고 아님" + (ChancePercent is { } chance ? $" · 서비스 추정 {chance}%" : "") + "\n" : "";
            var time = IsWatch ? $"관찰 기한: {ReferenceAt?.LocalDateTime:M/d HH:mm}" : $"게시: {AnnouncedAt.LocalDateTime:M/d HH:mm}";
            return $"{caution}{preview}\n{time}\n{Url}\n출처: codex-resets.com (비공식)";
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

        var data = root.GetProperty("data");
        var item = data.GetProperty("scheduled_reset");
        var isWatch = item.ValueKind == JsonValueKind.Null;
        if (isWatch) item = data.GetProperty("active_watch");
        if (item.ValueKind == JsonValueKind.Null) return new(generatedAt, null, false);
        var source = item.GetProperty("source");
        if ((isWatch ? item.GetProperty("level").GetString() is not ("elevated" or "strong") : item.GetProperty("status").GetString() != "scheduled")
            || source.GetProperty("type").GetString() != "x_post"
            || source.GetProperty("author").GetString() != "thsottiaux")
            throw new InvalidDataException("Tibo의 X 리셋 예고를 확인할 수 없음");

        var sourceUrl = source.GetProperty("url").GetString() ?? "";
        var id = isWatch ? sourceUrl[(sourceUrl.LastIndexOf('/') + 1)..] : item.GetProperty("id").GetString()!;
        if (string.IsNullOrEmpty(id) || !id.All(char.IsAsciiDigit)
            || !Uri.TryCreate(sourceUrl, UriKind.Absolute, out var url)
            || url.Scheme != Uri.UriSchemeHttps || !url.IsDefaultPort || url.UserInfo.Length != 0
            || (url.Host != "x.com" && url.Host != "twitter.com")
            || url.AbsolutePath != $"/thsottiaux/status/{id}" || url.Query.Length != 0 || url.Fragment.Length != 0)
            throw new InvalidDataException("Tibo 공지 원문 링크를 확인할 수 없음");

        var text = item.GetProperty("text").GetString();
        var announcedAt = item.GetProperty(isWatch ? "observed_at" : "announced_at").GetDateTimeOffset();
        if (string.IsNullOrWhiteSpace(text) || announcedAt > now.AddMinutes(5))
            throw new InvalidDataException("Tibo 공지 내용 또는 게시 시각을 확인할 수 없음");
        var reference = item.GetProperty(isWatch ? "expires_at" : "scheduled_for");
        DateTimeOffset? referenceAt = reference.ValueKind == JsonValueKind.Null ? null : reference.GetDateTimeOffset();
        int? chancePercent = null;
        if (isWatch)
        {
            var chance = item.GetProperty("reset_chance_percent");
            chancePercent = chance.ValueKind == JsonValueKind.Null ? null : chance.GetInt32();
            if (referenceAt is null || referenceAt <= announcedAt || chancePercent is < 0 or > 100)
                throw new InvalidDataException("리셋 관찰 기한 또는 추정 확률을 확인할 수 없음");
        }
        // 참고 기한 경과는 리셋 완료를 뜻하지 않으며, 지난 예고·관찰은 새로 알리지 않는다.
        return new(generatedAt, new(id, text, url, announcedAt, referenceAt, isWatch, chancePercent), referenceAt <= now);
    }
}
