# Builds the packages the three extension stores want:
#
#   dist/quickbyte-integration-<version>-chrome.zip   Chrome Web Store
#   dist/quickbyte-integration-<version>-edge.zip     Microsoft Edge Add-ons
#   dist/quickbyte-integration-<version>-firefox.zip  addons.mozilla.org
#   dist/firefox/                                     unpacked, for about:debugging
#
# Chrome and Edge take the same package: Edge is Chromium, and the extension
# ships with manifest.json as-is. Firefox gets manifest.firefox.json in its place
# — background scripts instead of a service worker, and an add-on ID — and
# otherwise the very same files.
#
# Every store rejects an archive containing anything the extension does not
# load, so the repo's own documentation is left out rather than shipped to users.
# Icons are NOT regenerated here: they come from BrandIcon.cs (see
# browser/chrome-extension/STORE.md) and are checked in, so packaging stays a
# pure file operation that needs no build.

[CmdletBinding()]
param(
    [string] $OutputDirectory = (Join-Path $PSScriptRoot 'dist'),

    [ValidateSet('all', 'chrome', 'edge', 'firefox')]
    [string] $Browser = 'all'
)

$ErrorActionPreference = 'Stop'

$source = Join-Path $PSScriptRoot 'chrome-extension'
$manifestPath = Join-Path $source 'manifest.json'
$firefoxManifestPath = Join-Path $source 'manifest.firefox.json'

foreach ($path in @($manifestPath, $firefoxManifestPath)) {
    if (-not (Test-Path $path)) { throw "No manifest at $path" }
}

$version = (Get-Content $manifestPath -Raw | ConvertFrom-Json).version
$firefoxVersion = (Get-Content $firefoxManifestPath -Raw | ConvertFrom-Json).version
if ($version -ne $firefoxVersion) {
    throw "manifest.json says $version but manifest.firefox.json says $firefoxVersion. Bump both."
}

# Everything the extension actually loads, named explicitly. A wildcard sweep
# would quietly pick up the next stray file someone drops in the folder, and the
# stores review whatever is in the archive. The manifest is added per browser.
$include = @(
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

function Stage([string] $folder, [string] $manifest) {
    if (Test-Path $folder) { Remove-Item $folder -Recurse -Force }
    foreach ($relative in $include) {
        $target = Join-Path $folder $relative
        New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
        Copy-Item (Join-Path $source $relative) $target
    }
    Copy-Item $manifest (Join-Path $folder 'manifest.json')
}

function Pack([string] $name, [string] $manifest) {
    $staging = Join-Path $OutputDirectory "staging-$name-$version"
    Stage $staging $manifest

    $zip = Join-Path $OutputDirectory "quickbyte-integration-$version-$name.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }

    # The archive's root must be the manifest, not a folder containing it.
    Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zip
    Remove-Item $staging -Recurse -Force

    Write-Host ("{0,-8} {1}  ({2:N0} bytes)" -f $name, $zip, (Get-Item $zip).Length)
}

if ($Browser -in 'all', 'chrome')  { Pack 'chrome' $manifestPath }
if ($Browser -in 'all', 'edge')    { Pack 'edge' $manifestPath }
if ($Browser -in 'all', 'firefox') {
    Pack 'firefox' $firefoxManifestPath

    # Firefox loads a temporary add-on from a folder's manifest.json, and the
    # source folder's manifest.json is Chrome's.
    Stage (Join-Path $OutputDirectory 'firefox') $firefoxManifestPath
    Write-Host "unpacked  $(Join-Path $OutputDirectory 'firefox')  (about:debugging > Load Temporary Add-on)"
}

Write-Host ""
Write-Host "version   : $version"
Write-Host "files     : $($include.Count + 1) per package"
Write-Host ""
Write-Host "Chrome : https://chrome.google.com/webstore/devconsole"
Write-Host "Edge   : https://partner.microsoft.com/dashboard/microsoftedge/overview"
Write-Host "Firefox: https://addons.mozilla.org/developers/"
