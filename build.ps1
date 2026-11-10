# CircleLine'ı derler ve yayın paketini (zip + SHA256SUMS.txt) dist\ klasörüne üretir.
# Geliştirici: thezencirs (https://github.com/thezencirs)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

$version = (Get-Content "$root\version.txt" -Raw).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "version.txt 'MAJOR.MINOR.PATCH' biçiminde olmalı (şu an: '$version')" }

$csc = @("$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe", "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $csc) { throw ".NET Framework 4 derleyicisi (csc.exe) bulunamadı." }

$obj = Join-Path $root "obj"
$dist = Join-Path $root "dist"
Remove-Item $obj, $dist -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory $obj, $dist | Out-Null

@"
using System.Reflection;
[assembly: AssemblyTitle("CircleLine by thezencirs")]
[assembly: AssemblyCompany("thezencirs")]
[assembly: AssemblyProduct("CircleLine")]
[assembly: AssemblyCopyright("Copyright (c) 2026 thezencirs - MIT License")]
[assembly: AssemblyVersion("$version.0")]
[assembly: AssemblyFileVersion("$version.0")]
"@ | Set-Content (Join-Path $obj "AssemblyInfo.g.cs") -Encoding UTF8

Write-Host "Derleniyor: CircleLine v$version (by thezencirs)" -ForegroundColor Cyan
$wpfDir = Join-Path (Split-Path $csc -Parent) "WPF"
& $csc /nologo /codepage:65001 /target:winexe /optimize+ /win32icon:"$root\assets\circleline.ico" /out:"$dist\CircleLine.exe" `
    /lib:"$wpfDir" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll `
    /r:PresentationCore.dll /r:WindowsBase.dll /r:System.Xaml.dll `
    "$root\src\*.cs" "$obj\AssemblyInfo.g.cs"
if ($LASTEXITCODE -ne 0) { throw "Derleme başarısız (kod $LASTEXITCODE)" }

# Yayın zip'i: exe + lisans
$stage = Join-Path $obj "stage"
New-Item -ItemType Directory $stage | Out-Null
Copy-Item "$dist\CircleLine.exe", "$root\LICENSE" $stage
$zipName = "CircleLine-v$version-win.zip"
Compress-Archive -Path "$stage\*" -DestinationPath (Join-Path $dist $zipName)

# SHA256SUMS.txt
$utf8 = New-Object System.Text.UTF8Encoding($false)
$sums = ""
foreach ($f in @($zipName, "CircleLine.exe")) {
    $h = (Get-FileHash (Join-Path $dist $f) -Algorithm SHA256).Hash.ToLowerInvariant()
    $sums += "$h  $f`n"
}
[IO.File]::WriteAllText((Join-Path $dist "SHA256SUMS.txt"), $sums, $utf8)

$cl = [IO.File]::ReadAllText("$root\CHANGELOG.md", [Text.Encoding]::UTF8)
$m = [regex]::Match($cl, "(?ms)^## \[$([regex]::Escape($version))\][^\r\n]*\r?\n(.*?)(?=^## |\z)")
$notes = if ($m.Success) { $m.Groups[1].Value.Trim() } else { "CircleLine v$version by thezencirs" }
[IO.File]::WriteAllText((Join-Path $dist "RELEASE_NOTES.md"), $notes + "`n", $utf8)

Write-Host "Hazır:" -ForegroundColor Green
Get-ChildItem $dist | Select-Object Name, @{n = "KB"; e = { [math]::Round($_.Length / 1KB, 1) } } | Format-Table -AutoSize
