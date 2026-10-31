# Third-party notices

codeusagemonit is an independent Windows application. It is not an official release of CodexBar or ccusage and is not affiliated with any monitored service. It contains no code or binaries from the projects below. The files taken from another project are the provider logo SVGs in `icons/` (from CodexBar, see below) and the Simplified Chinese Inno Setup translation `source/Windows/installer/ChineseSimplified.isl` (see below). The other projects are credited because their public source was studied as a reference, or because data derived from them ships with the application.

## Data that ships with the application

### LiteLLM model price catalog
- Source: https://github.com/BerriAI/litellm — `model_prices_and_context_window.json`
- License: MIT (the catalog lives outside the repository's `enterprise/` directory). Full text: `licenses/LiteLLM-MIT.txt`.
- Use: `pricing.json` (USD per million tokens) is compiled from this catalog for the API-equivalent cost estimates.

### models.dev
- Source: https://github.com/anomalyco/models.dev
- License: MIT. Full text: `licenses/models.dev-MIT.txt`.
- Use: long-context price tiers (e.g. whole-request rates above 272K input tokens) and a few models the LiteLLM catalog does not list yet, compiled into `pricing.json`.

Prices are the vendors' published list prices as recorded by these catalogs. The estimates are not bills.

## Bundled libraries

### Microsoft Edge WebView2 SDK 1.0.2903.40

`Microsoft.Web.WebView2.Core.dll`, `Microsoft.Web.WebView2.Wpf.dll` and `WebView2Loader.dll` from the
[Microsoft.Web.WebView2](https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.2903.40) NuGet package,
redistributed unmodified to show the sign-in window for platforms read with a web console token.
Copyright (C) Microsoft Corporation. License: `source/Windows/lib/webview2/LICENSE.txt` (BSD-style).
The WebView2 runtime itself is part of Windows 11 and is not shipped.

## Provider logos

The files in `icons/` are the logos of the monitored products, used unmodified only to identify each provider. They are taken from CodexBar (`Sources/CodexBar/Resources/ProviderIcon-<id>.svg`, MIT, Copyright (c) 2026 Peter Steinberger, license text in `licenses/CodexBar-MIT.txt`; `zcode.svg` is `ProviderIcon-zai.svg`). `agnes.svg` is the Agnes AI mark from lobe-icons (https://github.com/lobehub/lobe-icons, MIT), taken from usage-dash (`hooks/catalog.ts`) without its enclosing disc. The logos and product names are trademarks of their respective owners; their use does not imply endorsement.

## Installer translation

### Inno Setup Simplified Chinese messages
- File: `source/Windows/installer/ChineseSimplified.isl` (included unmodified).
- Source: https://github.com/jrsoftware/issrc/blob/6ef32198ef1f7b7b375cd4b6b90896c2a58eb4c2/Files/Languages/ChineseSimplified.isl
- The same translation lived at `Files/Languages/Unofficial/ChineseSimplified.isl` (still that path in the Inno Setup 6.7.3 source tag). The 6.7.3 installer does not copy unofficial languages into its `Languages` folder, so this repository vendors the file and the setup script references it by a relative path. Inno Setup 7 moved the file to `Files/Languages/`.
- Translation maintainer: Zhenghan Yang (Kira), https://github.com/kira-96/Inno-Setup-Chinese-Simplified-Translation (header in the file: 847320916@QQ.com).
- License: Inno Setup License. Copyright (C) 1997-2026 Jordan Russell. Portions Copyright (C) 2000-2026 Martijn Laan. Full text: `licenses/InnoSetup.txt`.

## Projects studied as references (no code included)

### usage-dash
- https://github.com/chinbor/usage-dash — MIT, Copyright (c) 2026 chinbor. License text: `licenses/usage-dash-MIT.txt`.
- Studied `hooks/providers.ts` and `hooks/lib.ts` for the Agnes platform login, subscription endpoint and four quota-window fields. Independently implemented in C# in `AgnesPlan.cs`; no plugin configuration, tokens or runtime are bundled.

### CodexBar
- https://github.com/steipete/CodexBar — MIT, Copyright (c) 2026 Peter Steinberger. License text kept in `licenses/CodexBar-MIT.txt`.
- The Windows application re-implements, in its own C#, the provider-overview idea and the published behaviour of the providers' quota endpoints (Codex windows and reset credits, Claude utilization, Antigravity local endpoints and agy usage report, DeepSeek balances, the Devin / Windsurf plan status and cached plan fields). Apart from the provider logos above, no CodexBar file is included.

### Claude Code Usage Monitor (CodeZeno)
- https://github.com/CodeZeno/Claude-Code-Usage-Monitor — MIT. License text kept in `licenses/CodeZeno-MIT.txt`.
- Studied for where Windows clients keep their sign-in state (Cursor `state.vscdb`, Antigravity Credential Manager target, Grok CLI session headers).

### ccusage
- https://github.com/ccusage/ccusage — studied for the local log formats of Claude Code, Codex, Antigravity, ZCode, Grok, Kimi, Pi, GitHub Copilot CLI and OpenCode, and their de-duplication rules. codeusagemonit reads those logs with its own engine (`LocalLogs.cs`, `AgentLogs.cs`, `MoreAgentLogs.cs`, `Pricing.cs`); ccusage is neither bundled nor called.

Service names and brand names belong to their respective owners. No proprietary API credentials are bundled.
