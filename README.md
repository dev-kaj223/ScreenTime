# ScreenTime

ScreenTime is a local Windows utility for managing selected applications' daily allowance and scheduled downtime. It runs in your interactive Windows session, stores data locally and does not require an online account.

This repository is a development build. Release packaging and production-profile acceptance are Phase 9 work; the upstream TimeGuard downloads are not ScreenTime releases.

## Current behavior

- Each configured app has its own weekday allowance and downtime schedule. Running time counts whether the app is foreground, background or minimized.
- Legitimate allowance exhaustion grants an already-running session one persisted 20-minute **Finish Current Session** period. New sessions cannot join it. Restarting ScreenTime, sleep or midnight does not extend its original deadline.
- Existing grace may continue through newly starting downtime; downtime still blocks new sessions. At the deadline, ScreenTime stops only the captured, verified process instances.
- Left-click the tray icon for password-free, read-only status. Each configured app has separate usage, remaining allowance, restriction and grace facts.
- Right-click for **Open ScreenTime**, **Settings** or **Exit ScreenTime**. Settings and Exit require the configured password. Closing a status or settings window leaves monitoring running.
- Passive notifications show allowance and session milestones, including an optional final-minute countdown. Protected settings provide Standard, Minimal and Custom preferences, colors and a safe preview. Disabling a notice does not disable enforcement.

The initial target is Apex Legends (`r5apex_dx12`). There are no drivers, game hooks, anti-cheat integration, unrelated-app surveillance, aggregate app budget, enforced breaks or automatic crash watchdog. ScreenTime cannot enforce while it is not running and is not a tamper-proof security boundary against the Windows account owner.

## Build and run

Use Windows with the .NET 8 SDK:

```powershell
dotnet restore TimeGuard.sln
dotnet build TimeGuard.sln --no-restore --no-incremental -c Release
dotnet run --project src/TimeGuard.App -c Release --no-build
```

The app output is `src/TimeGuard.App/bin/Release/net8.0-windows/ScreenTime.exe`. The solution, project directories and internal `TimeGuard.*` namespaces remain intentionally stable.

The first run asks you to create a password. Use the tray menu to open Settings; the development shortcut is **Ctrl+Alt+Shift+S**. Development builds use `%AppData%\ScreenTime-Dev` (including `screentime.db`) and do not register Windows startup. They do not import, modify or fall back to installed `%AppData%\TimeGuard` data. Keep installed TimeGuard separate.

## Validation and development

Read [AGENTS.md](AGENTS.md), the [product requirements](docs/PRODUCT_REQUIREMENTS.md), [decisions](docs/DECISIONS.md) and [review protocol](docs/REVIEW_PROTOCOL.md) before changing behavior. The [architecture](docs/SCREEN_TIME_ARCHITECTURE_PLAN.md) includes historical diagnosis; current requirements and reviewed source govern the product.

```powershell
dotnet test tests/TimeGuard.Tests/TimeGuard.Tests.csproj --no-build -c Release --logger trx
dotnet test tests/TimeGuard.UITests/TimeGuard.UITests.csproj --no-build -c Release --logger trx
```

UI tests need an unlocked interactive desktop and must run serially. Fixtures launch `ScreenTime.exe` with unique disposable test profiles and use only their owned `ScreenTime.TestProcess` helpers. Do not use real games or installed TimeGuard as destructive test fixtures. Automated native notification tests do not establish game frame-time or every display configuration.

Core owns policy, measured accounting, SQLite persistence and exact-instance enforcement. The WPF app owns presentation and protected commands. SQLite/Dapper, OxyPlot, xUnit and FlaUI remain the existing dependencies.

Branding is centralized in `src/TimeGuard.App/Branding/ScreenTimeBranding.xaml`. On Windows, run `powershell -STA -File tools/Generate-BrandingIcon.ps1` after changing it; add `-Verify` to check the committed multi-size ICO without writing it. The owner-selected filled-base hourglass is shared by the app, tray, windows and notices.

## License and provenance

ScreenTime is derived from [debmis/TimeGuard](https://github.com/debmis/TimeGuard). The original MIT copyright and permission notice are preserved in [LICENSE](LICENSE). Upstream features and download links are historical context, not the current ScreenTime product contract.
