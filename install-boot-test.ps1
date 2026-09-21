param(
 [string]$Repository = 'C:\Users\linde\source\repos\ImageOptCompat',
 [string]$GameRoot = 'C:\Program Files (x86)\Steam\steamapps\common\RimWorld',
 [string]$Config = 'C:\Users\linde\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Config\ModsConfig.xml'
)
$ErrorActionPreference = 'Stop'
if (Get-Process RimWorldWin64 -ErrorAction SilentlyContinue) { throw 'Close RimWorld before installing the boot-test build.' }
$assemblyPath = Join-Path $Repository 'Assemblies\ImageOptCompat.dll'
$previewPath = Join-Path $Repository 'About\Preview.png'
if (-not (Test-Path -LiteralPath $assemblyPath)) { throw 'Packaged DLL missing.' }
if ((Get-Item -LiteralPath $previewPath).Length -ge 1000000) { throw 'Preview is too large.' }
$manifest = Get-Content -LiteralPath (Join-Path $Repository 'boot-test-manifest.json') -Raw | ConvertFrom-Json
if ((Get-FileHash -LiteralPath $assemblyPath -Algorithm SHA256).Hash -ne $manifest.assemblySha256) { throw 'Packaged DLL differs from validated build.' }
$backupDir = Join-Path $PSScriptRoot ('boot-backup-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $backupDir | Out-Null
Copy-Item -LiteralPath $Config -Destination (Join-Path $backupDir 'ModsConfig.xml')
$modDir = Join-Path $GameRoot 'Mods\ImageOptCompat'
if (Test-Path -LiteralPath $modDir) { Copy-Item -LiteralPath $modDir -Destination (Join-Path $backupDir 'ImageOptCompat') -Recurse }
$doc = [xml]::new()
$doc.PreserveWhitespace = $true
$doc.Load($Config)
$active = $doc.ModsConfigData.activeMods
$old = @($active.SelectNodes('li') | Where-Object { $_.InnerText -eq 'local.loadbearing' })
$new = @($active.SelectNodes('li') | Where-Object { $_.InnerText -eq 'degradingant.imageoptcompat' })
if ($old.Count -eq 1 -and $new.Count -eq 0) { $old[0].InnerText = 'degradingant.imageoptcompat' }
elseif ($old.Count -eq 0 -and $new.Count -eq 1) { }
else { throw 'Expected exactly one active old or new compatibility mod; no mod-list changes made.' }
New-Item -ItemType Directory -Force -Path (Join-Path $modDir 'About'),(Join-Path $modDir 'Assemblies') | Out-Null
Copy-Item -LiteralPath $assemblyPath -Destination (Join-Path $modDir 'Assemblies\ImageOptCompat.dll') -Force
Copy-Item -LiteralPath $previewPath -Destination (Join-Path $modDir 'About\Preview.png') -Force
Copy-Item -LiteralPath (Join-Path $Repository 'About\About.xml') -Destination (Join-Path $modDir 'About\About.xml') -Force
foreach ($name in @('README.md','LICENSE')) { Copy-Item -LiteralPath (Join-Path $Repository $name) -Destination (Join-Path $modDir $name) -Force }
if ((Get-FileHash -LiteralPath (Join-Path $modDir 'Assemblies\ImageOptCompat.dll')).Hash -ne $manifest.assemblySha256) { throw 'Installed DLL verification failed.' }
$doc.Save($Config)
Write-Output "Boot-test build installed. Loadbearing is inactive; ImageOptCompat occupies its previous load-order slot. Backup: $backupDir"
