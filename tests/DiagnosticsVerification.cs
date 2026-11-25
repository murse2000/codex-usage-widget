global using CodexUsageWidget;

internal static class DiagnosticsVerification
{
    private static int checks;

    public static void Run()
    {
        var directory = Path.GetFullPath(Path.Combine("obj", "diagnostics-verification"));
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        Directory.CreateDirectory(directory);
        try
        {
            VerifyLoadRangeAndBadLines(directory);
            VerifyRetention(directory);
            VerifyRedactionAndLimits(directory);
            VerifyProcessMetrics(directory);
            VerifyConcurrentWrites(directory);
            VerifyEmptyUi(directory);
            VerifyPopulatedUi(directory);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        Console.WriteLine($"성능 진단 검증 {checks}개 통과");
    }

    private static void VerifyLoadRangeAndBadLines(string root)
    {
        var now = new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);
        var directory = TestDirectory(root, "load");
        var path = Path.Combine(directory, "diagnostics-20261126.jsonl");
        var boundary = now.AddDays(-7);
        WriteRefresh(path, boundary, workingSet: 100, privateBytes: 120);
        WriteRefresh(path, now.AddDays(-7).AddSeconds(-1), workingSet: 200, privateBytes: 220);
        WriteRefresh(path, now.AddSeconds(1), workingSet: 300, privateBytes: 320);
        WriteRefresh(path, now.AddMinutes(-1), workingSet: -1, privateBytes: 400);
        File.AppendAllText(path, "{broken json\n");
        File.AppendAllText(path, new string('x', 70_000) + "\n");

        var store = new PerformanceDiagnostics(directory);
        var loaded = store.Load(TimeSpan.FromDays(7), now);

        Check(loaded.Count == 1, "7일 경계 기록만 포함하고 기간 밖·손상·과도한 길이 줄 제외");
        Check(loaded[0].WorkingSet == 100 && loaded[0].PrivateBytes == 120, "유효한 프로세스 수치를 그대로 로드");
        Check(store.Load(TimeSpan.Zero, now).Count == 0, "범위가 0이면 기록을 로드하지 않음");
    }

    private static void VerifyRetention(string root)
    {
        var directory = TestDirectory(root, "retention");
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var oldPath = Path.Combine(directory, $"diagnostics-{today.AddDays(-8):yyyyMMdd}.jsonl");
        var boundaryPath = Path.Combine(directory, $"diagnostics-{today.AddDays(-7):yyyyMMdd}.jsonl");
        WriteRefresh(oldPath, DateTimeOffset.UtcNow, workingSet: 100, privateBytes: 120);
        WriteRefresh(boundaryPath, DateTimeOffset.UtcNow, workingSet: 130, privateBytes: 150);

        var store = new PerformanceDiagnostics(directory);
        store.RecordEvent("retention", "cleanup check");

        Check(!File.Exists(oldPath), "7일을 초과한 날짜 파일 정리");
        Check(File.Exists(boundaryPath), "7일 경계 날짜 파일은 보관");
    }

    private static void VerifyRedactionAndLimits(string root)
    {
        var directory = TestDirectory(root, "redaction");
        var store = new PerformanceDiagnostics(directory);
        const string apiKey = "sk-proj-ABCDEFGHIJKLMNOPQRSTUVWXYZ123456";
        const string goKey = "oc_sk_ABCDEFGHIJKLMNOPQRSTUVWXYZ123456";
        const string password = "p@ssw0rd-Ciphertext-987654321";
        const string bearer = "Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.signature123456";
        store.RecordEvent(apiKey, $"api_key={apiKey}; go_key={goKey}; password={password}; Authorization: {bearer}");
        store.RecordEvent(new string('k', 100), new string('m', 700));

        var lines = File.ReadAllLines(Directory.GetFiles(directory, "diagnostics-*.jsonl").Single());
        Check(lines.Length == 2, "이벤트를 JSONL 한 줄로 저장");
        Check(!lines[0].Contains(apiKey, StringComparison.Ordinal) && !lines[0].Contains(goKey, StringComparison.Ordinal)
            && !lines[0].Contains(password, StringComparison.Ordinal) && !lines[0].Contains("eyJhbGciOiJIUzI1NiJ9", StringComparison.Ordinal),
            "API 키·암호문·Authorization 헤더 값 저장하지 않음");
        Check(lines[0].Contains("[redacted]", StringComparison.Ordinal), "키 형태가 포함된 메시지를 redaction");

        using (var document = System.Text.Json.JsonDocument.Parse(lines[1]))
        {
            var kind = document.RootElement.GetProperty("Kind").GetString()!;
            var message = document.RootElement.GetProperty("Message").GetString()!;
            Check(kind.Length == 64 && message.Length == 512, "kind·message를 각각 64·512자로 제한");
        }
    }

    private static void VerifyProcessMetrics(string root)
    {
        var directory = TestDirectory(root, "metrics");
        var store = new PerformanceDiagnostics(directory);
        var before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        store.RecordRefresh(TimeSpan.FromMilliseconds(42.7), 12, 4321, codexOk: true, goOk: false);
        var after = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var point = store.Load(TimeSpan.FromMinutes(1), DateTimeOffset.UtcNow).Single();

        Check(point.WorkingSet > 0 && point.PrivateBytes > 0, "WorkingSet·PrivateBytes 현재 프로세스 수치 기록");
        Check(point.ThreadCount >= 1 && point.HandleCount >= 1, "스레드·핸들 수가 유효한 양수 범위");
        Check(point.GcHeapBytes >= 0, "GC 힙 수치가 음수가 아님");
        Check(point.RefreshMilliseconds == 42 && point.UiaMilliseconds == 12, "갱신·UIA 경과 시간 기록");
        Check(point.CodexProcessId == 4321 && point.CodexOk && !point.GoOk, "app-server PID와 서비스 성공 상태 기록");
        Check(point.TimestampUtc >= before && point.TimestampUtc <= after, "UTC 타임스탬프 기록");
        Check(point.Kind is null && point.Message is null, "성공 갱신 레코드에는 이벤트 문자열을 저장하지 않음");
    }

    private static void VerifyConcurrentWrites(string root)
    {
        var directory = TestDirectory(root, "concurrent");
        var store = new PerformanceDiagnostics(directory);
        Parallel.For(0, 64, index => store.RecordEvent("parallel", index.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var loaded = store.Load(TimeSpan.FromMinutes(1), DateTimeOffset.UtcNow);
        Check(loaded.Count == 64 && loaded.Select(point => point.Message).Distinct().Count() == 64,
            "동시 이벤트 기록이 lock으로 유실되지 않음");
    }

    private static void VerifyEmptyUi(string root)
    {
        var store = new PerformanceDiagnostics(TestDirectory(root, "empty-ui"));
        using var form = new DiagnosticsForm(store);
        form.Show();
        Application.DoEvents();

        Check(form is GlassForm, "기존 글라스 창 상속");
        Check(form.FormBorderStyle == FormBorderStyle.None && !form.ShowInTaskbar, "테두리 없는 진단 창과 작업표시줄 버튼 숨김");
        var chart = form.Controls.OfType<Panel>().SelectMany(panel => panel.Controls.Cast<Control>()).OfType<DiagnosticsChart>().SingleOrDefault();
        Check(chart is { Visible: true, Width: > 0, Height: > 0 }, "기록이 없어도 24시간 추세 영역 표시");
        Check(form.Controls.OfType<Panel>().SelectMany(panel => panel.Controls.OfType<Button>()).Any(button => button.Text == "×"),
            "공통 헤더의 닫기 버튼 사용");
        Check(FindControl<Button>(form, "refreshButton") is { Text: "새로고침" }, "공통 스타일의 새로고침 버튼 사용");
        Check(FindControl<Label>(form, "sampleSummary")!.Text.Contains("기록이 없습니다", StringComparison.Ordinal),
            "기록이 없는 경우 정상 안내 문구 표시");
        Check(FindControl<Label>(form, "workingSetValue")!.Text == "—", "기록이 없는 현재 수치는 빈 값으로 표시");
        Directory.CreateDirectory("obj/layout-verification");
        using (var bitmap = new Bitmap(form.Width, form.Height))
        {
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
            Check(bitmap.Width > 0 && bitmap.Height > 0, "빈 기록 진단 창 렌더링");
            bitmap.Save("obj/layout-verification/diagnostics.png");
        }
        form.Close();
    }

    private static void VerifyPopulatedUi(string root)
    {
        var directory = TestDirectory(root, "populated-ui");
        var now = DateTimeOffset.UtcNow;
        WriteRefresh(Path.Combine(directory, $"diagnostics-{DateOnly.FromDateTime(now.UtcDateTime):yyyyMMdd}.jsonl"),
            now.AddMinutes(-2), workingSet: 128L * 1024 * 1024, privateBytes: 192L * 1024 * 1024,
            threadCount: 17, handleCount: 321, gcHeapBytes: 48L * 1024 * 1024,
            refreshMilliseconds: 250, uiaMilliseconds: 9876, codexProcessId: 4321, codexOk: true, goOk: true);

        using var form = new DiagnosticsForm(new PerformanceDiagnostics(directory));
        form.Show();
        Application.DoEvents();

        Check(FindControl<Label>(form, "workingSetValue")!.Text == "128.0 MB", "현재 WorkingSet 표시");
        Check(FindControl<Label>(form, "privateBytesValue")!.Text == "192.0 MB", "현재 Private Bytes 표시");
        Check(FindControl<Label>(form, "gcHeapValue")!.Text == "48.0 MB", "현재 GC 힙 표시");
        Check(FindControl<Label>(form, "threadCountValue")!.Text == "17", "현재 스레드 수 표시");
        Check(FindControl<Label>(form, "handleCountValue")!.Text == "321", "현재 핸들 수 표시");
        Check(FindControl<Label>(form, "uiaValue")!.Text.Contains("9876 ms", StringComparison.Ordinal), "마지막 UIA 시간 표시");
        Check(FindControl<Label>(form, "processIdValue")!.Text == "4321", "app-server PID 표시");
        Check(!FindControl<Label>(form, "sampleSummary")!.Text.Contains("기록이 없습니다", StringComparison.Ordinal),
            "기록이 있는 경우 샘플 요약 표시");
        FindControl<Button>(form, "refreshButton")!.PerformClick();
        Application.DoEvents();
        Check(FindControl<Label>(form, "workingSetValue")!.Text == "128.0 MB", "새로고침 후 현재 수치 유지");
        form.Close();
    }

    private static string TestDirectory(string root, string name)
    {
        var path = Path.Combine(root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void WriteRefresh(string path, DateTimeOffset timestamp, long workingSet, long privateBytes,
        int threadCount = 4, int handleCount = 40, long gcHeapBytes = 1024,
        long refreshMilliseconds = 10, long? uiaMilliseconds = null, int? codexProcessId = null,
        bool codexOk = false, bool goOk = false)
    {
        var line = System.Text.Json.JsonSerializer.Serialize(new
        {
            WorkingSet = workingSet,
            PrivateBytes = privateBytes,
            ThreadCount = threadCount,
            HandleCount = handleCount,
            GcHeapBytes = gcHeapBytes,
            RefreshMilliseconds = refreshMilliseconds,
            UiaMilliseconds = uiaMilliseconds,
            CodexProcessId = codexProcessId,
            CodexOk = codexOk,
            GoOk = goOk,
            TimestampUtc = timestamp.ToUnixTimeSeconds()
        });
        File.AppendAllText(path, line + Environment.NewLine);
    }

    private static T? FindControl<T>(Control root, string name) where T : Control
    {
        foreach (Control control in root.Controls)
        {
            if (control.Name == name && control is T match) return match;
            var nested = FindControl<T>(control, name);
            if (nested is not null) return nested;
        }
        return null;
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        checks++;
        Console.WriteLine("통과: " + name);
    }
}
