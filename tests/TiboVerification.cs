using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class TiboVerification
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic;
    private static readonly Assembly App = Assembly.Load("CodexUsageWidget");
    private static readonly Type Feed = App.GetType("CodexUsageWidget.TiboResetFeed")!;
    private static readonly Type FormType = App.GetType("CodexUsageWidget.UsageForm")!;
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-15T06:00:00Z");
    private static int checks;

    public static void VerifyDelivery()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var snapshot = Property(Read(client), "Snapshot")!;
        Check(snapshot is not null && Property(snapshot, "Notice") is not null && !(bool)Property(snapshot, "ReferenceTimePassed")!, "현재 유효한 실제 예고·관찰 자료 준비");
        var path = (string)FormType.GetField("SettingsPath", Private)!.GetValue(null)!;
        var backup = File.Exists(path) ? File.ReadAllBytes(path) : null;
        using var form = (Form)Activator.CreateInstance(FormType, true)!;
        var tray = (NotifyIcon)FormType.GetField("tray", Private)!.GetValue(form)!;
        var settings = FormType.GetField("settings", Private)!.GetValue(form)!;
        var keys = (ISet<string>)settings.GetType().GetProperty("TiboAlertKeys")!.GetValue(settings)!;
        var notice = Property(snapshot!, "Notice")!;
        var shown = false;
        tray.BalloonTipShown += (_, _) => shown = true;
        try
        {
            Check((bool)settings.GetType().GetProperty("TiboAlertsEnabled")!.GetValue(settings)! && !keys.Contains(Key(notice)), "알림 설정이 켜져 있고 아직 알리지 않은 현재 자료");
            FormType.GetMethod("ApplyTiboSnapshot", Private)!.Invoke(form, new[] { snapshot });
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!shown && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(20); }
            Check(shown, "Windows에서 현재 Tibo 알림의 BalloonTipShown 표시 이벤트 수신");
            Check(keys.Contains(Key(notice)), "실제 전달한 현재 자료의 알림 이력 저장");
        }
        finally
        {
            form.Close();
            tray.Dispose();
            if (!shown) { if (backup is null) File.Delete(path); else File.WriteAllBytes(path, backup); }
        }
        Console.WriteLine($"실제 알림 전달 검증 {checks}개 통과");
    }

    public static void Run(bool live)
    {
        var watchSnapshot = Parse(WatchFixture());
        var watchNotice = Property(watchSnapshot, "Notice")!;
        Check(watchNotice is not null, "확정 예고가 없어도 Tibo의 유효한 리셋 관찰을 알림 대상으로 추출");
        Check((bool)Property(watchNotice!, "IsWatch")!, "가능성 관찰을 확정 예고와 구분");
        Check((string)Property(watchNotice!, "Id")! == "2110951585983631505", "관찰 원문에서 게시물 ID 추출");
        Check(((string)Property(watchNotice!, "NotificationTitle")!).Contains("가능성 관찰"), "관찰 알림 제목 구분");
        var watchText = (string)Property(watchNotice!, "NotificationText")!;
        Check(watchText.Contains("확정 예고 아님") && watchText.Contains("서비스 추정 40%") && watchText.Contains("관찰 기한:"), "관찰 알림에 불확실성과 서비스 추정치·기한 표시");
        Check(watchText.Length <= 255 && watchText.Contains("codex-resets.com (비공식)"), "관찰 알림 크기와 출처 표시 유지");
        Check((bool)Property(Property(Parse(WatchFixture(level: "strong")), "Notice")!, "IsWatch")!, "strong 단계의 관찰 허용");
        Check(!((string)Property(Property(Parse(WatchFixture(chance: null)), "Notice")!, "NotificationText")!).Contains("서비스 추정"), "추정치가 없는 관찰에 임의 확률을 표시하지 않음");
        Check((bool)Property(Parse(WatchFixture(expires: Now.AddMinutes(-1).ToString("O"))), "ReferenceTimePassed")!, "관찰 기한 경과 구분");
        Reject(WatchFixture(level: "unknown"), "알 수 없는 관찰 단계 거부");
        Reject(WatchFixture(chance: 101), "범위를 벗어난 추정치 거부");
        Reject(WatchFixture(expires: null), "기한 없는 관찰 거부");
        Reject(WatchFixture(author: "someone_else"), "다른 작성자의 관찰 거부");
        Reject(WatchFixture(url: "https://example.com/thsottiaux/status/2110951585983631505"), "관찰의 외부 원문 링크 거부");
        Check(Key(watchNotice!) == Key(Property(Parse(WatchFixture(chance: 60)), "Notice")!), "추정 확률만 갱신되면 반복 알림 방지");
        var both = JsonNode.Parse(Fixture())!;
        both["data"]!["active_watch"] = JsonNode.Parse(WatchFixture())!["data"]!["active_watch"]!.DeepClone();
        Check(!(bool)Property(Property(Parse(both.ToJsonString()), "Notice")!, "IsWatch")!, "확정 예고와 관찰이 함께 있으면 확정 예고 우선");
        var snapshot = Parse(Fixture());
        var notice = Property(snapshot, "Notice")!;
        Check((string)Property(notice, "Id")! == "2110595554036400151", "리셋 예고 추출");
        Check(!(bool)Property(snapshot, "ReferenceTimePassed")!, "미래 참고 기한");
        Check(Property(Parse(Fixture(schedule: false)), "Notice") is null, "완료 기록만 있을 때는 알림 대상에서 제외");
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

    private static string WatchFixture(string level = "elevated", int? chance = 40, string? expires = "2026-10-15T08:00:00Z",
        string author = "thsottiaux", string url = "https://x.com/thsottiaux/status/2110951585983631505")
        => JsonSerializer.Serialize(new
        {
            meta = new { api_version = "v1", generated_at = Now.ToString("O") },
            data = new
            {
                scheduled_reset = (object?)null,
                active_watch = new
                {
                    level, reset_chance_percent = chance, forecast_window = "within 24h", observed_at = Now.AddHours(-1).ToString("O"),
                    expires_at = expires, text = "Let’s break the cycle.", source = new { type = "x_post", author, url }
                }
            }
        });

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
            var alarm = form.OwnedForms.Single();
            Check(alarm.Visible && alarm.TopMost && alarm.ShowInTaskbar && !alarm.ControlBox,
                "리셋 알람은 확인 가능한 별도 창으로 표시");
            alarm.Close();
            Check(!alarm.IsDisposed && alarm.Visible, "확인 없이 알람 닫기 차단");
            var layout = alarm.Controls.OfType<TableLayoutPanel>().Single();
            Check(layout.Controls.OfType<TextBox>().Single().Text.Contains((string)Property(Property(snapshot, "Notice")!, "Text")!),
                "알람에 예고 본문 전체 표시");
            ((Button)alarm.AcceptButton!).PerformClick();
            Check(alarm.IsDisposed && form.OwnedForms.Length == 0, "확인 버튼으로 알람 닫기");
            apply.Invoke(form, new[] { snapshot });
            Check(form.OwnedForms.Length == 0, "확인한 알람은 다시 열리지 않음");
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
            var watch = Parse(WatchFixture());
            apply.Invoke(form, new[] { watch });
            var watchKey = Key(Property(watch, "Notice")!);
            Check(keys.Count == 3 && keys.Contains(watchKey), "새 관찰 알림 이력 저장");
            var status = (ToolStripMenuItem)FormType.GetField("tiboStatus", Private)!.GetValue(form)!;
            Check(status.Text!.Contains("확정 아님") && original.Enabled, "트레이에서도 가능성 관찰로 표시하고 원문 활성화");
            apply.Invoke(form, new[] { watch });
            apply.Invoke(form, new[] { Parse(WatchFixture(chance: 60)) });
            Check(keys.Count == 3, "같은 관찰과 확률 갱신의 중복 알림 방지");
            var promoted = Parse(Fixture().Replace("2110595554036400151", "2110951585983631505").Replace("Landing within the next 2 hours", "Let’s break the cycle."));
            apply.Invoke(form, new[] { promoted });
            Check(keys.Count == 4 && !Key(Property(promoted, "Notice")!).Equals(watchKey), "관찰이 같은 글의 확정 예고로 바뀌면 새 알림");
            enabledProperty.SetValue(settings, false);
            apply.Invoke(form, new[] { Parse(WatchFixture(expires: Now.AddHours(3).ToString("O"))) });
            Check(keys.Count == 4, "관찰에도 기존 알림 끄기 설정 적용");
            enabledProperty.SetValue(settings, true);
            apply.Invoke(form, new[] { Parse(WatchFixture(expires: Now.AddMinutes(-1).ToString("O"))) });
            Check(keys.Count == 4, "기한이 지난 관찰은 알리지 않음");
            loaded = FormType.GetMethod("LoadSettings", Private)!.Invoke(null, null)!;
            Check(((ISet<string>)keysProperty.GetValue(loaded)!).SetEquals(keys), "관찰과 확정 예고 알림 이력 재실행 후 복원");
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
            data = new { scheduled_reset = schedule ? notice : null, latest_reset = new { text = "Reset completed" }, active_watch = (object?)null }
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
