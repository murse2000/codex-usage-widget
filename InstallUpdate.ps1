param([Parameter(Mandatory = $true)][string]$JobPath)
$ErrorActionPreference = 'Stop'
$directory = Split-Path -LiteralPath $JobPath
$job = Get-Content -LiteralPath $JobPath -Raw -Encoding UTF8 | ConvertFrom-Json
$target = [IO.Path]::GetFullPath($job.Target)
$incoming = $target + '.incoming-' + [Guid]::NewGuid().ToString('N')
$backup = $target + '.backup-' + [Guid]::NewGuid().ToString('N')
$replaced = $false
$parentExited = $false
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
    [IO.File]::WriteAllText((Join-Path $directory 'ready'), 'ready')
    if ($null -ne $parent -and -not $parent.WaitForExit(30000)) {
        throw '위젯이 종료되지 않아 업데이트를 취소했습니다.'
    }
    $parentExited = $true
    # 같은 폴더에서 원자적으로 교체하고, 재실행 실패 시 원본을 복구한다.
    [IO.File]::Copy($job.Source, $incoming, $false)
    if ((Get-FileHash -LiteralPath $incoming -Algorithm SHA256).Hash -ne $job.Hash) {
        throw '복사한 파일 검증에 실패했습니다.'
    }
    [IO.File]::Replace($incoming, $target, $backup)
    $replaced = $true
    [Diagnostics.Process]::Start($restart).Dispose()
    Remove-Item -LiteralPath $backup -Force -ErrorAction SilentlyContinue
}
catch {
    $failure = $_.Exception.Message
    try {
        if ($replaced) { [IO.File]::Replace($backup, $target, $incoming) }
        if ($parentExited) { [Diagnostics.Process]::Start($restart).Dispose() }
    }
    catch { $failure += "`n기존 버전 복구/재실행 실패: " + $_.Exception.Message }
    Add-Type -AssemblyName System.Windows.Forms
    [Windows.Forms.MessageBox]::Show("업데이트를 완료하지 못했습니다.`n$failure", 'Codex 업데이트 오류') | Out-Null
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
