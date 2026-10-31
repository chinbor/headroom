# Verification checklist

Maintainer notes; not shipped in the zip or by setup.exe. Results of past releases are in `CHANGELOG.md`; the upstream codeusagemonit verification log (up to 1.2) is in its own repository.

## Every release

1. `build.ps1 -OutputDirectory <dir>` builds with no `error CS` line.
2. `headroom.exe --self-test` exits 0; `verification\tests.json` lists every check and no errors.
3. `headroom.exe --demo`: the taskbar summary, hover card, right-click panel and the three pages of the detail window show the demo platforms; minimise, maximise and restore animate and do not flash white.
4. `build.ps1 -Installer` writes `headroom-setup-<version>.exe`, `headroom-<version>-win-x64.zip` and `SHA256SUMS.txt`; the zip has `使用说明.md` and no `VERIFICATION.md` or `installed.txt`.

## Installer and upgrades

See the manual steps in `installer/README.md`. For the rename (2.0.1 → 2.1):

- Install 2.0.1, turn on 开机自启, change a setting, quit.
- Run the new setup.exe: it installs into the same folder, `codeusagemonit.exe` is gone, `HKCU\Software\codeusagemonit` is gone and `HKCU\Software\Headroom\InstallPath` is set.
- First start: `%LOCALAPPDATA%\codeusagemonit` has moved to `%LOCALAPPDATA%\Headroom` with the setting; `HKCU\...\Run` has `Headroom` = `"<install>\headroom.exe" --background` and no `codeusagemonit`.
- `install.ps1` over a 2.0.1 copy in `Programs\codeusagemonit`: the folder becomes `Programs\Headroom` with its `data` and no `codeusagemonit.exe`.
