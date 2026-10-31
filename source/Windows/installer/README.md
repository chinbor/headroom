# Headroom setup.exe

Inno Setup 6 script (`headroom.iss`) for the Windows installer. The portable zip has no `installed.txt`, and the app keeps using a `data` folder next to the executable.

## What the installer does

- Per-user by default (no administrator). The wizard can switch to “all users”, which asks for administrator rights.
- Directory page, default `{autopf}\余量 Headroom`:
  - current user → `%LOCALAPPDATA%\Programs\余量 Headroom`
  - all users → `C:\Program Files\余量 Headroom`
  - any other folder or drive is allowed, including a drive root.
- Start menu shortcut; optional desktop shortcut (off by default).
- Optional “start with Windows” (off by default). Same registry value the app’s own 开机自启 switch uses: `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Headroom` = `"<install>\headroom.exe" --background`.
- Registers an uninstaller under Settings → Apps. Program files are removed. The uninstaller then asks before deleting user data; **No** is the default. A silent uninstall (`unins000.exe /SILENT`) keeps user data. A directory junction is not deleted (Scoop’s persist link), and neither is a `data` folder on a drive root.
- Upgrades use the same AppId and previous directory and replace program files. The data directory is not touched.
- Before copying files it asks a copy running from that folder to `--quit`, then force-closes only processes whose path is inside the install folder.

### Upgrading from codeusagemonit (2.0.1 and earlier)

Those versions were installed under the same AppId as `codeusagemonit.exe`. The new setup:

- deletes `{app}\codeusagemonit.exe` and `HKCU\Software\codeusagemonit`, and stops a running `codeusagemonit.exe` from that folder;
- leaves the rest to the app’s first start: `%LOCALAPPDATA%\codeusagemonit` is moved to `%LOCALAPPDATA%\Headroom` (only while the new folder is empty), and the `codeusagemonit` autostart value becomes `Headroom`.

Uninstall removes either autostart value when it points into the install folder.

## Where data is stored

| How you run it | Settings, keys, caches |
| --- | --- |
| `setup.exe` | `%LOCALAPPDATA%\Headroom` |
| Zip, Scoop, `install.ps1` | `<folder of the exe>\data` |
| `portable.txt` next to the exe | `<folder of the exe>\data`, even if that folder was installed |
| `HEADROOM_DATA` | that path, unless `portable.txt` is present |
| `--demo` | `<folder of the exe>\verification\demo-data` (never the real data) |

`setup.exe` writes `installed.txt` into the install folder and `HKCU\Software\Headroom\InstallPath`. Either one selects the per-user directory, which is what makes a read-only Program Files install work for every Windows user (`installed.txt` is in the program folder; the registry value is only for the user who ran setup).

The first launch of an installed copy also copies an existing `<exe>\data` into `%LOCALAPPDATA%\Headroom` when that folder is still empty, then deletes the old folder. A directory junction is copied and **not** removed.

## Build on Windows

Requires Windows 10 or 11 x64 and [Inno Setup 6.3 or newer](https://jrsoftware.org/isdl.php). No Visual Studio. The Simplified Chinese wizard text is `ChineseSimplified.isl` next to `headroom.iss` (see `THIRD-PARTY-NOTICES.md`); Inno Setup's own `Languages` folder is not used for it. `x64compatible` needs 6.3. The script calls `CopyFile` on 6.4 and newer and `FileCopy` on 6.3.

```powershell
git clone https://github.com/chinbor/headroom.git
cd headroom
.\source\Windows\build.ps1 -OutputDirectory .\dist -Installer
```

This writes, next to `headroom.exe`:

- `headroom-setup-<version>.exe` (version from `AssemblyVersion` in `source\Windows\App.cs`)
- `headroom-<version>-win-x64.zip` (portable; no `installed.txt`). The zip includes `source/` except `source/Windows/VERIFICATION.md`. `setup.exe` installs `使用说明.md` but never `source/`.
- `SHA256SUMS.txt`

`.github/workflows/release.yml` runs the same command on `windows-latest`. Pushing a tag `v<version>` (it must match `AssemblyVersion`) creates the release. **Actions → Build Windows release → Run workflow** with “publish” checked uploads `headroom-setup-<version>.exe` onto an existing release and does not replace a zip that is already published.

## Test on Windows

SmartScreen will warn; the executable is not code-signed. Choose More info → Run anyway.

1. `headroom.exe --self-test` exits 0 (portable vs installed paths, migration into an empty folder, the pre-rename folder, a junction that is copied but not deleted).
2. Run `headroom-setup-<version>.exe`. The directory page opens on `%LOCALAPPDATA%\Programs\余量 Headroom`. Install.
3. The Start menu opens the app. Settings → 打开数据目录 opens `%LOCALAPPDATA%\Headroom`. The install folder contains `installed.txt` and no `data` folder.
4. Change a setting, run setup again (upgrade): the setting is still there.
5. Settings → Apps → 余量 Headroom → Uninstall, choose **No**: the install folder is gone, `%LOCALAPPDATA%\Headroom` remains. Uninstall again after reinstalling and choose **Yes**: that folder is removed.
6. Unzip the portable zip into a writable folder and run it: `data\settings.json` is created beside it and `%LOCALAPPDATA%\Headroom` is not used.
7. Install with “start with Windows” checked: `Run\Headroom` is set; uninstall removes it.
8. The upgrade from 2.0.1 described above.

Silent install and uninstall:

```powershell
.\headroom-setup-<version>.exe /CURRENTUSER /SILENT
& "$env:LOCALAPPDATA\Programs\余量 Headroom\unins000.exe" /SILENT
```

The silent uninstall keeps `%LOCALAPPDATA%\Headroom`. Add `/LOG` to either command to write a log under `%TEMP%`.
