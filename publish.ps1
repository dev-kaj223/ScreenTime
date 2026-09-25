<# Builds a bounded, self-contained portable win-x64 candidate. Does not install,
register startup, create tags, sign, or publish a public release. Requires pwsh 7. #>
[CmdletBinding()]
param(
    [ValidatePattern('^[a-z0-9-]{1,40}$')][string]$BuildLabel = 'candidate',
    [switch]$SkipTests,
    [switch]$AllowDirty
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = $PSScriptRoot
function Run-Dotnet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed ($LASTEXITCODE): $($Arguments -join ' ')" }
}
Push-Location $root
try {
    $sdk = (& dotnet --version).Trim()
    if ($sdk -ne '8.0.425') { throw "Expected pinned SDK 8.0.425, got $sdk" }
    $commit = (& git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Git source identity unavailable.' }
    $dirty = [bool](& git status --porcelain --untracked-files=normal)
    if ($dirty -and -not $AllowDirty) { throw 'Commit reviewed source before constructing a release candidate (or use -AllowDirty for local experiments only).' }
    $stagingRoot = Join-Path $root 'artifacts/staging'
    $stage = Join-Path $stagingRoot ([guid]::NewGuid().ToString('N'))
    $output = Join-Path $stage 'package'
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    # Unique staging is never reused or recursively deleted by this script.
    if (-not $SkipTests) {
        Run-Dotnet @('restore', 'TimeGuard.sln', '--locked-mode')
        Run-Dotnet @('build', 'TimeGuard.sln', '--no-restore', '--no-incremental', '-c', 'Release')
        Run-Dotnet @('test', 'tests/TimeGuard.Tests/TimeGuard.Tests.csproj', '--no-build', '-c', 'Release')
    }
    $build = Join-Path $stage 'build'
    Run-Dotnet @('publish', 'src/TimeGuard.App/TimeGuard.App.csproj', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
        '--artifacts-path', $build, '-o', $output, '-p:RestoreLockedMode=true', '-p:ScreenTimeDistribution=true',
        '-p:ContinuousIntegrationBuild=true', '-p:Deterministic=true', '-p:PublishSingleFile=false', '-p:PublishReadyToRun=false',
        '-p:DebugType=None', '-p:DebugSymbols=false', '-p:NuGetAudit=true', '-p:NuGetAuditMode=all', '-p:WarningsAsErrors=NU1900%3BNU1901%3BNU1902%3BNU1903%3BNU1904', "-p:PathMap=$root=/_/", "-p:SourceRevisionId=$commit")
    $assetFiles = @(Get-ChildItem (Join-Path $build 'obj') -Filter project.assets.json -Recurse |
        Where-Object { $_.FullName -match '[\\/]TimeGuard.App[\\/]' })
    if ($assetFiles.Count -ne 1) { throw 'Expected one isolated app dependency graph.' }
    $assets = Get-Content -Raw $assetFiles[0].FullName | ConvertFrom-Json -AsHashtable
    $targetName = @($assets.targets.Keys | Where-Object { $_ -like '*/win-x64' })
    if ($targetName.Count -ne 1) { throw 'Missing unique win-x64 dependency target.' }
    $licenseMap = Get-Content -Raw packaging/license-map.json | ConvertFrom-Json -AsHashtable
    $packages = @(foreach ($entry in ($assets.targets[$targetName[0]].GetEnumerator() | Sort-Object Key)) {
        if ($entry.Value.type -ne 'package') { continue }
        $id, $version = $entry.Key -split '/', 2
        if (-not $licenseMap.ContainsKey($id)) { throw "No reviewed license coverage for $id/$version" }
        foreach ($license in $licenseMap[$id]) { if (-not (Test-Path (Join-Path 'packaging/licenses' $license))) { throw "Missing license $license" } }
        $runtime = @(); $native = @()
        if ($entry.Value.ContainsKey('runtime')) { $runtime = @($entry.Value.runtime.Keys | Where-Object { $_ -notmatch '(^|/)_\._$' } | Sort-Object) }
        if ($entry.Value.ContainsKey('native')) { $native = @($entry.Value.native.Keys | Where-Object { $_ -notmatch '(^|/)_\._$' } | Sort-Object) }
        $delivered = @(foreach ($asset in @($runtime)+@($native)) {
            $destination = Join-Path $output ([IO.Path]::GetFileName($asset))
            if (-not (Test-Path -LiteralPath $destination)) { continue }
            foreach ($packageRoot in $assets.packageFolders.Keys) {
                $source = Join-Path (Join-Path $packageRoot $assets.libraries[$entry.Key].path) $asset
                if ((Test-Path -LiteralPath $source) -and
                    (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -eq (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash) {
                    [IO.Path]::GetFileName($asset); break
                }
            }
        })
        [ordered]@{ id=$id; version=$version; contentHash=$assets.libraries[$entry.Key].sha512;
            resolvedRuntime=$runtime; resolvedNative=$native; deliveredFiles=$delivered; licenses=$licenseMap[$id] }
    })
    $config = Get-Content -Raw (Join-Path $output 'ScreenTime.runtimeconfig.json') | ConvertFrom-Json
    foreach ($framework in $config.runtimeOptions.includedFrameworks) {
        if ($framework.version -ne '8.0.31') { throw "Unexpected bundled runtime $($framework.name)/$($framework.version)" }
    }
    # Audit only the restored shipping app graph, including transitive packages.
    $priorExtensions = $env:MSBuildProjectExtensionsPath
    try {
        $env:MSBuildProjectExtensionsPath = $assetFiles[0].Directory.FullName + [IO.Path]::DirectorySeparatorChar
        $auditText = & dotnet list src/TimeGuard.App/TimeGuard.App.csproj package --vulnerable --include-transitive --format json
    } finally { $env:MSBuildProjectExtensionsPath = $priorExtensions }
    if ($LASTEXITCODE -ne 0) { throw 'Dependency audit could not complete.' }
    $audit = ($auditText -join "`n") | ConvertFrom-Json -AsHashtable
    foreach ($project in $audit.projects) {
        if ($project.ContainsKey('frameworks')) {
            foreach ($framework in $project.frameworks) {
                foreach ($kind in @('topLevelPackages','transitivePackages')) {
                    if ($framework.ContainsKey($kind) -and $framework[$kind].Count) { throw 'Vulnerable shipping dependency reported; inspect fresh audit.' }
                }
            }
        }
    }
    Copy-Item LICENSE $output
    # Git clean status compares normalized content, so a clean worktree may still
    # contain CRLF bytes. Canonicalize only our package prose; upstream texts below
    # remain verbatim, including their original whitespace and encoding.
    foreach ($name in @('README.txt', 'THIRD-PARTY-NOTICES.txt')) {
        $text = [IO.File]::ReadAllText((Join-Path $root "packaging/$name"))
        $text = $text.Replace("`r`n", "`n").Replace("`r", "`n")
        [IO.File]::WriteAllText((Join-Path $output $name), $text, [Text.UTF8Encoding]::new($false))
    }
    Copy-Item packaging/licenses $output -Recurse
    $files = @(Get-ChildItem $output -File -Recurse | Sort-Object FullName | ForEach-Object {
        [ordered]@{ path=[IO.Path]::GetRelativePath($output,$_.FullName).Replace('\','/'); size=$_.Length; sha256=(Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
    $manifest = [ordered]@{ format=1; product='ScreenTime'; sourceCommit=$commit; sourceDirty=$dirty; sdk=$sdk; rid='win-x64';
        selfContained=$true; defaultProfile='Production'; automaticStartup=$false; runtimes=$config.runtimeOptions.includedFrameworks;
        packages=$packages; files=$files }
    $utf8 = [Text.UTF8Encoding]::new($false)
    [IO.File]::WriteAllText((Join-Path $output 'manifest.json'), ($manifest | ConvertTo-Json -Depth 20)+"`n", $utf8)
    $release = Join-Path $root "artifacts/$BuildLabel"
    if (Test-Path $release) { throw "Output already exists: $release. Choose a new label; existing candidates are never overwritten." }
    New-Item -ItemType Directory -Path $release | Out-Null
    $zip = Join-Path $release 'ScreenTime-win-x64.zip'
    $archive = [IO.Compression.ZipFile]::Open($zip, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in (Get-ChildItem $output -File -Recurse | Sort-Object FullName)) {
            $name = [IO.Path]::GetRelativePath($output,$file.FullName).Replace('\','/')
            $entry = $archive.CreateEntry($name, [IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = [DateTimeOffset]::new(2000,1,1,0,0,0,[TimeSpan]::Zero)
            $entry.ExternalAttributes = 0
            $input = $file.OpenRead(); $stream = $entry.Open()
            try { $input.CopyTo($stream) } finally { $stream.Dispose(); $input.Dispose() }
        }
    } finally { $archive.Dispose() }
    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText("$zip.sha256", "$hash  ScreenTime-win-x64.zip`n", $utf8)
    ($auditText -join "`n") | Set-Content (Join-Path $release 'dependency-audit.json')
    Write-Host "Package: $zip"
    Write-Host "SHA256: $hash"
    Write-Host "Staging: $stage"
} finally { Pop-Location }
