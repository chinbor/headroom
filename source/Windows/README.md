# Headroom for Windows — source

余量 Headroom is a Windows 11 taskbar widget for AI subscription quotas: Claude, Codex, DeepSeek, Agnes and user-written platform recipes. It is a C# 5 / WPF application built with the .NET Framework compiler that ships with Windows (no Visual Studio). It started as a fork of [codeusagemonit](https://github.com/fanchengliu/codeusagemonit) and keeps its local usage engine, pricing and the Claude / Codex quota readers; see `THIRD-PARTY-NOTICES.md`.

## Files

| Area | Files |
| --- | --- |
| Entry, single instance, tray, refresh loop | `App.cs` (`Program`, `MonitorPanel`), `Pacing.cs` |
| Taskbar summary, hover card, right-click panel, alerts | `TaskbarPanel.cs`, `TaskbarFlyout.cs`, `QuickMenu.cs`, `AlertCard.cs` |
| Detail window (额度 / 消耗 / 设置) | `QuotaDashboard.cs`, `UsageCharts.cs`, `UsageDetails.cs`, `SimpleSettings.cs`, `Theme.cs` |
| What a quota means (headline, pace, run-out) | `SubscriptionView.cs`, `Pace.cs` |
| Settings, data directory, Claude / Codex readers | `Core.cs` (`AppConfig`, `Store`, `ProviderService`, `Parsers`) |
| Platform recipes (DeepSeek, Agnes, custom) | `CustomProviders.cs`, `platforms/*.json`, `ConsoleLogin.cs` (WebView2 sign-in), `PlatformUsage.cs`, `BalanceSpend.cs` |
| Local usage engine | `LocalLogs.cs` (Codex, Claude Code), `DeepSeekHarness.cs` + `Zstd.cs`, `UsageScan.cs`, `Timing.cs`, `Pricing.cs` + `pricing.json` |
| Third-party endpoints (relays) | `ThirdParty.cs` |
| Claude Code sign-in renewal | `SessionRecovery.cs`, `BackgroundCli.cs` |
| Interface language | `I18n.cs`, `I18nTable.cs` (English keyed by the Chinese text) |
| Self-tests | `Tests.cs`, `RecoveryTests.cs` |

## Local usage

| Agent | Source read | Notes |
| --- | --- | --- |
| Codex | `CODEX_HOME` / `~/.codex/sessions`, `archived_sessions` (`*.jsonl`) | `last_token_usage`, or the difference of `total_token_usage`; model from `turn_context`; fast / priority multiplier |
| Claude Code | `CLAUDE_CONFIG_DIR` / `~/.claude/projects`, `~/.config/claude/projects` | `message.id` + `requestId` de-duplication; the larger copy of a streamed message wins; `costUSD` used when present |
| DeepSeek Harness | `~/.dsh/sessions/**/session.v4.jsonl.zstd` | zstd frames read incrementally |

- Each agent has an hourly index `data/<agent>-logs.json` (fresh input, cache read, cache write, output, requests, USD, unpriced tokens). Only counts and model names are stored, never content.
- Platforms whose console reports daily usage (DeepSeek, Agnes) replace the local log days for that platform.
- `pricing.json` is compiled from the public LiteLLM and models.dev catalogs (`tools/update-pricing.mjs`, refreshed by `.github/workflows/pricing.yml`); the app downloads the current copy from this repository once a day unless turned off.
- Output speed = output tokens ÷ (request sent → last output written), kept per agent; replies under 50 tokens are not timed.

## Data directory

- **setup.exe**: `%LOCALAPPDATA%\Headroom`. The installer writes `installed.txt` next to the exe and `HKCU\Software\Headroom\InstallPath`; either selects the per-user directory, so Program Files stays read-only.
- **Zip and `install.ps1`**: `<exe>\data`. `portable.txt` next to the exe forces this layout; `HEADROOM_DATA` overrides the directory unless `portable.txt` is present.
- **`--demo`**: `<exe>\verification\demo-data`; the real directory is never read or migrated.
- First start of an installed copy, while `%LOCALAPPDATA%\Headroom` is still empty: the pre-rename `%LOCALAPPDATA%\codeusagemonit` (versions up to 2.0.1) is moved in, otherwise an `<exe>\data` folder. A junction is copied and left in place. The old `codeusagemonit` autostart value is replaced by `Headroom`.

Tokens read by a web sign-in are encrypted with DPAPI (`data/<platform id>.key`) and only sent to the hosts in the recipe. Auto proxy mode reads the Windows HTTP proxy, with HTTP(S)_PROXY as fallback.

## Command line

`headroom.exe [--background] [--demo] [--side-by-side] [--quit] [--self-test]`

- `--background` starts in the tray (the autostart entry uses it); `--quit` asks the running instance to exit.
- `--demo` runs a separate instance on made-up data; `--side-by-side` runs a second copy with its own data that never touches autostart.
- `--self-test` writes `verification\tests.json` and exits non-zero on a failure.

## Build

```powershell
.\source\Windows\build.ps1 -OutputDirectory .\dist             # dist\headroom.exe
Start-Process .\dist\headroom.exe --self-test -Wait            # dist\verification\tests.json
.\source\Windows\build.ps1 -OutputDirectory .\dist -Installer  # + setup.exe, zip, SHA256SUMS.txt
```

`build.ps1` must stay UTF-8 with BOM (Windows PowerShell 5.1 reads Chinese text otherwise as ANSI). The release version is the literal `AssemblyVersion("x.y.z.w")` in `App.cs`. `-Installer` needs Inno Setup 6.3 or newer; see `installer/README.md`.
