using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexUsageWidget;

internal sealed record UpdateRelease(string Tag, Version Version, string Digest);

internal static class AppUpdate
{
    internal const string Repository = "murse2000/codex-usage-widget";
    internal const string AssetName = "CodexUsageWidget-win-x64.zip";
    internal static Version CurrentVersion => typeof(AppUpdate).Assembly.GetName().Version!;
    private static HttpClient client = CreateClient();

    private static HttpClient CreateClient()
    {
        var result = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        result.DefaultRequestHeaders.UserAgent.ParseAdd($"CodexUsageWidget/{CurrentVersion.ToString(3)}");
        return result;
    }

    internal static async Task<UpdateRelease?> FindAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var json = await client.GetStringAsync($"https://api.github.com/repos/{Repository}/releases/latest", timeout.Token);
        return Parse(json, CurrentVersion);
    }

    internal static UpdateRelease? Parse(string json, Version installed)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Regex.IsMatch(tag, @"^v[0-9]+\.[0-9]+\.[0-9]+\z") || !Version.TryParse(tag[1..], out var version))
            throw new InvalidDataException("릴리스 버전 형식이 올바르지 않습니다.");
        if (version <= new Version(installed.Major, installed.Minor, Math.Max(0, installed.Build))) return null;
        var asset = root.GetProperty("assets").EnumerateArray().SingleOrDefault(a => a.GetProperty("name").GetString() == AssetName);
        if (asset.ValueKind == JsonValueKind.Undefined || !asset.TryGetProperty("digest", out var digest))
            throw new InvalidDataException("업데이트 파일 또는 검증 정보가 없습니다.");
        var hash = digest.GetString() ?? "";
        if (!Regex.IsMatch(hash, "^sha256:[a-fA-F0-9]{64}$")) throw new InvalidDataException("업데이트 검증 정보가 올바르지 않습니다.");
        return new(tag, version, hash[7..]);
    }

    internal static async Task<string> DownloadAsync(UpdateRelease release)
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexUsageWidget", "updates", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            using var response = await client.GetAsync($"https://github.com/{Repository}/releases/download/{release.Tag}/{AssetName}",
                HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            const int maximumSize = 100 * 1024 * 1024;
            if (response.Content.Headers.ContentLength > maximumSize) throw new InvalidDataException("업데이트 파일이 허용 크기를 초과했습니다.");
            await using (var input = await response.Content.ReadAsStreamAsync(timeout.Token))
            await using (var output = File.Create(Path.Combine(directory, AssetName)))
            {
                var buffer = new byte[32768];
                var total = 0L;
                int count;
                while ((count = await input.ReadAsync(buffer, timeout.Token)) > 0)
                {
                    total += count;
                    if (total > maximumSize) throw new InvalidDataException("업데이트 파일이 허용 크기를 초과했습니다.");
                    await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
                }
            }
            await Task.Run(() => Extract(Path.Combine(directory, AssetName), directory, release));
            return directory;
        }
        catch
        {
            Directory.Delete(directory, true);
            throw;
        }
    }

    internal static void Extract(string archive, string directory, UpdateRelease release)
    {
        using (var stream = File.OpenRead(archive))
        {
            if (stream.Length > 100 * 1024 * 1024 || !Convert.ToHexString(SHA256.HashData(stream)).Equals(release.Digest, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("다운로드 파일 검증에 실패했습니다. 설치하지 않았습니다.");
        }
        using var zip = ZipFile.OpenRead(archive);
        if (zip.Entries.Count != 1 || zip.Entries[0].FullName != "CodexUsageWidget.exe" || zip.Entries[0].Length > 100 * 1024 * 1024)
            throw new InvalidDataException("업데이트 압축 파일 구성이 올바르지 않습니다.");
        var executable = Path.Combine(directory, "CodexUsageWidget.exe");
        zip.Entries[0].ExtractToFile(executable);
        var info = FileVersionInfo.GetVersionInfo(executable);
        if (new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart) != release.Version)
            throw new InvalidDataException("업데이트 실행 파일의 버전이 일치하지 않습니다.");
    }

    internal static async Task StartInstallerAsync(string directory, string target)
    {
        // 실행 파일 폴더에 쓸 수 있는지 먼저 확인하여 종료 후 권한 실패를 줄인다.
        var probe = Path.Combine(Path.GetDirectoryName(target)!, $".codex-update-{Guid.NewGuid():N}.tmp");
        using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
        var source = Path.Combine(directory, "CodexUsageWidget.exe");
        string hash;
        using (var stream = File.OpenRead(source)) hash = Convert.ToHexString(SHA256.HashData(stream));
        var job = Path.Combine(directory, "job.json");
        using var current = Process.GetCurrentProcess();
        File.WriteAllText(job, JsonSerializer.Serialize(new { Target = target, Source = source, Hash = hash,
            ProcessId = current.Id, StartedTicks = current.StartTime.ToUniversalTime().Ticks }));
        var script = Path.Combine(directory, "InstallUpdate.ps1");
        using (var resource = typeof(AppUpdate).Assembly.GetManifestResourceStream("CodexUsageWidget.InstallUpdate.ps1")!)
        using (var reader = new StreamReader(resource)) File.WriteAllText(script, reader.ReadToEnd(), new System.Text.UTF8Encoding(true));
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        // 다른 PowerShell 버전에서 상속된 모듈 경로 대신 Windows 기본 모듈을 사용한다.
        start.Environment["PSModulePath"] = Path.Combine(Path.GetDirectoryName(start.FileName)!, "Modules");
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-JobPath", job })
            start.ArgumentList.Add(argument);
        using var helper = Process.Start(start) ?? throw new IOException("업데이트 설치 도우미를 실행하지 못했습니다.");
        var helperError = helper.StandardError.ReadToEndAsync();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!File.Exists(Path.Combine(directory, "ready")))
        {
            if (helper.HasExited || DateTime.UtcNow >= deadline)
            {
                if (!helper.HasExited) helper.Kill(true);
                await helper.WaitForExitAsync();
                throw new IOException($"업데이트 설치 도우미가 준비되지 않았습니다. 기존 버전은 유지됩니다. (종료 코드 {helper.ExitCode})\n" + await helperError);
            }
            await Task.Delay(100);
        }
    }
}
