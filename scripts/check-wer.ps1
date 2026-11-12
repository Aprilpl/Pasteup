$ErrorActionPreference = 'SilentlyContinue'
"===== WER registry ====="
Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\Windows Error Reporting' | Format-List Disabled, DontShowUI, DumpFolder
"===== CrashDumps folder ====="
Get-ChildItem "$env:LOCALAPPDATA\CrashDumps" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 10 Name, LastWriteTime, Length
"===== WER ReportArchive/Queue (last 24h) ====="
Get-ChildItem "$env:LOCALAPPDATA\Microsoft\Windows\WER\ReportArchive", "$env:LOCALAPPDATA\Microsoft\Windows\WER\ReportQueue" -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.LastWriteTime -gt (Get-Date).AddHours(-24) } |
    Sort-Object LastWriteTime -Descending | Select-Object -First 10 Name, LastWriteTime
"===== System log: Defender / app errors last 12h mentioning pasteup ====="
Get-WinEvent -FilterHashtable @{LogName='System'; StartTime=(Get-Date).AddHours(-12)} -MaxEvents 2000 |
    Where-Object { $_.Message -match 'Pasteup' } |
    Select-Object -First 5 | ForEach-Object { "---- $($_.TimeCreated) [$($_.ProviderName)/$($_.Id)] ----"; $_.Message }
"done"
