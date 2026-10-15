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

    internal static async Task<UpdateRelease?> FindAsync()
        => Parse(await RunGhAsync(TimeSpan.FromSeconds(30), "api", $"repos/{Repository}/releases/latest"), CurrentVersion);

    internal static UpdateRelease? Parse(string json, Version installed)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Regex.IsMatch(tag, @"^v\d+\.\d+\.\d+$") || !Version.TryParse(tag[1..], out var version))
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
            await RunGhAsync(TimeSpan.FromMinutes(5), "release", "download", release.Tag, "--repo", Repository,
                "--pattern", AssetName, "--dir", directory);
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

    private static async Task<string> RunGhAsync(TimeSpan timeout, params string[] arguments)
    {
        // 탐색기로 실행한 앱의 PATH에 GitHub CLI가 아직 반영되지 않은 경우도 확인한다.
        var gh = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(path => Path.Combine(path.Trim('"'), "gh.exe"))
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "GitHub CLI", "gh.exe"))
            .FirstOrDefault(File.Exists) ?? throw new FileNotFoundException("GitHub CLI(gh)를 설치하고 gh auth login으로 로그인하세요. 비공개 저장소 접근 권한이 필요합니다.");
        var start = new ProcessStartInfo(gh) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("GitHub CLI를 실행하지 못했습니다.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(timeout).ConfigureAwait(false); }
        catch
        {
            if (!process.HasExited) process.Kill(true);
            throw;
        }
        await error.ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new IOException("GitHub 릴리스에 접근하지 못했습니다. GitHub CLI(gh) 설치와 gh auth login, 비공개 저장소 접근 권한을 확인하세요.");
        return await output.ConfigureAwait(false);
    }
}
