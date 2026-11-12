$ErrorActionPreference = 'SilentlyContinue'
Write-Output "===== ALL events mentioning Pasteup, last 1 day, any provider ====="
Get-WinEvent -FilterHashtable @{LogName='Application'; StartTime=(Get-Date).AddDays(-1)} -MaxEvents 2000 |
    Where-Object { $_.Message -match 'Pasteup' } |
    ForEach-Object {
        "---- $($_.TimeCreated) [$($_.ProviderName) / $($_.Id)] [$($_.LevelDisplayName)] ----"
        ($_.Message -split "`n" | Select-Object -First 12) -join "`n"
    }
Write-Output ""
Write-Output "===== LocalDumps configured? ====="
Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps' | Format-List | Out-String
Write-Output "===== Pasteup processes ====="
Get-Process Pasteup -ErrorAction SilentlyContinue | Select-Object Id, StartTime, Path | Format-List | Out-String
