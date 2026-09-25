# Portable package preparation

`publish.ps1` prepares an unsigned self-contained Windows x64 **folder ZIP**. Keep every extracted file together. The prior single-file publishing path was replaced deliberately: explicit runtime/native files avoid temporary native extraction, allow direct module-path verification, and make the complete delivered file/notice inventory independently hashable. This remains a portable app; there is no installer or automatic updater.

## Build and verify

Install the SDK pinned in `global.json` (8.0.425), use PowerShell 7, and start from a clean committed checkout. Runtime/framework versions and the RID-specific NuGet graph are locked. From the repository root:

```powershell
./publish.ps1 -BuildLabel candidate-a
./tools/Test-ReleasePackage.ps1 -Package artifacts/candidate-a/ScreenTime-win-x64.zip
./publish.ps1 -BuildLabel candidate-b -SkipTests
Get-FileHash artifacts/candidate-a/ScreenTime-win-x64.zip
Get-FileHash artifacts/candidate-b/ScreenTime-win-x64.zip
```

The default build runs Release/Core validation. `-SkipTests` is for an already validated source revision, not a waiver of release tests. `-AllowDirty` is for local experiments only and marks the manifest accordingly; never deliver that as an approved candidate. The script creates new bounded staging/output directories and refuses to overwrite a label. It never deletes other outputs. `artifacts/` is ignored. Fresh stages, including an independent clean checkout, must produce matching complete archive hashes before claiming reproducibility. First-party README/notice prose is emitted as UTF-8 without BOM with LF line endings regardless of working-tree checkout bytes; upstream license texts remain verbatim. The guarantee is scoped to the pinned SDK/runtime/dependencies, source revision, Windows build tooling and package inputs; it is not an untested cross-platform/toolchain guarantee.

`manifest.json` includes source SHA/dirty status, SDK/RID/runtime versions, the resolved application-only dependency graph, license coverage and delivered file hashes. Placeholder assets are excluded. `deliveredFiles` identifies byte-matching resolved package assets actually copied; framework-provided replacements are distinguished from candidates. The separate archive SHA-256 covers the manifest itself too. ZIP entry order/timestamps/attributes are normalized. Dependency audit output lives beside the archive, outside its deterministic payload; it is fresh external evidence, not a frozen assurance.

The isolated publish restore enables all-transitive NuGet audit and treats advisory/fetch failures as errors. The supplemental JSON audit is directed at that same isolated app assets graph. CI builds packages for `screentime-dev` PRs/integration and uploads review artifacts only. It has read-only repository permissions, no tag trigger and no GitHub Release step. Nothing signs, installs or publishes publicly.

## Profiles, runtime and operation

Only `ScreenTimeDistribution=true`, explicitly passed by packaging, compiles the distribution default. Ordinary Debug/Release builds remain Development. The distribution uses `%AppData%\ScreenTime`, development uses `%AppData%\ScreenTime-Dev`, and explicit test arguments use unique disposable paths. Test launch cannot point into any real ScreenTime/Dev/TimeGuard profile. `legacy-production` is not an accepted launch selection. There is no import, migration from legacy TimeGuard or fallback to it.

Autostart is disabled; the existing TimeGuard Run entry is preserved. Launch manually. A fresh production profile defaults its configurable settings shortcut to `Ctrl+Alt+S`; saved production choices are honored. Development retains `Ctrl+Alt+Shift+S`. Tray access remains available if a shortcut conflicts. Policy/password/data changes remain local. Exit is protected; close status freely without stopping enforcement.

The package includes .NET/WindowsDesktop 8.0.31, independently confirmed by its executable and module paths. [.NET 8 support ends 2026-11-10](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core). Reassess lifecycle/security before later distribution; updating a machine-wide runtime does not service a self-contained package.

The minimal Sqlite 8.0.31 update resolves SQLitePCLRaw 2.1.12. The [reviewed SQLite advisory](https://github.com/advisories/GHSA-2m69-gcr7-jv3q) affects <=2.1.11 and requires native SQLite >=3.50.2. Validate the actual extracted native version; do not rely only on the managed package label or suppress an advisory. This package's final version/audit is recorded in PR evidence.

## Acceptance and owner-controlled actions

`Test-ReleasePackage.ps1` verifies every hash, absence of test dependencies, actual distribution defaults, Test override, rejection of legacy arguments, and native-load failure. The read-only probe runs before logger/storage/startup setup; both successful and failed probes are checked against before/after real profile hashes. It verifies included runtime/native module paths with machine-runtime lookup disabled. All actual app smoke tests launch the extracted executable in disposable Test profiles. They exercise setup, resources/tray, duplicate launch, protected Settings/Exit, actual quota crossing to an original twenty-minute episode, relaunch denial, restart identity/deadline preservation and seeded near-expiry recovery/termination. No test-host MonitorService substitutes for packaged enforcement.

```powershell
$env:SCREENTIME_RELEASE_DIRECTORY = '<verified extracted folder>'
dotnet test tests/TimeGuard.UITests/TimeGuard.UITests.csproj --no-build -c Release --filter FullyQualifiedName~ReleaseSmokeTests --logger trx
# Optional stronger actual elapsed 20-minute package gate:
$env:SCREENTIME_RELEASE_FULL_DURATION = '1'
dotnet test tests/TimeGuard.UITests/TimeGuard.UITests.csproj --no-build -c Release --filter FullyQualifiedName~ExtractedPackage_ActualGrant_RelaunchDenied_RestartKeepsOriginalTwentyMinutes --logger trx
Remove-Item Env:SCREENTIME_RELEASE_FULL_DURATION, Env:SCREENTIME_RELEASE_DIRECTORY
```

Retain the exact tested archive/hash and manifest source commit. After integration merges that source, deliver that tested artifact rather than silently rebuilding a different binary. New corrections require a new candidate and affected validation. Seeded near-expiry testing is not an elapsed twenty-minute claim. Rendered scaling and isolated profiles do not claim a physical mixed-monitor or separate Windows-account matrix. Runtime operation uses local files/IPC only; no network-dependent service is introduced, and no firewall/network configuration is changed for validation.

Before public publication, the owner must approve the target/channel/version and, if desired, provide signing credentials through their normal secure process. These are not required to prepare/review the unsigned candidate. No code-signing key, store credential, public release, installation over TimeGuard, uninstall, real profile migration or deletion of history is authorized here. Portable removal means exiting the app and removing only its extracted application folder while keeping `%AppData%\ScreenTime`; there is no automated uninstall action. The archive includes project LICENSE/upstream attribution and actual third-party license/NOTICE texts.
