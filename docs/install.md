# Installing

## The installer

```bat
install.cmd                          :: interactive wizard
install.cmd --check                  :: check requirements only, change nothing
install.cmd --set-password           :: set or change the recorder password (typed by you)
install.cmd --test                   :: open the overlay once and report whether each camera connects
install.cmd --answers FILE --yes     :: unattended, from an answers file (see examples\install.answers.example)
install.cmd --uninstall              :: remove the program, sign-in start and PATH entry
```

`install.cmd` runs `install.ps1` with `powershell -ExecutionPolicy Bypass`, so it works without changing your
PowerShell execution policy. PowerShell-style switches (`-SetPassword`, `-Check`, …) work too when you run
`install.ps1` directly.

What it changes, all inside your own profile (no administrator rights):

| Where | What |
|---|---|
| `%LOCALAPPDATA%\Programs\cctv-overlay\` | `cctv-overlay.exe`, `cctv-overlayw.exe`, `libmpv-2.dll`, examples, docs |
| `%APPDATA%\cctv-overlay\config.ini` | your configuration (an existing one is kept unless you choose to replace it; it is backed up first) |
| `%APPDATA%\cctv-overlay\secrets.ini` | the password, readable only by your account |
| `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` → `cctv-overlay` | starts `cctv-overlayw.exe daemon` at sign-in (asked) |
| your user `PATH` | the install folder, so `cctv-overlay` works in new terminals (asked) |
| `%LOCALAPPDATA%\cctv-overlay\daemon.log` | the background daemon's log |

Set `CCTV_OVERLAY_PREFIX` to install somewhere else.

### libmpv

The video engine is libmpv, the library behind the mpv player. The installer downloads the current
`mpv-dev-x86_64-*.7z` (or `aarch64` on ARM) from the mpv project's recommended Windows builds,
[github.com/shinchiro/mpv-winbuild-cmake](https://github.com/shinchiro/mpv-winbuild-cmake/releases), checks its
SHA-256 against the release listing, and unpacks only `libmpv-2.dll` with the `tar.exe` built into Windows.

To use your own copy instead, put `libmpv-2.dll` next to `install.ps1` (or set `MPV_DLL = C:\path\libmpv-2.dll` in
an answers file). Any libmpv with client API 2.0 or newer (mpv 0.35+) works. Re-running the installer offers to
update an installed libmpv.

## Manual build and install

Nothing needs to be installed to build: Windows includes the .NET Framework C# compiler.

```bat
powershell -ExecutionPolicy Bypass -File build.ps1
```

This produces `build\cctv-overlay.exe` and `build\cctv-overlayw.exe`. Copy both and `libmpv-2.dll` into one
folder, create `%APPDATA%\cctv-overlay\config.ini` from `examples\config.example.ini` and `secrets.ini` as described
in [configuration.md](configuration.md#secretsini), and run:

```bat
cctv-overlay check
cctv-overlay toggle
```

To start the daemon at sign-in without the installer, add a shortcut to `cctv-overlayw.exe daemon` to
`shell:startup`, or:

```powershell
Set-ItemProperty HKCU:\Software\Microsoft\Windows\CurrentVersion\Run cctv-overlay '"C:\path\to\cctv-overlayw.exe" daemon'
```

## Updating

Pull or download the new version and run `install.cmd` again. It stops the running daemon, installs the new
program, keeps your configuration and password, and starts the daemon again.

## Uninstalling

```bat
install.cmd --uninstall
```

It stops the daemon, removes the program folder, the sign-in entry and the PATH entry, and asks before deleting
your configuration and password (`%APPDATA%\cctv-overlay`).
