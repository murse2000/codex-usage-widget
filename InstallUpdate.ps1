param([Parameter(Mandatory = $true)][string]$JobPath)
$ErrorActionPreference = 'Stop'
$directory = Split-Path -LiteralPath $JobPath
$job = Get-Content -LiteralPath $JobPath -Raw -Encoding UTF8 | ConvertFrom-Json
$target = [IO.Path]::GetFullPath($job.Target)
$incoming = $target + '.incoming-' + [Guid]::NewGuid().ToString('N')
$backup = $target + '.backup-' + [Guid]::NewGuid().ToString('N')
$replaced = $false
$readyWritten = $false
$restart = New-Object Diagnostics.ProcessStartInfo
$restart.FileName = $target
$restart.WorkingDirectory = Split-Path -LiteralPath $target
$restart.UseShellExecute = $false
$restart.CreateNoWindow = $true
try {
    if ((Get-FileHash -LiteralPath $job.Source -Algorithm SHA256).Hash -ne $job.Hash) {
        throw '설치 직전 파일 검증에 실패했습니다.'
    }
    $parent = Get-Process -Id $job.ProcessId -ErrorAction SilentlyContinue
    if ($null -ne $parent -and $parent.StartTime.ToUniversalTime().Ticks -ne $job.StartedTicks) {
        throw '업데이트 대상 프로세스가 변경되었습니다.'
    }
    # ready는 앱을 종료해도 된다는 준비 신호다. C#은 설치 완료를 기다리지 않는다.
    [IO.File]::WriteAllText((Join-Path $directory 'ready'), 'ready')
    $readyWritten = $true
    if ($null -ne $parent -and -not $parent.WaitForExit(30000)) {
        throw '위젯이 종료되지 않아 업데이트를 취소했습니다.'
    }
    # 같은 폴더에서 원자적으로 교체하고, 재실행 실패 시 원본을 복구한다.
    [IO.File]::Copy($job.Source, $incoming, $false)
    if ((Get-FileHash -LiteralPath $incoming -Algorithm SHA256).Hash -ne $job.Hash) {
        throw '복사한 파일 검증에 실패했습니다.'
    }
    [IO.File]::Replace($incoming, $target, $backup)
    $replaced = $true
    [Diagnostics.Process]::Start($restart).Dispose()
    [IO.File]::WriteAllText((Join-Path $directory 'installed'), 'installed')
    Remove-Item -LiteralPath $backup -Force -ErrorAction SilentlyContinue
}
catch {
    $failure = $_.Exception.Message
    # 롤백과 재실행은 독립적으로 시도한다. 백업 이동이 막혀도 가능한 실행 파일로 복구한다.
    if ($replaced) {
        try { [IO.File]::Replace($backup, $target, $incoming) }
        catch { $failure += "`n기존 버전 복구 실패: " + $_.Exception.Message }
    }
    # 롤백이 성공하면 백업은 이미 소모됐으므로 target만 재실행한다.
    $restartTargets = @($target)
    if ($replaced -and (Test-Path -LiteralPath $backup)) { $restartTargets = @($backup, $target) }
    $recoveryStarted = $false
    foreach ($candidate in $restartTargets) {
        try {
            $restart.FileName = $candidate
            $restart.WorkingDirectory = Split-Path -LiteralPath $candidate
            [Diagnostics.Process]::Start($restart).Dispose()
            $recoveryStarted = $true
            break
        }
        catch {
            $name = if ($candidate -eq $backup) { '백업' } else { '기존 버전' }
            $failure += "`n${name} 재실행 실패: " + $_.Exception.Message
        }
    }
    if ($recoveryStarted) {
        try { [IO.File]::WriteAllText((Join-Path $directory 'recovered'), 'recovered') }
        catch { $failure += "`n기존 버전 재실행 기록 실패: " + $_.Exception.Message }
    }
    try { [IO.File]::WriteAllText((Join-Path $directory 'helper-error'), $failure) }
    catch { }
    # 앱이 아직 실행 중일 때는 helper를 다시 시작하지 않고 앱의 오류 안내 경로를 사용한다.
    if (-not $readyWritten) {
        Add-Type -AssemblyName System.Windows.Forms
        [Windows.Forms.MessageBox]::Show("업데이트를 완료하지 못했습니다.`n$failure", 'Codex 업데이트 오류') | Out-Null
    }
}
finally {
    if (Test-Path -LiteralPath $incoming) { Remove-Item -LiteralPath $incoming -Force }
    # 실행 중인 스크립트 폴더는 이번 작업의 고유 임시 폴더로만 제한한다.
    $updatesRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'CodexUsageWidget\updates')) + [IO.Path]::DirectorySeparatorChar
    $resolved = [IO.Path]::GetFullPath($directory)
    if ($resolved.StartsWith($updatesRoot, [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $resolved) -match '^[a-f0-9]{32}$') {
        Remove-Item -LiteralPath $resolved -Recurse -Force -ErrorAction SilentlyContinue
    }
}
