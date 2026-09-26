# ScreenTime

ScreenTime is a local Windows utility for setting independent weekday allowances and scheduled downtime for selected applications. It measures configured apps while they run, including when they are in the background or minimized, and keeps monitoring separate from its user interface.

## Download / Beta 3

The current public prerelease is [ScreenTime v0.1.0-beta.3](https://github.com/dev-kaj223/ScreenTime/releases/tag/v0.1.0-beta.3).

It is an unsigned, self-contained Windows x64 portable app. Download and extract the complete folder, keep its files together, then launch `ScreenTime.exe` manually. There is no installer, automatic updater, automatic startup, or automatic watchdog.

## Quick start

1. Download and extract the Beta 3 portable package.
2. Run `ScreenTime.exe`.
3. Create a password on first launch, then configure the applications, weekday daily allowances, downtime, and notification preferences in Settings.
4. Leave ScreenTime running in the notification area while it monitors configured apps.

ScreenTime uses the production profile at `%AppData%\ScreenTime`. It works locally and offline; no online account is required.

## Features

- Independent daily allowances for each configured application, with separate weekday values.
- Per-app scheduled downtime, including overnight periods.
- Measured runtime for configured apps, including background and minimized runtime.
- Passive, gaming-safe notifications with configurable notification preferences.
- Password-protected Settings and Exit actions.

ScreenTime enforces policy against verified process instances rather than by terminating all processes with a matching name.

## Finish Current Session

When an app legitimately reaches its quota during a continuous permitted session, ScreenTime can grant the already-running captured process instances one persisted 20-minute **Finish Current Session** period.

- Relaunched or replacement processes do not receive that grace.
- The original deadline survives ScreenTime restart, sleep, and midnight; sleep does not pause it.
- Existing legitimate grace is not cut short if scheduled downtime begins. Downtime still blocks new or replacement launches.

The grace period does not replace normal daily allowances and is not granted for an already-exhausted app that is first discovered after the fact.

## Usage and tray UI

Left-click the notification-area icon to open the unified ScreenTime Usage window. It provides current per-app information and password-free Week, Month, and Year usage history.

Right-click the icon to toggle a temporary quick-status popup. It is intended for a brief per-app status check; the main Usage window remains the place for history and full details. Settings and Exit remain password protected. Closing either view does not stop monitoring or enforcement.

## Current beta limitations and non-goals

ScreenTime is a beta release and is not a tamper-proof boundary against the Windows account owner. It is intentionally a focused, local utility:

- No drivers, DLL injection, DirectX or game hooks, anti-cheat integration, network filtering, or unrelated-app surveillance.
- No aggregate cross-app cap, mandatory break enforcement, automatic watchdog, or TimeGuard profile import.
- No installer or automatic updater; launch the portable app manually.

## Data and privacy

ScreenTime stores its configuration, password material, usage data, and diagnostics locally in `%AppData%\ScreenTime`. It does not require an online account or a network service to operate.

ScreenTime does not import from, fall back to, or modify a TimeGuard profile. Existing TimeGuard data and startup registration remain separate.

## Development and build

Build from Windows with the .NET 8 SDK:

```powershell
dotnet restore TimeGuard.sln
dotnet build TimeGuard.sln --no-restore --no-incremental -c Release
dotnet run --project src/TimeGuard.App -c Release --no-build
```

Ordinary development builds use `%AppData%\ScreenTime-Dev`, not the production profile. Tests use disposable isolated profiles. The solution and internal `TimeGuard.*` project and namespace names are intentionally retained.

For development validation and contribution workflow, see [AGENTS.md](AGENTS.md), [product requirements](docs/PRODUCT_REQUIREMENTS.md), [decisions](docs/DECISIONS.md), and the [review protocol](docs/REVIEW_PROTOCOL.md).

## License and TimeGuard provenance

ScreenTime is derived from [debmis/TimeGuard](https://github.com/debmis/TimeGuard) and is distributed under the MIT License. The original attribution and license text are preserved in [LICENSE](LICENSE).
