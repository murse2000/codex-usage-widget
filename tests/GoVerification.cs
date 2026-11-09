using System.Collections;
using System.Net;
using System.Reflection;

internal static class GoVerification
{
    private const BindingFlags Static = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;
    private const BindingFlags Instance = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
    private static readonly Assembly App = Assembly.Load("CodexUsageWidget");
    private static readonly Type Usage = App.GetType("CodexUsageWidget.OpenCodeGoUsage")!;
    private static readonly Type FormType = App.GetType("CodexUsageWidget.UsageForm")!;
    private static int checks;

    private const string Fixture = """
        {"usage":{"rolling":{"status":"ok","percent":1,"resetsAt":"2026-11-09T14:16:03.000Z"},"weekly":{"status":"ok","percent":0,"resetsAt":"2026-11-16T00:00:00.000Z"},"monthly":{"status":"ok","percent":72,"resetsAt":"2026-11-20T07:51:25.000Z"}}}
        """;

    public static void Run(bool live)
    {
        var windows = Parse(Fixture);
        Check(windows.Count == 3, "세 구간 사용량 추출");
        Check(Key(windows[0]) == "rolling" && (int)Value(windows[0], "UsedPercent") == 1, "5시간 사용률");
        Check(Key(windows[1]) == "weekly" && (int)Value(windows[1], "UsedPercent") == 0, "주간 사용률");
        Check(Key(windows[2]) == "monthly" && (int)Value(windows[2], "UsedPercent") == 72, "월간 사용률");
        Check(Value(windows[2], "Label") as string == "월간 한도", "월간 라벨");
        Check((int)Value(windows[0], "WindowMinutes") == 300 && (int)Value(windows[1], "WindowMinutes") == 10080, "창 길이 구분");
        Check((long)Value(windows[2], "ResetsAt") == DateTimeOffset.Parse("2026-11-20T07:51:25Z").ToUnixTimeSeconds(), "리셋 시각 변환");

        Check(Parse("""{"usage":{"rolling":{"percent":1,"resetsAt":"2026-11-09T14:16:03Z"}}}""").Count == 1, "일부 구간만 있어도 표시");
        Check((int)Value(Parse("""{"usage":{"rolling":{"percent":140,"resetsAt":"2026-11-09T14:16:03Z"}}}""")[0], "UsedPercent") == 100,
            "범위를 벗어난 사용률은 0~100으로 제한");
        Check((int)Value(Parse("""{"usage":{"monthly":{"percent":-5,"resetsAt":"2026-11-20T07:51:25Z"}}}""")[0], "UsedPercent") == 0,
            "음수 사용률은 0으로 제한");

        Reject("", "빈 응답 거부");
        Reject("{}", "usage 없는 응답 거부");
        Reject("""{"usage":{}}""", "구간 없는 응답 거부");
        Reject("""{"usage":{"rolling":{"percent":"높음","resetsAt":"2026-11-09T14:16:03Z"}}}""", "숫자가 아닌 사용률 거부");
        Reject("""{"usage":{"rolling":{"percent":10}}}""", "리셋 시각 없는 응답 거부");
        Reject("""{"usage":{"rolling":{"percent":10,"resetsAt":"어제"}}}""", "잘못된 리셋 시각 거부");

        var authorized = new StubHandler(HttpStatusCode.OK, Fixture);
        Check(Read(new HttpClient(authorized)) is null, "정상 응답 조회");
        Check(authorized.Request?.Headers.Authorization?.Scheme == "Bearer", "API 키를 Bearer로 전송");
        Check(authorized.Request?.Headers.Authorization?.Parameter == "test-key", "입력한 API 키 전송");
        Check(authorized.Request?.Headers.TryGetValues("x-opencode-session", out _) == true, "세션 헤더 전송");
        Check(authorized.Request?.RequestUri?.AbsoluteUri == "https://opencode.ai/zen/go/v1/usage", "공개 사용량 엔드포인트 사용");

        Check(Read(new HttpClient(new StubHandler(HttpStatusCode.Unauthorized, ""))) is { } unauthorized
            && unauthorized.GetType().Name == "OpenCodeGoAuthException", "401 인증 실패 구분");
        Check(Read(new HttpClient(new StubHandler(HttpStatusCode.Forbidden, ""))) is { } forbidden
            && forbidden.GetType().Name == "OpenCodeGoAuthException", "403 인증 실패 구분");
        Check(Read(new HttpClient(new StubHandler(HttpStatusCode.TooManyRequests, ""))) is { } limited
            && limited.GetType().Name != "OpenCodeGoAuthException", "429 요청 제한은 인증 실패와 구분");
        Check(Read(new HttpClient(new StubHandler(HttpStatusCode.InternalServerError, ""))) is { } server
            && server.GetType().Name != "OpenCodeGoAuthException", "서버 오류는 인증 실패와 구분");
        Check(Read(new HttpClient(new StubHandler(HttpStatusCode.OK, "{}"))) is InvalidDataException, "형식이 다른 응답 거부");
        Check(Read(new HttpClient(new StubHandler(HttpStatusCode.OK, Fixture)), "") is { } missing
            && missing.GetType().Name == "OpenCodeGoNotConfiguredException", "빈 키는 미설정으로 구분");

        var secret = App.GetType("CodexUsageWidget.SecretStore")!;
        var protect = secret.GetMethod("Protect", Static)!;
        var unprotect = secret.GetMethod("Unprotect", Static)!;
        var encrypted = (string?)protect.Invoke(null, new object[] { "oc_sk_test_value" });
        Check(encrypted is not null && !encrypted.Contains("oc_sk_test_value", StringComparison.Ordinal), "API 키 암호화 저장");
        Check(unprotect.Invoke(null, new object?[] { encrypted }) as string == "oc_sk_test_value", "API 키 복호화");
        Check(unprotect.Invoke(null, new object?[] { "not-base64" }) is null, "손상된 값은 키 없음으로 처리");
        Check(unprotect.Invoke(null, new object?[] { null }) is null, "저장된 키가 없으면 null");

        if (live) VerifyLive();
        Console.WriteLine($"OpenCode Go 검증 {checks}개 통과");
    }

    private static void VerifyLive()
    {
        var key = Environment.GetEnvironmentVariable("OPENCODE_GO_KEY");
        if (string.IsNullOrWhiteSpace(key))
        {
            Console.WriteLine("OPENCODE_GO_KEY가 없어 실제 조회는 건너뜁니다.");
            return;
        }
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        Check(Read(client, key) is null, "실제 OpenCode Go 사용량 조회");

        var path = (string)FormType.GetField("SettingsPath", Instance | BindingFlags.Static)!.GetValue(null)!;
        var backup = File.Exists(path) ? File.ReadAllBytes(path) : null;
        using var form = (Form)Activator.CreateInstance(FormType, true)!;
        try
        {
            form.Show();
            Application.DoEvents();
            var save = (Task<string?>)FormType.GetMethod("SaveGoKeyAsync", Instance)!.Invoke(form, new object[] { key })!;
            Wait(() => save.IsCompleted);
            Check(save.Result is null, "실제 키 인증 및 저장");
            var windows = (IEnumerable)FormType.GetField("goWindows", Instance)!.GetValue(form)!;
            Check(windows.Cast<object>().Count() == 3, "실제 세 구간 조회");
            var section = (Label)FormType.GetField("goSection", Instance)!.GetValue(form)!;
            Check(section.Visible, "실제 키로 Go 목록 표시");
            Directory.CreateDirectory("obj/layout-verification");
            using (var bitmap = new Bitmap(form.Width, form.Height))
            {
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                bitmap.Save(Path.Combine("obj/layout-verification", "live-go.png"));
            }

            var json = File.ReadAllText(path);
            Check(!json.Contains(key, StringComparison.Ordinal), "설정 파일에 키 평문 없음");
            Check(json.Contains("OpenCodeGoKey", StringComparison.Ordinal), "설정 파일에 암호화된 키 저장");

            using (var restored = (Form)Activator.CreateInstance(FormType, true)!)
                Check(FormType.GetField("goApiKey", Instance)!.GetValue(restored) as string == key, "재실행 시 암호화된 키 복원");

            FormType.GetMethod("ClearGoKey", Instance)!.Invoke(form, null);
            Check(!section.Visible, "키를 지우면 Go 목록 숨김");
        }
        finally
        {
            form.Close();
            if (backup is null) File.Delete(path); else File.WriteAllBytes(path, backup);
        }
        Console.WriteLine("실제 조회 성공 · 키는 출력하지 않습니다.");
    }

    private static void Wait(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (!condition() && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(20); }
        Check(condition(), "실제 조회 준비");
    }

    private static List<object> Parse(string json)
        => ((IEnumerable)Usage.GetMethod("Parse", Static)!.Invoke(null, new object[] { json })!).Cast<object>().ToList();

    private static Exception? Read(HttpClient client, string key = "test-key")
    {
        var task = (Task)Usage.GetMethod("ReadAsync", Static)!.Invoke(null, new object[] { client, key })!;
        try
        {
            task.GetAwaiter().GetResult();
            return null;
        }
        catch (Exception error)
        {
            return error is TargetInvocationException { InnerException: { } inner } ? inner : error;
        }
    }

    private static object Value(object target, string name) => target.GetType().GetProperty(name, Instance)!.GetValue(target)!;
    private static string Key(object window) => (string)Value(window, "Key");

    private static void Reject(string json, string name)
    {
        try
        {
            Usage.GetMethod("Parse", Static)!.Invoke(null, new object[] { json });
        }
        catch (TargetInvocationException error) when (error.InnerException is InvalidDataException)
        {
            checks++;
            return;
        }
        throw new InvalidOperationException(name);
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        checks++;
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode status;
        private readonly string body;

        internal StubHandler(HttpStatusCode status, string body)
        {
            this.status = status;
            this.body = body;
        }

        internal HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Request = new HttpRequestMessage(request.Method, request.RequestUri);
            foreach (var header in request.Headers) Request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }
}
