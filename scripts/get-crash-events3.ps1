$ErrorActionPreference = 'SilentlyContinue'
Write-Output "===== ALL .NET Runtime 1026 / App Error 1000 / WER 1001, last 12h ====="
Get-WinEvent -FilterHashtable @{LogName='Application'; StartTime=(Get-Date).AddHours(-12)} -MaxEvents 3000 |
    Where-Object { $_.Id -in 1000, 1001, 1026 } |
    Select-Object -First 20 |
    ForEach-Object {
        "---- $($_.TimeCreated) [$($_.ProviderName)/$($_.Id)] ----"
        ($_.Message -split "`n" | Select-Object -First 8) -join "`n"
        ""
    }
Write-Output "===== Real data dir ====="
$d = Join-Path $env:APPDATA 'Pasteup'
Get-ChildItem $d -Recurse -File | Group-Object Extension | ForEach-Object { "{0}  x{1}  {2:N1} MB" -f $_.Name, $_.Count, (($_.Group | Measure-Object Length -Sum).Sum / 1MB) }
Get-Content (Join-Path $d 'settings.json') -ErrorAction SilentlyContinue
