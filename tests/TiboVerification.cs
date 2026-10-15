using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

internal static class TiboVerification
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic;
    private static readonly Assembly App = Assembly.Load("CodexUsageWidget");
    private static readonly Type Feed = App.GetType("CodexUsageWidget.TiboResetFeed")!;
    private static readonly Type FormType = App.GetType("CodexUsageWidget.UsageForm")!;
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-15T06:00:00Z");
    private static int checks;

    public static void Run(bool live)
    {
        var snapshot = Parse(Fixture());
        var notice = Property(snapshot, "Notice")!;
        Check((string)Property(notice, "Id")! == "2110595554036400151", "리셋 예고 추출");
        Check(!(bool)Property(snapshot, "ReferenceTimePassed")!, "미래 참고 기한");
        Check(Property(Parse(Fixture(schedule: false)), "Notice") is null, "완료 기록과 예측은 알림 대상에서 제외");
        Check(!(bool)Property(Parse(Fixture(reference: null)), "ReferenceTimePassed")!, "시각이 없는 예고 허용");
        Check((bool)Property(Parse(Fixture(reference: Now.AddMinutes(-1).ToString("O"))), "ReferenceTimePassed")!, "지난 기한은 완료로 바꾸지 않고 구분");
        Reject(Fixture(generated: Now.AddMinutes(-16).ToString("O")), "오래된 자료 거부");
        Reject(Fixture(generated: Now.AddMinutes(6).ToString("O")), "미래 자료 시각 거부");
        Reject(Fixture(author: "someone_else"), "다른 작성자 거부");
        Reject(Fixture(url: "https://example.com/thsottiaux/status/2110595554036400151"), "외부 원문 링크 거부");
        Reject(Fixture(url: "https://x.com/thsottiaux/status/1"), "게시물 ID와 원문 불일치 거부");
        Reject(Fixture(url: "javascript:alert(1)"), "실행 가능한 링크 거부");
        Reject(Fixture().Replace("\"api_version\":\"v1\"", "\"api_version\":\"v2\""), "알 수 없는 API 버전 거부");
        Check(Key(notice) == Key(Property(Parse(Fixture()), "Notice")!), "같은 예고의 중복 키 유지");
        Check(Key(notice) != Key(Property(Parse(Fixture(reference: Now.AddHours(3).ToString("O"))), "Notice")!), "같은 글의 기한 변경 구분");
        Check(Key(notice) == Key(Property(Parse(Fixture(reference: Now.AddHours(2).ToOffset(TimeSpan.FromHours(9)).ToString("O"))), "Notice")!), "동일 시각의 시간대 표현 변경은 중복 처리");
        Check(((string)Property(notice, "NotificationText")!).Contains("codex-resets.com (비공식)"), "알림에 출처와 비공식 표시");
        Check(((string)Property(notice, "NotificationText")!).Contains("https://x.com/thsottiaux/status/"), "알림 원문 링크 포함");

        using (var client = new HttpClient(new TestHandler(_ => new(HttpStatusCode.TooManyRequests)
            { Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(10)) } })))
        {
            var result = Read(client);
            Check(Property(result, "Snapshot") is null && (DateTimeOffset)Property(result, "RetryAt")! >= DateTimeOffset.UtcNow.AddMinutes(9), "요청 제한의 Retry-After 준수");
        }
        using (var client = new HttpClient(new TestHandler(request =>
        {
            Check(request.RequestUri!.AbsoluteUri == "https://codex-resets.com/api/v1/status", "문서화된 공개 API만 조회");
            Check(request.Headers.UserAgent.ToString().Contains("CodexUsageWidget"), "요청에 앱 식별자 포함");
            return new(HttpStatusCode.OK) { Content = new StringContent(Fixture(generated: DateTimeOffset.UtcNow.ToString("O"))) };
        })))
            Check(Property(Read(client), "Snapshot") is not null, "HTTP 응답 파싱");
        VerifyState(snapshot);
        if (live)
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var result = Read(client);
            Check(Property(result, "Snapshot") is not null, "실제 공개 API 조회");
        }
        Console.WriteLine($"Tibo 예고 검증 {checks}개 통과");
    }

    private static void VerifyState(object snapshot)
    {
        var path = (string)FormType.GetField("SettingsPath", Private)!.GetValue(null)!;
        var backup = File.Exists(path) ? File.ReadAllBytes(path) : null;
        using var form = (Form)Activator.CreateInstance(FormType, true)!;
        try
        {
            ((NotifyIcon)FormType.GetField("tray", Private)!.GetValue(form)!).Visible = false;
            var settings = FormType.GetField("settings", Private)!.GetValue(form)!;
            var keysProperty = settings.GetType().GetProperty("TiboAlertKeys")!;
            var keys = (ISet<string>)keysProperty.GetValue(settings)!;
            var enabledProperty = settings.GetType().GetProperty("TiboAlertsEnabled")!;
            var apply = FormType.GetMethod("ApplyTiboSnapshot", Private)!;
            enabledProperty.SetValue(settings, true);
            keys.Clear();
            apply.Invoke(form, new[] { snapshot });
            var key = Key(Property(snapshot, "Notice")!);
            Check(keys.Contains(key), "새 예고 알림 이력 저장");
            apply.Invoke(form, new[] { snapshot });
            Check(keys.Count == 1, "같은 예고 반복 처리 방지");
            apply.Invoke(form, new[] { Parse(Fixture(reference: Now.AddHours(3).ToString("O"))) });
            apply.Invoke(form, new[] { snapshot });
            Check(keys.Count == 2, "다른 예고 이후 이전 예고가 다시 와도 중복 방지");
            var loaded = FormType.GetMethod("LoadSettings", Private)!.Invoke(null, null)!;
            Check(((ISet<string>)keysProperty.GetValue(loaded)!).SetEquals(keys), "재실행 후에도 알림 이력 복원");
            enabledProperty.SetValue(settings, false);
            apply.Invoke(form, new[] { Parse(Fixture(reference: Now.AddHours(4).ToString("O"))) });
            Check(keys.Count == 2, "알림 끄기 적용");
            enabledProperty.SetValue(settings, true);
            apply.Invoke(form, new[] { Parse(Fixture(reference: Now.AddMinutes(-1).ToString("O"))) });
            Check(keys.Count == 2, "지난 참고 기한의 예고는 알리지 않음");
            var original = (ToolStripMenuItem)FormType.GetField("tiboOriginal", Private)!.GetValue(form)!;
            Check(original.Enabled, "원문 메뉴 활성화");
            apply.Invoke(form, new[] { Parse(Fixture(schedule: false)) });
            Check(!original.Enabled && FormType.GetField("tiboNotice", Private)!.GetValue(form) is null, "예고 제거 시 원문 메뉴 정리");
        }
        finally
        {
            form.Close();
            if (backup is null) File.Delete(path); else File.WriteAllBytes(path, backup);
        }
    }

    private static string Fixture(bool schedule = true, string? generated = "2026-10-15T06:00:00Z", string? reference = "2026-10-15T08:00:00Z",
        string author = "thsottiaux", string url = "https://x.com/thsottiaux/status/2110595554036400151")
    {
        var notice = new
        {
            id = "2110595554036400151", status = "scheduled", reset_type = "regular", announced_at = "2026-10-15T04:56:07Z",
            scheduled_for = reference, text = "Landing within the next 2 hours",
            source = new { type = "x_post", author, url }
        };
        return JsonSerializer.Serialize(new
        {
            meta = new { api_version = "v1", generated_at = generated },
            data = new { scheduled_reset = schedule ? notice : null, latest_reset = new { text = "Reset completed" }, active_watch = new { reset_chance_percent = 99 } }
        });
    }

    private static object Parse(string json) => Feed.GetMethod("Parse")!.Invoke(null, new object[] { json, Now })!;
    private static object? Property(object target, string name) => target.GetType().GetProperty(name)!.GetValue(target);
    private static string Key(object notice) => (string)Property(notice, "AlertKey")!;
    private static object Read(HttpClient client)
    {
        var task = (Task)Feed.GetMethod("ReadAsync")!.Invoke(null, new object[] { client })!;
        task.GetAwaiter().GetResult();
        return task.GetType().GetProperty("Result")!.GetValue(task)!;
    }

    private static void Reject(string json, string name)
    {
        try { Parse(json); }
        catch (TargetInvocationException error) when (error.InnerException is InvalidDataException) { Check(true, name); return; }
        throw new InvalidOperationException(name);
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        checks++;
    }

    private sealed class TestHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(request));
    }
}
