$dst = Join-Path $env:TEMP ("pasteup-realdata-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
Copy-Item (Join-Path $env:APPDATA "Pasteup") $dst -Recurse -Force
$s = Join-Path $dst "settings.json"
$t = Get-Content $s -Raw
$t = $t -replace '"PasteOnEnter":\s*true', '"PasteOnEnter": false'
Set-Content -Path $s -Value $t -Encoding UTF8
Write-Output "realdata copy: $dst"
Write-Output "PasteOnEnter now: $((Get-Content $s -Raw) -match 'false')"
