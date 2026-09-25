[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Package,
    [int]$ActiveProductionProcessId,
    [long]$ActiveProductionStartUtcTicks
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$activeProduction = $PSBoundParameters.ContainsKey('ActiveProductionProcessId')
if ($activeProduction -ne $PSBoundParameters.ContainsKey('ActiveProductionStartUtcTicks')) {
    throw 'Active production verification requires both expected process ID and start UTC ticks.'
}
$ownerProcess = $null
try {
if ($activeProduction) {
    if ($ActiveProductionProcessId -le 0 -or $ActiveProductionStartUtcTicks -le 0) { throw 'Invalid active production identity.' }
    $ownerProcess = [Diagnostics.Process]::GetProcessById($ActiveProductionProcessId)
    $null = $ownerProcess.Handle # Retain this exact instance; never stop or modify it.
    if ($ownerProcess.HasExited -or $ownerProcess.ProcessName -cne 'ScreenTime' -or
        $ownerProcess.StartTime.ToUniversalTime().Ticks -ne $ActiveProductionStartUtcTicks) {
        throw 'Active production process does not match the expected ScreenTime instance.'
    }
}
$root = Split-Path $PSScriptRoot -Parent
$zip = (Resolve-Path -LiteralPath $Package).Path
$expected = ((Get-Content -Raw -LiteralPath "$zip.sha256") -split '\s+')[0]
if ((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash -ine $expected) { throw 'Archive hash mismatch.' }
$extract = Join-Path $root ('artifacts/extracted/' + [guid]::NewGuid().ToString('N'))
[IO.Compression.ZipFile]::ExtractToDirectory($zip, $extract)
$manifest = Get-Content -Raw (Join-Path $extract 'manifest.json') | ConvertFrom-Json
$actual = @(Get-ChildItem $extract -File -Recurse)
if ($actual.Count -ne $manifest.files.Count + 1) { throw 'Unexpected delivered file count.' }
foreach ($file in $manifest.files) {
    $path = [IO.Path]::GetFullPath((Join-Path $extract $file.path))
    if (-not $path.StartsWith($extract + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Manifest path escapes package.' }
    if ((Get-Item -LiteralPath $path).Length -ne $file.size -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ine $file.sha256) { throw "File mismatch: $($file.path)" }
}
if (-not $manifest.selfContained -or $manifest.defaultProfile -ne 'Production' -or $manifest.automaticStartup) { throw 'Unexpected package policy.' }
if (@($actual | Where-Object { $_.Name -match 'xunit|testhost|FlaUI|ScreenTime.TestProcess' }).Count) { throw 'Test dependency leaked into distribution.' }
function Probe([string[]]$Arguments) {
    $info = [Diagnostics.ProcessStartInfo]::new((Join-Path $extract 'ScreenTime.exe'))
    $info.UseShellExecute=$false; $info.CreateNoWindow=$true
    $info.RedirectStandardOutput=$true; $info.RedirectStandardError=$true
    $info.Environment.Remove('TIMEGUARD_TEST_DB') | Out-Null
    $info.Environment['DOTNET_ROOT'] = (Join-Path $extract 'no-installed-runtime')
    $info.Environment['DOTNET_MULTILEVEL_LOOKUP']='0'
    foreach($arg in $Arguments) { $info.ArgumentList.Add($arg) }
    $process=[Diagnostics.Process]::Start($info)
    try {
        $null=$process.Handle # Retain the exact process handle before timeout/cleanup.
        $stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
        if(-not $process.WaitForExit(15000)){throw 'Runtime probe timed out.'}
        if(-not [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($stdout,$stderr),5000)){throw 'Runtime probe output timed out.'}
        [pscustomobject]@{ExitCode=$process.ExitCode;Output=$stdout.Result;Error=$stderr.Result}
    } finally {
        if(-not $process.HasExited){$process.Kill();$null=$process.WaitForExit(5000)} # Exact launched process; no tree/name cleanup.
        $process.Dispose()
    }
}
function UserState {
    @('ScreenTime','ScreenTime-Dev','TimeGuard') | ForEach-Object {
        # The explicitly identified owner instance can legitimately write/lock only
        # this production profile. Never claim its live bytes are unchanged.
        if ($activeProduction -and $_ -eq 'ScreenTime') { return }
        $path=Join-Path ([Environment]::GetFolderPath('ApplicationData')) $_
        if(Test-Path -LiteralPath $path) {
            "directory:$path"
            Get-ChildItem -LiteralPath $path -File -Recurse | Sort-Object FullName | ForEach-Object { "$($_.FullName)|$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash)" }
        }
    }
    $runPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    if (-not (Test-Path -LiteralPath $runPath)) { 'startup-key:absent' }
    else {
        $runKey = Get-Item -LiteralPath $runPath
        'startup-key:present'
        foreach ($name in @($runKey.GetValueNames() | Sort-Object)) {
            [ordered]@{ Name=$name; Kind=$runKey.GetValueKind($name).ToString(); Value=$runKey.GetValue($name) } | ConvertTo-Json -Compress
        }
    }
}
$beforeState=@(UserState)
$probe=Probe @('--describe-runtime')
if($probe.ExitCode -ne 0){throw $probe.Error}
$description=$probe.Output|ConvertFrom-Json
$production=Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'ScreenTime'
if(-not $description.Distribution -or $description.Profile -ne 'Production' -or $description.Root -ne $production -or $description.AllowsStartup){throw 'Distribution default profile mismatch.'}
if([version]$description.SqliteVersion -lt [version]'3.50.2'){throw 'Packaged SQLite is older than security minimum.'}
if($description.Framework -ne '.NET 8.0.31'){throw 'Unexpected packaged .NET runtime.'}
foreach($module in @($description.RuntimeModule,$description.DesktopModule,$description.SqliteModule)) {
    if(-not $module.StartsWith($extract+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw "Module loaded outside extracted package: $module"}
}
$testRoot=Join-Path $extract 'uncreated-test-profile'
$test=Probe @('--describe-runtime','--test-profile',$testRoot)
if($test.ExitCode -ne 0 -or ($test.Output|ConvertFrom-Json).Profile -ne 'Test' -or (Test-Path $testRoot)){throw 'Test override is not isolated/pure.'}
$rejected=Probe @('--describe-runtime','--profile','legacy-production')
if($rejected.ExitCode -eq 0){throw 'Legacy profile was accepted.'}
$native=Join-Path $extract 'e_sqlite3.dll'
$disabled=Join-Path $extract 'e_sqlite3.dll.probe-disabled'
try {
    Move-Item -LiteralPath $native -Destination $disabled
    $failedNative=Probe @('--describe-runtime')
    if($failedNative.ExitCode -eq 0 -or -not $failedNative.Error){throw 'Missing native dependency must fail diagnostically.'}
    $failedNativeTest=Probe @('--describe-runtime','--test-profile',$testRoot)
    if($failedNativeTest.ExitCode -eq 0 -or -not $failedNativeTest.Error -or (Test-Path -LiteralPath $testRoot)) {
        throw 'Missing native dependency must fail without creating the isolated profile.'
    }
} finally {if(Test-Path -LiteralPath $disabled){Move-Item -LiteralPath $disabled -Destination $native}}
if((@($beforeState) -join "`n") -cne (@(UserState) -join "`n")){throw 'A diagnostic probe modified a user profile.'}
if ($activeProduction) {
    $ownerProcess.Refresh()
    if ($ownerProcess.HasExited -or $ownerProcess.StartTime.ToUniversalTime().Ticks -ne $ActiveProductionStartUtcTicks) {
        throw 'Expected active production instance did not survive verification.'
    }
}
[pscustomobject]@{
    ArchiveSha256=$expected; Extracted=$extract; Description=$description; Files=$actual.Count; LegacyRejected=$true
    ProductionProfileBytes= $(if ($activeProduction) { 'NotComparedActiveOwner' } else { 'Unchanged' })
    LegacyAndDevelopmentProfiles='Unchanged'; StartupValues='Unchanged'
    ActiveProductionIdentity= $(if ($activeProduction) { [ordered]@{ ProcessId=$ActiveProductionProcessId; StartUtcTicks=$ActiveProductionStartUtcTicks; Survived=$true } } else { $null })
}|ConvertTo-Json -Depth 6
} finally { if ($null -ne $ownerProcess) { $ownerProcess.Dispose() } }
