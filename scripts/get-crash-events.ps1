$ErrorActionPreference = 'SilentlyContinue'
Write-Output "===== .NET Runtime (1026) ====="
Get-WinEvent -FilterHashtable @{LogName='Application'; ProviderName='.NET Runtime'} -MaxEvents 30 |
    Where-Object { $_.Message -match 'Pasteup' } |
    Select-Object -First 5 |
    ForEach-Object { "---- $($_.TimeCreated) ----"; $_.Message }
Write-Output ""
Write-Output "===== Application Error (1000) ====="
Get-WinEvent -FilterHashtable @{LogName='Application'; ProviderName='Application Error'} -MaxEvents 40 |
    Where-Object { $_.Message -match 'Pasteup' } |
    Select-Object -First 5 |
    ForEach-Object { "---- $($_.TimeCreated) ----"; $_.Message }
Write-Output ""
Write-Output "===== WER ReportArchive dirs for Pasteup ====="
Get-ChildItem "$env:LOCALAPPDATA\Microsoft\Windows\WER\ReportArchive", "$env:LOCALAPPDATA\Microsoft\Windows\WER\ReportQueue" -Directory |
    Where-Object { $_.Name -match 'Pasteup' } |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 5 FullName, LastWriteTime
