# Changelog

All notable changes are listed here. The project follows [Semantic Versioning](https://semver.org/) once
version 0.1.0 is tagged. Licensed under MIT.

## [Unreleased]

### Added

- Windows edition of [cctv-overlay](https://github.com/kaibur02/cctv-overlay) with the same features and
  behaviour: grid view and single-camera ring, audio toggle with speaker icon, per-tile reconnect with backoff,
  camera-system presets and path templates, the full CLI (`toggle`, `next`, `prev`, `grid`, `audio`, `show`, `hide`,
  `status [--json]`, `list`, `check`, `reload`, `daemon`, `quit`, `ping`), exit codes, status JSON, and the
  `config.ini` / `secrets.ini` format (Linux configs load unchanged).
- Click-through, no-focus, always-on-top overlay window (layered tool windows) with a GDI+-drawn HUD.
- libmpv video engine with `wid` embedding, Direct3D 11 and D3D11VA zero-copy decoding.
- Per-user named-pipe control channel (your account only, network denied, peer checks) and single-instance mutex.
- `[keys]`: global keys handled by the daemon (the Windows counterpart of the Hyprland bindings).
- `install.cmd` / `install.ps1`: requirements check, build with the built-in C# compiler, per-user install,
  libmpv download with SHA-256 check, camera/display/size/corner/keys wizard, password prompt (console or password
  box) into a private `secrets.ini`, sign-in start, PATH, live test; `--answers` for unattended/AI installs,
  `--set-password`, `--check`, `--test`, `--uninstall`.
- `build.ps1` (`build`, `check`, `smoke`, `demo`, `clean`) with ported unit tests and end-to-end control-pipe tests.
- Friendlier installer: every question re-asks with a plain explanation instead of stopping; keys are captured by
  pressing them (or typed as `Ctrl + Shift + -`, `Ctrl + Num *`, …) and checked for clashes with other programs;
  "keep my setup, change only the keys"; the window stays open when started by double-click; step numbers.
- Key names as people write them (`Ctrl + Shift + -`, `Num *`, `Win + F9`), alongside the Hyprland names.
- Synthetic demo cameras (`tools/demo`), `run-demo.ps1`, and `record-media.ps1`, which made the README media.

### Fixed

- The installer no longer crashes on an unrecognised key combination, or when it is re-run while cctv-overlay is
  stopped (a Windows PowerShell 5.1 stderr quirk).
