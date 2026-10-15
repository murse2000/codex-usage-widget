using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class UpdateVerification
{
    private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance;
    private static readonly Assembly App = Assembly.Load("CodexUsageWidget");
    private static readonly Type Updater = App.GetType("CodexUsageWidget.AppUpdate")!;
    private static int checks;

    public static void Run()
    {
        var root = Path.GetFullPath("obj/update-verification/한글 경로");
        Directory.CreateDirectory(root);
        var fixture = Path.GetFullPath("tests/fixtures/bin/Release/net8.0");
        var source = Path.Combine(fixture, "gh.exe");
        Check(File.Exists(source), "업데이트 검증용 실행 파일 준비");
        var zip = Path.Combine(root, "payload.zip");
        CreateZip(zip, source, "CodexUsageWidget.exe");
        var hash = Hash(zip);
        var release = Parse(Json(hash), new(1, 0, 0))!;
        Check(release is not null, "새 버전 발견");
        Check(Parse(Json(hash), new(1, 1, 0, 0)) is null, "같은 버전 반복 업데이트 방지");
        Check(Parse(Json(hash), new(2, 0, 0)) is null, "이전 버전으로 다운그레이드 방지");
        Check(Parse(Json(hash).Replace("\"draft\":false", "\"draft\":true"), new(1, 0, 0)) is null, "초안 제외");
        Check(Parse(Json(hash).Replace("\"prerelease\":false", "\"prerelease\":true"), new(1, 0, 0)) is null, "시험판 제외");
        Reject(() => Parse(Json(hash, tag: "v2.0.0;whoami"), new(1, 0, 0)), "잘못된 태그 거부");
        Reject(() => Parse(Json(""), new(1, 0, 0)), "해시 없는 릴리스 거부");
        Reject(() => Parse(Json(hash).Replace("CodexUsageWidget-win-x64.zip", "other.zip"), new(1, 0, 0)), "정해진 파일 없는 릴리스 거부");
        var extract = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(extract);
        Invoke("Extract", zip, extract, release!);
        Check(Hash(Path.Combine(extract, "CodexUsageWidget.exe")) == Hash(source), "정상 ZIP 해시·실행 파일 버전 검증");
        Reject(() => Invoke("Extract", zip, extract, Parse(Json(new string('0', 64)), new(1, 0, 0))!), "다운로드 손상 거부");
        var mismatch = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(mismatch);
        Reject(() => Invoke("Extract", zip, mismatch, Parse(Json(hash, "v1.2.0"), new(1, 0, 0))!), "릴리스와 실행 파일 버전 불일치 거부");
        var malicious = Path.Combine(root, "traversal.zip");
        CreateZip(malicious, source, "../CodexUsageWidget.exe");
        Reject(() => Invoke("Extract", malicious, extract, Parse(Json(Hash(malicious)), new(1, 0, 0))!), "ZIP 경로 이탈 거부");
        VerifyConsent(root, fixture);
        VerifyInstaller(root, fixture, false);
        VerifyInstaller(root, fixture, true);
        Console.WriteLine($"업데이트 검증 {checks}개 통과");
    }

    public static void InstallWorker(string directory, string target)
    {
        using var locked = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
        var task = (Task)Invoke("StartInstallerAsync", directory, target)!;
        WaitUntil(() => task.IsCompleted, 35000);
        task.GetAwaiter().GetResult();
    }

    public static void RunLive(string metadata)
    {
        var release = Parse(File.ReadAllText(metadata), new(1, 0, 0))!;
        var download = (Task<string>)Invoke("DownloadAsync", release)!;
        WaitUntil(() => download.IsCompleted, 60000);
        var directory = download.GetAwaiter().GetResult();
        try
        {
            Check(Hash(Path.Combine(directory, "CodexUsageWidget.exe")) == Hash("CodexUsageWidget.exe"), "실제 비공개 릴리스 다운로드·검증 후 로컬 EXE와 일치");
            var latest = (Task)Invoke("FindAsync")!;
            WaitUntil(() => latest.IsCompleted, 35000);
            latest.GetAwaiter().GetResult();
            Check(latest.GetType().GetProperty("Result")!.GetValue(latest) is null, "현재 릴리스 버전에서 재업데이트 없음");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void VerifyInstaller(string root, string fixture, bool damaged)
    {
        var installed = Path.Combine(root, "설치 시험 " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(installed);
        foreach (var file in Directory.GetFiles(fixture)) File.Copy(file, Path.Combine(installed, Path.GetFileName(file)));
        var target = Path.Combine(installed, "CodexUsageWidget.exe");
        File.Copy(Path.Combine(fixture, "gh.exe"), target);
        var original = Hash(target);
        var stage = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexUsageWidget", "updates", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        var payload = Path.Combine(stage, "CodexUsageWidget.exe");
        if (damaged) File.WriteAllText(payload, "not an executable");
        else
        {
            File.Copy(target, payload);
            using var stream = new FileStream(payload, FileMode.Append);
            stream.WriteByte(0);
        }
        var wanted = Hash(payload);
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var arg in new[] { "--update-worker", stage, target }) start.ArgumentList.Add(arg);
        using var worker = Process.Start(start)!;
        var errors = worker.StandardError.ReadToEndAsync();
        if (!worker.WaitForExit(40000)) { worker.Kill(true); worker.WaitForExit(); }
        Check(worker.ExitCode == 0, "종료 대기 도우미 준비 · " + damaged + (worker.ExitCode == 0 ? "" : " · " + errors.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult()));
        WaitUntil(() => File.Exists(Path.Combine(installed, "restarted")), 15000);
        Check(File.Exists(Path.Combine(installed, "restarted")), damaged ? "재실행 실패 시 기존 버전 복구·재실행" : "잠긴 파일의 종료 후 교체·재실행");
        Check(Hash(target) == (damaged ? original : wanted), damaged ? "복구 파일 원본 해시 일치" : "설치 파일 새 해시 일치");
        WaitUntil(() =>
        {
            if (damaged) foreach (var process in Process.GetProcessesByName("powershell"))
            {
                using (process)
                {
                    if (process.MainWindowTitle == "Codex 업데이트 오류") process.CloseMainWindow();
                }
            }
            return !Directory.Exists(stage);
        }, 10000);
        Check(!Directory.Exists(stage), "설치 임시 파일 정리 · " + damaged);
    }

    private static void VerifyConsent(string root, string fixture)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        var formType = App.GetType("CodexUsageWidget.UsageForm")!;
        var settingsPath = (string)formType.GetField("SettingsPath", Flags)!.GetValue(null)!;
        var backup = File.Exists(settingsPath) ? File.ReadAllBytes(settingsPath) : null;
        var fake = Path.Combine(root, "GitHub CLI " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fake);
        foreach (var file in Directory.GetFiles(fixture)) File.Copy(file, Path.Combine(fake, Path.GetFileName(file)));
        File.WriteAllText(Path.Combine(fake, "release.json"), Json(new string('0', 64), "v9.0.0"));
        File.WriteAllText(Path.Combine(fake, "payload.zip"), "damaged");
        Environment.SetEnvironmentVariable("PATH", fake + Path.PathSeparator + path);
        using var form = (Form)Activator.CreateInstance(formType, true)!;
        var tray = (NotifyIcon)formType.GetField("tray", Flags)!.GetValue(form)!;
        tray.Visible = false;
        var settings = formType.GetField("settings", Flags)!.GetValue(form)!;
        settings.GetType().GetProperty("DeclinedUpdateVersion")!.SetValue(settings, null);
        foreach (var name in new[] { "checkingUpdate", "checkingTibo", "refreshing" }) formType.GetField(name, Flags)!.SetValue(form, true);
        form.Show();
        Application.DoEvents();
        formType.GetField("checkingUpdate", Flags)!.SetValue(form, false);
        var decisions = new Queue<int>(new[] { 7, 6, 1 });
        var clicked = new HashSet<IntPtr>();
        using var timer = new System.Windows.Forms.Timer { Interval = 100 };
        timer.Tick += (_, _) => EnumThreadWindows(GetCurrentThreadId(), (window, _) =>
        {
            var title = new StringBuilder(128);
            GetWindowText(window, title, title.Capacity);
            if (title.ToString() != "Codex 업데이트" || decisions.Count == 0 || clicked.Contains(window)) return true;
            var button = GetDlgItem(window, decisions.Peek());
            if (button == IntPtr.Zero && decisions.Peek() == 1) button = GetDlgItem(window, 2);
            if (button != IntPtr.Zero) { clicked.Add(window); decisions.Dequeue(); PostMessage(button, 0x00F5, IntPtr.Zero, IntPtr.Zero); }
            return true;
        }, IntPtr.Zero);
        timer.Start();
        try
        {
            CallCheck(false);
            Check(decisions.Count == 2 && !Calls().Contains("release download"), "거절 시 다운로드·설치 없음");
            CallCheck(false);
            Check(decisions.Count == 2, "거절한 버전 자동 재질문 방지");
            CallCheck(true);
            Check(Calls().Contains("release download v9.0.0"), "수동 확인 재질문·승낙 후에만 다운로드");
            Check(decisions.Count == 0, "손상된 다운로드의 오류 안내");
            Check(!Calls().Contains("auth token"), "토큰을 조회·추출하지 않음");
        }
        finally
        {
            timer.Stop();
            form.Close();
            Environment.SetEnvironmentVariable("PATH", path);
            if (backup is not null) File.WriteAllBytes(settingsPath, backup);
            else if (File.Exists(settingsPath)) File.Delete(settingsPath);
        }
        string Calls() => File.ReadAllText(Path.Combine(fake, "calls"));
        void CallCheck(bool manual)
        {
            var task = (Task)formType.GetMethod("CheckUpdateAsync", Flags)!.Invoke(form, new object[] { manual })!;
            WaitUntil(() => task.IsCompleted, 15000);
            task.GetAwaiter().GetResult();
        }
    }

    private static object? Parse(string json, Version installed) => Invoke("Parse", json, installed);
    private static object? Invoke(string name, params object[] args) => Updater.GetMethod(name, Flags)!.Invoke(null, args);
    private static string Json(string hash, string tag = "v1.1.0") => JsonSerializer.Serialize(new
    { tag_name = tag, draft = false, prerelease = false, assets = new[] { new { name = "CodexUsageWidget-win-x64.zip", digest = "sha256:" + hash } } });
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static void CreateZip(string path, string source, string entry)
    {
        if (File.Exists(path)) File.Delete(path);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        zip.CreateEntryFromFile(source, entry);
    }
    private static void Reject(Action action, string message)
    {
        try { action(); }
        catch (TargetInvocationException error) when (error.InnerException is InvalidDataException or IOException) { Check(true, message); return; }
        throw new Exception(message);
    }
    private static void WaitUntil(Func<bool> condition, int milliseconds)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (!condition() && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(20); }
        if (!condition()) throw new TimeoutException("업데이트 검증 시간 초과");
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        checks++;
        Console.WriteLine("통과: " + message);
    }
    private delegate bool WindowCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint thread, WindowCallback callback, IntPtr parameter);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern IntPtr GetDlgItem(IntPtr dialog, int id);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
