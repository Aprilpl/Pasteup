param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [int]$Runs = 3
)
# POC 闸门 3 辅助：父进程实测 进程拉起 → 首帧渲染完成（PASTEUP_POC_OUT 文件出现首行）
$results = @()
for ($i = 0; $i -lt $Runs; $i++) {
    $out = Join-Path $env:TEMP ("poc-cold-" + [guid]::NewGuid().ToString('N') + ".txt")
    $env:PASTEUP_POC_OUT = $out
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $p = Start-Process -FilePath $Exe -ArgumentList '--poc=coldstart' -PassThru -WindowStyle Hidden
    while ($true) {
        if ((Test-Path $out) -and ((Get-Item $out).Length -gt 0)) { break }
        if ($sw.ElapsedMilliseconds -gt 15000) { break }
        Start-Sleep -Milliseconds 5
    }
    $sw.Stop()
    if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
    $line = if (Test-Path $out) { (Get-Content $out | Select-Object -First 1) } else { '(timeout)' }
    $results += $sw.ElapsedMilliseconds
    Write-Output ("run{0}`t{1}ms`t{2}" -f ($i + 1), $sw.ElapsedMilliseconds, $line)
    Remove-Item $out -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 400
}
# 注：本机 PowerShell 对数组聚合有异常表现，故不输出 SUMMARY；以各次 run 行数据为准
Write-Output "DONE`t(见上方各次 run 行)"
