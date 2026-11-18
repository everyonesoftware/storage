#Requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^0\.1\.[1-9][0-9]*$')][string]$Version,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$SourceCommit,
    [string]$NativeDll = (Join-Path $PSScriptRoot '../artifacts/native/windows-x64/storage_core.dll'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../dist/windows'),
    [switch]$AllowUnsigned
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-Checked([string]$Command, [string[]]$Arguments) {
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Command failed ($LASTEXITCODE)" }
}
function Assert-X64Pe([string]$Path, [switch]$RequireGui) {
    $stream = [IO.File]::OpenRead($Path)
    $reader = [IO.BinaryReader]::new($stream)
    try {
        if ($stream.Length -lt 64 -or $reader.ReadUInt16() -ne 0x5A4D) { throw "Not a PE: $Path" }
        $stream.Position = 60; $offset = $reader.ReadUInt32()
        if ($offset -gt $stream.Length - 6) { throw "Invalid PE offset: $Path" }
        $stream.Position = $offset
        if ($reader.ReadUInt32() -ne 0x4550 -or $reader.ReadUInt16() -ne 0x8664) { throw "Not Windows x64: $Path" }
        if ($RequireGui) {
            # An Exe apphost would create a console even if its UI stays hidden.
            if ($offset -gt $stream.Length - 24) { throw "Missing PE optional header: $Path" }
            $stream.Position = $offset + 20; $optionalSize = $reader.ReadUInt16()
            if ($optionalSize -lt 70 -or $offset + 24 + $optionalSize -gt $stream.Length) { throw "Invalid PE optional header: $Path" }
            $stream.Position = $offset + 24
            if ($reader.ReadUInt16() -ne 0x20B) { throw "Not PE32+ executable: $Path" }
            $stream.Position = $offset + 24 + 68
            if ($reader.ReadUInt16() -ne 2) { throw "Console or non-GUI executable rejected: $Path" }
        }
    } finally { $reader.Dispose(); $stream.Dispose() }
}
function Get-PayloadInventory([string]$Directory) {
    $inventory = @{}
    foreach ($file in Get-ChildItem -LiteralPath $Directory -Recurse -Force) {
        if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked payload not allowed' }
        if ($file.PSIsContainer) { continue }
        $name = [IO.Path]::GetRelativePath($Directory, $file.FullName).Replace('\','/')
        if ($inventory.ContainsKey($name)) { throw "Duplicate payload: $name" }
        $inventory[$name] = @{ bytes = $file.Length; sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    }
    return $inventory
}
function Assert-PackagedPayload([string]$PublishDirectory, [string]$PackagePath) {
    $expected = Get-PayloadInventory $PublishDirectory
    $found = @{}; $seen = @{}
    $stream = [IO.File]::OpenRead($PackagePath)
    $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Read)
    try {
        foreach ($entry in $archive.Entries) {
            $name = $entry.FullName
            if ($seen.ContainsKey($name) -or $name -match '(^/|\\|:|(^|/)\.\.?(/|$))' -or (($entry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000) { throw "Unsafe or duplicate archive entry: $name" }
            $seen[$name] = $true
            if (-not $name.StartsWith('lib/app/', [StringComparison]::Ordinal)) { continue }
            $relative = $name.Substring(8)
            if ($relative.EndsWith('/')) { continue }
            if (-not $expected.ContainsKey($relative)) {
                # Exactly these three files are injected by pinned vpk 1.2.161 preprocessing.
                if ($relative -cnotin @('sq.version','Squirrel.exe','Storage_ExecutionStub.exe') -or $entry.Length -le 0) { throw "Unexpected packaged payload: $relative" }
                continue
            }
            $body = $entry.Open()
            try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($body)).ToLowerInvariant() }
            finally { $body.Dispose() }
            if ($entry.Length -ne $expected[$relative].bytes -or $hash -cne $expected[$relative].sha256) { throw "Packaged payload changed: $relative" }
            $found[$relative] = $true
        }
        if ($found.Count -ne $expected.Count) { throw 'Packaged payload missing recursive published files' }
    } finally { $archive.Dispose(); $stream.Dispose() }
}
function Assert-StandalonePayload([string]$Directory, [string]$ReleaseVersion, [string]$Commit) {
    foreach ($name in @('Storage.Browser.exe','Storage.Browser.dll','Storage.Browser.deps.json','Storage.Browser.runtimeconfig.json',
        'resources.pri','Assets/Storage.ico','Microsoft.UI.Xaml.dll','Microsoft.WindowsAppRuntime.dll','coreclr.dll','hostfxr.dll',
        'Service/Storage.Service.exe','Service/Storage.Service.dll','Service/Storage.Service.deps.json','Service/Storage.Service.runtimeconfig.json',
        'Service/Assets/Storage.ico','Service/coreclr.dll','Service/hostfxr.dll','Service/storage_core.dll')) {
        $file = Get-Item -LiteralPath (Join-Path $Directory $name)
        if ($file.Length -le 0 -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Incomplete standalone payload: $name" }
    }
    foreach ($name in @('Storage.Browser.exe','Service/Storage.Service.exe')) { Assert-X64Pe (Join-Path $Directory $name) -RequireGui }
    Assert-X64Pe (Join-Path $Directory 'Service/storage_core.dll')
    foreach ($app in @('Storage.Browser','Service/Storage.Service')) {
        $runtime = Get-Content -LiteralPath (Join-Path $Directory "$app.runtimeconfig.json") -Raw | ConvertFrom-Json -AsHashtable
        if ($runtime.runtimeOptions.ContainsKey('framework') -or $runtime.runtimeOptions.ContainsKey('frameworks') -or -not $runtime.runtimeOptions.includedFrameworks) { throw 'Framework-dependent runtime rejected' }
        $deps = Get-Content -LiteralPath (Join-Path $Directory "$app.deps.json") -Raw | ConvertFrom-Json -AsHashtable
        if ($deps.runtimeTarget.name -notmatch 'win-x64' -or -not $deps.libraries.ContainsKey("$([IO.Path]::GetFileName($app))/$ReleaseVersion")) { throw 'Wrong runtime or application version in published dependencies' }
        $metadata = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $Directory "$app.dll"))
        if ($metadata.ProductVersion -cne "$ReleaseVersion+$Commit") { throw 'Published source/version metadata mismatch' }
    }
    $null = Get-PayloadInventory $Directory
}

if (-not $IsWindows) { throw 'Installer packaging requires native Windows; Linux compile/pack probes are not release builds.' }
if (-not $AllowUnsigned) { throw 'Explicit -AllowUnsigned required. Installer and payload are unsigned, not publisher-verified.' }
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Set-Location $root
$sourceJson = & node scripts/release.mjs version
if ($LASTEXITCODE -ne 0) { throw 'Full committed source history is required' }
$source = $sourceJson | ConvertFrom-Json
if ($source.version -cne $Version -or $source.sha -cne $SourceCommit -or $source.count -gt 65534) { throw 'Version/SourceCommit must match committed release identity' }
$dirty = & git status --porcelain --untracked-files=all -- apps/windows scripts/build-windows-installer.ps1 scripts/test-windows-installer.ps1 scripts/release.mjs tests/windows-release.test.mjs core Cargo.toml Cargo.lock rust-toolchain.toml
if ($LASTEXITCODE -ne 0 -or $dirty) { throw 'Release inputs must be committed and clean' }
$native = (Resolve-Path -LiteralPath $NativeDll).Path
Assert-X64Pe $native
$output = [IO.Path]::GetFullPath($OutputDirectory)
if ((Test-Path -LiteralPath $output) -and ((Get-Item -LiteralPath $output).Attributes -band [IO.FileAttributes]::ReparsePoint -or @(Get-ChildItem -LiteralPath $output -Force).Count)) { throw 'OutputDirectory must be new/empty and not linked' }
$null = New-Item -ItemType Directory -Path $output -Force
$work = Join-Path ([IO.Path]::GetTempPath()) ('storage-pack-' + [guid]::NewGuid().ToString('N'))
$publish = Join-Path $work 'publish'
$null = New-Item -ItemType Directory -Path $publish -Force
try {
    Push-Location (Join-Path $root 'apps/windows')
    try {
    Invoke-Checked dotnet @('tool','restore','--tool-manifest',(Join-Path $root 'apps/windows/.config/dotnet-tools.json'))
    Invoke-Checked dotnet @('run','--project',(Join-Path $root 'apps/windows/Storage.Tests'),'-c','Release','--',$native)
    $properties = @('-p:ContinuousIntegrationBuild=true',"-p:Version=$Version", "-p:InformationalVersion=$Version+$SourceCommit",'-p:IncludeSourceRevisionInInformationalVersion=false')
    Invoke-Checked dotnet (@('publish',(Join-Path $root 'apps/windows/Storage.Browser/Storage.Browser.csproj'),'-c','Release','-r','win-x64','--self-contained','true','-p:WindowsAppSDKSelfContained=true','-o',$publish) + $properties)
    Invoke-Checked dotnet (@('publish',(Join-Path $root 'apps/windows/Storage.Service/Storage.Service.csproj'),'-c','Release','-r','win-x64','--self-contained','true','-o',(Join-Path $publish 'Service')) + $properties)
    } finally { Pop-Location }
    Copy-Item -LiteralPath $native -Destination (Join-Path $publish 'Service/storage_core.dll')
    Assert-StandalonePayload $publish $Version $SourceCommit
    Push-Location (Join-Path $root 'apps/windows')
    try {
        # Preserve every published byte, including nested Service and symbols.
        Invoke-Checked dotnet @('tool','run','vpk','--','[win]','pack','--packId','StorageWindows','--packVersion',$Version,'--packDir',$publish,
            '--mainExe','Storage.Browser.exe','--packTitle','Storage','--packAuthors','Everyone Software','--runtime','win-x64','--channel','win-x64',
            '--framework','vcredist143-x64','--noPortable','--noDefaultExclude','--delta','None','--exclude','(?!)','--shortcuts','StartMenuRoot','--outputDir',$output,'--yes','--skip-updates')
    } finally { Pop-Location }
    Assert-PackagedPayload $publish (Join-Path $output "StorageWindows-$Version-win-x64-full.nupkg")
    $installer = Join-Path $output 'StorageWindows-win-x64-Setup.exe'
    Assert-X64Pe $installer
    $hash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText("$installer.sha256", "$hash  StorageWindows-win-x64-Setup.exe`n", [Text.UTF8Encoding]::new($false))
    $names = @('StorageWindows-win-x64-Setup.exe','StorageWindows-win-x64-Setup.exe.sha256',"StorageWindows-$Version-win-x64-full.nupkg",'releases.win-x64.json','assets.win-x64.json','RELEASES-win-x64')
    $actual = @(Get-ChildItem -LiteralPath $output -Force)
    if ($actual.Count -ne $names.Count) { throw 'Unexpected installer output inventory' }
    foreach ($name in $names) { if (-not (Get-Item -LiteralPath (Join-Path $output $name)).Length) { throw "Missing output: $name" } }
    $after = & node scripts/release.mjs version
    if ($LASTEXITCODE -ne 0 -or ($after | ConvertFrom-Json).sha -cne $SourceCommit) { throw 'Source identity changed during build' }
    $dirtyAfter = & git status --porcelain --untracked-files=all -- apps/windows scripts/build-windows-installer.ps1 scripts/test-windows-installer.ps1 scripts/release.mjs tests/windows-release.test.mjs core Cargo.toml Cargo.lock rust-toolchain.toml
    if ($LASTEXITCODE -ne 0 -or $dirtyAfter) { throw 'Release inputs changed during the installer build' }
    Write-Host 'Unsigned Storage installer and full feed packaged. Installed lifecycle gate is still required before publication.'
} finally { if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force } }
