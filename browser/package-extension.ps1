# Builds the .zip the Chrome Web Store dashboard wants.
#
# The store rejects an archive containing anything the extension does not load,
# so the repo's own documentation is left out rather than shipped to users.
# Icons are NOT regenerated here: they come from BrandIcon.cs (see
# browser/chrome-extension/STORE.md) and are checked in, so packaging stays a
# pure file operation that needs no build.

[CmdletBinding()]
param(
    [string] $OutputDirectory = (Join-Path $PSScriptRoot 'dist')
)

$ErrorActionPreference = 'Stop'

$source = Join-Path $PSScriptRoot 'chrome-extension'
$manifestPath = Join-Path $source 'manifest.json'

if (-not (Test-Path $manifestPath)) { throw "No manifest at $manifestPath" }

$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
$version = $manifest.version

# Everything the extension actually loads, named explicitly. A wildcard sweep
# would quietly pick up the next stray file someone drops in the folder, and the
# store reviews whatever is in the archive.
$include = @(
    'manifest.json',
    'background.js',
    'content.js',
    'shared.js',
    'options.html',
    'options.js',
    'popup.html',
    'popup.js',
    'style.css',
    'icons/icon16.png',
    'icons/icon32.png',
    'icons/icon48.png',
    'icons/icon128.png'
)

$missing = $include | Where-Object { -not (Test-Path (Join-Path $source $_)) }
if ($missing) { throw "Missing from the extension folder: $($missing -join ', ')" }

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$staging = Join-Path $OutputDirectory "staging-$version"
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }

foreach ($relative in $include) {
    $target = Join-Path $staging $relative
    New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
    Copy-Item (Join-Path $source $relative) $target
}

$zip = Join-Path $OutputDirectory "quickbyte-integration-$version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }

# The archive's root must be the manifest, not a folder containing it.
Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zip
Remove-Item $staging -Recurse -Force

Write-Host "packaged  : $zip"
Write-Host "version   : $version"
Write-Host "files     : $($include.Count)"
Write-Host "size      : $('{0:N0}' -f (Get-Item $zip).Length) bytes"
Write-Host ""
Write-Host "Upload at https://chrome.google.com/webstore/devconsole - then put the"
Write-Host "extension ID it gives you into 'QuickByte App Packager.iss'."
