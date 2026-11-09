# Configuration

cctv-overlay uses two INI files in one folder, `%APPDATA%\cctv-overlay\`
(usually `C:\Users\<you>\AppData\Roaming\cctv-overlay\`):

| File | Holds | Permissions |
|---|---|---|
| `config.ini` | Layout, stream and recorder settings, keys, camera list. **No secrets.** | normal |
| `secrets.ini` | Recorder password, optionally username | **readable only by your account** |

The easiest way to create both is `install.cmd` (it asks about your camera system, cameras, display and keys, and
reads the password with a hidden prompt). Templates are in `examples\` in the source tree and in
`%LOCALAPPDATA%\Programs\cctv-overlay\examples\` after installing. The program itself never writes these files.
`cctv-overlay check` validates both files offline and never prints the password.

Syntax rules (the same as the Linux version, which uses GLib's key-file format):

- Comments are **full lines** starting with `#`. Do **not** put a comment after a value on the same line:
  `width = 480  # pixels` makes the value `480  # pixels` and it is rejected.
- Section names are case-sensitive. Use `[camera.<id>]` for each camera; the order of these sections is the
  cycling order.
- Values are literal. Do not quote them and do not use escape sequences.
- Unknown sections or keys are errors, so typos are caught. Errors name the file, section and key.
- UTF-8, with or without a byte-order mark; Windows (CRLF) or Unix line endings.

Apply changes with `cctv-overlay reload`. An invalid file is rejected and the previous configuration keeps running.
A `config.ini` from the Linux version works unchanged.

## `config.ini`

### `[general]`

| Key | Default | Values | Notes |
|---|---|---|---|
| `log_level` | `info` | `debug`, `info`, `warning`, `error` | `debug` is verbose. Logs are redacted but still review them before sharing (see [troubleshooting](troubleshooting.md#sharing-logs)). |
| `monitor` | `focused` | `focused`, `primary`, or a display name such as `DISPLAY2` | Where the overlay appears. |
| `start_hidden` | `true` | boolean | Accepted but has no effect yet: the daemon always starts hidden. |

`monitor` values:

- `DISPLAY2` (also `\\.\DISPLAY2` or just `2`): the overlay always appears on that display. `install.cmd` lists
  your displays. If it does not exist, a warning is logged and the focused display is used.
- `focused`: the display of the window you are working in when the overlay opens (or the one under the mouse).
- `primary`: your main display (Settings > System > Display > "Make this my main display").

### `[overlay]`

| Key | Default | Values | Notes |
|---|---|---|---|
| `width` | `480` | 64–7680 | Logical pixels: scaled by the display's scaling (150 % → 1.5×). |
| `height` | `270` | 64–4320 | Logical pixels. |
| `anchor` | `top-right` | `top-left`, `top-right`, `bottom-left`, `bottom-right` | Screen corner. |
| `margin_x` | `24` | 0–10000 | Distance from the left or right edge, whichever is anchored. |
| `margin_y` | `24` | 0–10000 | Distance from the top or bottom edge. Edges are those of the work area, so the overlay never covers the taskbar. |
| `layer` | `overlay` | `overlay`, `top` | Keep `overlay`. See below. |
| `show_label` | `true` | boolean | Camera name chip at the top left. |
| `show_status` | `true` | boolean | Status dot, and the status line shown while not live. |
| `opacity` | `1.0` | number | Accepted but **not applied** yet. |

#### Layer: `overlay` or `top`

Both are always-on-top windows. `overlay` (the default) also re-asserts its place once a second while visible, so
it stays above other always-on-top windows (the taskbar, other overlays, some games' borderless windows). `top`
skips that and can end up below other always-on-top windows.

### `[stream]`

| Key | Default | Values | Notes |
|---|---|---|---|
| `transport` | `tcp` | `tcp`, `udp` | RTSP transport. TCP is the stable default. UDP may lower latency but can drop packets on a busy LAN (and may be blocked by Windows Firewall). |
| `subtype` | `1` | `0`–`3` | Default Dahua stream: `0` = main, `1` = sub stream. Overridable per camera. |
| `hwdec` | `d3d11va` | `d3d11va`, `d3d11va-copy`, `dxva2`, `dxva2-copy`, `nvdec`, `nvdec-copy`, `auto-safe`, `auto-copy`, `no` | Hardware decode. `d3d11va` is zero-copy and falls back to software when the hardware path is unusable. `vaapi` / `vaapi-copy` (Linux values) are accepted and mean `d3d11va` / `d3d11va-copy`. |
| `reconnect` | `true` | boolean | Retry with backoff after an error or stall. |
| `audio` | `false` | boolean | Camera audio when the daemon starts. Toggled at runtime with `cctv-overlay audio` (or `audio on`/`off`); a speaker icon shows while it is on. Only the single-camera view plays audio. The camera's audio must be enabled in the recorder's encoding settings. |
| `preview_snapshot` | `true` | boolean | Accepted but has no effect yet (no snapshot preview in this build). |
| `preload_next` | `false` | boolean | Accepted but has no effect yet (no preloading). |

### `[recorder]`

| Key | Default | Notes |
|---|---|---|
| `host` | *(none, required)* | Recorder address, for example `192.168.1.100`. Required unless every camera has a `url` or `host`. |
| `port` | `554` | RTSP port, 1–65535. |
| `username` | *(empty)* | Recorder user. Prefer `secrets.ini` or `CCTV_OVERLAY_USERNAME`. |
| `path` | `/cam/realmonitor` | RTSP path. Without `{placeholders}` (the default) the Dahua query `?channel=N&subtype=S` is appended. With placeholders it is a template, see below. |

The **password is not a key here.** It is rejected in `config.ini`. Put it in `secrets.ini` (see below).

#### URL templates (other camera systems)

The stream URL is `rtsp://USER:PASSWORD@host:port` + the expanded `path`. Credentials are always inserted by the
program from `secrets.ini`; they never appear in `config.ini`. Placeholders:

| Placeholder | Value |
|---|---|
| `{channel}` | camera channel (`3`) |
| `{channel02}` | channel with two digits (`03`) |
| `{subtype}` | `0` main stream, `1` sub stream |
| `{stream}` | `1` main stream, `2` sub stream |
| `{mainsub}` | `main` or `sub` |

| System | `path` |
|---|---|
| Dahua, Amcrest, Lorex | `/cam/realmonitor` (default) |
| Hikvision, HiLook, Annke NVR | `/Streaming/Channels/{channel}0{stream}` |
| Reolink NVR | `/h264Preview_{channel02}_{mainsub}` |
| Uniview NVR | `/unicast/c{channel}/s{subtype}/live` |

Unknown placeholders are configuration errors.

### `[keys]` (Windows)

Global keys handled by the background process (on Linux this is your compositor's key-binding file). They work
while the daemon runs; `install.cmd` starts it at sign-in.

| Key | Runs | Installer default |
|---|---|---|
| `toggle` | `cctv-overlay toggle` | `Ctrl + Num *` |
| `next` | `cctv-overlay next` | `Num *` |
| `prev` | `cctv-overlay prev` | `Num /` |
| `audio` | `cctv-overlay audio` | `Ctrl + Num -` |

The easiest way to choose keys is `install.cmd`: it asks you to **press** each combination and checks that no other
program already uses it (if you keep your existing setup, it offers to change only the keys).

Syntax: `MODIFIERS + KEY`, written the way you would say it. Modifiers: `Ctrl`, `Alt`, `Shift`, `Win` (also
`CTRL`, `SUPER`, …). `none` or an empty value leaves the action unbound. Everything is case-insensitive.

| Keys | Write |
|---|---|
| Number pad | `Num *`, `Num /`, `Num -`, `Num +`, `Num .`, `Num 0` … `Num 9` (also `Numpad *`, or the Hyprland names `KP_Multiply`, `KP_Divide`, `KP_Subtract`, `KP_Add`, `KP_0`…) |
| Letters, digits | `A` … `Z`, `0` … `9` |
| Symbols | `-`, `=`, `,`, `.`, `/`, `;`, `'`, `` ` ``, `[`, `]`, `\` (also `minus`, `equal`, `comma`, …) |
| Function keys | `F1` … `F24` |
| Others | `Space`, `Tab`, `Enter`, `Esc`, `Insert`, `Delete`, `Home`, `End`, `Page Up`, `Page Down`, `Up`, `Down`, `Left`, `Right`, `Pause`, `Scroll Lock`, `Print Screen`, `Play/Pause`, `Next Track`, `Previous Track`, `Stop Media` |

Examples: `Ctrl + Shift + -`, `Ctrl + Num *`, `Alt + F9`, `Win + F10`, `Ctrl + Alt + C`. `Num -` (number pad) and
`-` (the key next to `0`) are different keys. `cctv-overlay check` shows how each key was understood.

- A key without Ctrl, Alt or Win stops working in every other program while cctv-overlay runs (number-pad and
  function keys are the usual exception people accept; the installer warns before taking a typing key such as `-`).
- A combination another program already registered cannot be used; the daemon logs
  `cannot use key '…': it is already used by another program`, and the installer shows it.
- Many `Win + …` combinations are reserved by Windows.
- `cctv-overlay reload` re-registers the keys.

### `[camera.<id>]`

One section per camera. `<id>` is a short identifier you choose (letters, digits, `_`), used by `show <id>`.

| Key | Required | Notes |
|---|---|---|
| `name` | yes | Display name, unique (case-insensitive). Used by `show <name>`. |
| `channel` | yes, unless `url` or `path` is set | Recorder channel number, starting at 1. Check it on your recorder. |
| `subtype` | no | Overrides `[stream] subtype` for this camera. |
| `host` | no | This camera's own address (separate IP cameras without a recorder). Overrides `[recorder] host`. |
| `port` | no | This camera's RTSP port. Overrides `[recorder] port`. |
| `path` | no | This camera's RTSP path, used **literally** (no Dahua query is appended); placeholders allowed. With `path` set, `channel` is optional. Examples: Hikvision camera `/Streaming/Channels/10{stream}`, Reolink `/h264Preview_01_{mainsub}`, TP-Link Tapo `/stream{stream}`, Dahua/Amcrest camera `/cam/realmonitor?channel=1&subtype={subtype}`. |
| `url` | no | Full `rtsp://` URL. Bypasses the recorder builder. **Discouraged**: the URL would contain the password. Use only for non-secret sources. |

```ini
[camera.front]
name = Front Door
channel = 1

[camera.driveway]
name = Driveway
channel = 2
subtype = 0
```

Separate IP cameras (no recorder), sharing one user name and password:

```ini
[recorder]
username = viewer

[camera.porch]
name = Porch
host = 192.168.1.21
path = /Streaming/Channels/10{stream}
```

### `[grid]`

The grid (overview) view shows up to four cameras in a 2×2 layout. When it is available it is the view `toggle`
always opens on, and `next`/`prev` cycle **grid → camera 1 → … → camera N → grid** (both ways, wrapping). Each tile
is its own stream with its own reconnect logic, so one offline camera does not affect the others; the overlay state
is `live` while any tile is live.

| Key | Default | Notes |
|---|---|---|
| `enabled` | `true` | `false` restores the plain camera ring (toggle opens the current camera). The grid also needs at least 2 cameras. |
| `cameras` | first four | Comma-separated camera ids or names, 2–4, in tile order (top-left, top-right, bottom-left, bottom-right). |
| `subtype` | per camera | Stream for the grid tiles, e.g. `1` to use the lighter sub streams for the overview only. |

The grid never plays audio. Four streams decode at once, so hardware decoding is strongly recommended; with the main
stream expect roughly four times the bandwidth of one camera.

## `secrets.ini`

```ini
# Recorder credentials. Readable only by you. Never commit this file.
[recorder]
password = your-recorder-password
# username = viewer
```

- Values are literal: no quotes, no escapes. A `#` inside a password is part of the password, but a comment must
  still be on its own line. Leading and trailing spaces are trimmed.
- The file should be readable only by your account. If another account (for example `BUILTIN\Users`) can read it,
  the daemon logs a warning naming the file and those accounts. `install.cmd --set-password` always writes it with
  the right permissions.
- Environment variables override the file: `CCTV_OVERLAY_PASSWORD` and `CCTV_OVERLAY_USERNAME`. Prefer the file
  (environment values are visible to other programs you run).
- Never pass the password on a command line (other programs can read command lines). cctv-overlay never accepts
  it that way.

To create it by hand with the right permissions (PowerShell):

```powershell
$f = "$env:APPDATA\cctv-overlay\secrets.ini"
Copy-Item "$env:LOCALAPPDATA\Programs\cctv-overlay\examples\secrets.example.ini" $f
icacls $f /inheritance:r /grant:r "${env:USERNAME}:F"
notepad $f   # replace the placeholder password
```

## Complete minimal example

```ini
[general]
monitor = DISPLAY1

[overlay]
width = 480
height = 270
anchor = top-right

[recorder]
host = 192.168.1.100
port = 554
username = viewer

[keys]
toggle = Ctrl + Num *

[camera.front]
name = Front Door
channel = 1
```

Pair it with `secrets.ini` containing `password`.

## Start-up latency: main stream vs sub stream

How quickly a camera appears is dominated by the recorder, not by this program. Measured on one Dahua recorder
(HEVC, wired LAN) with the Linux version, toggle to first picture:

| `subtype` | Time to picture | Cost while visible |
|---|---|---|
| 1 (sub stream) | about 2.7 s (the recorder starts the sub stream on demand) | low |
| 0 (main stream) | about 0.8 s | about 4–5× the bandwidth |

If the overlay is slow to appear, try `subtype = 0` (globally in `[stream]`, or per camera). Nothing runs while the
overlay is hidden, so the extra cost applies only while it is visible. Measure your own recorder with
`CCTV_OVERLAY_MPV_TRACE` (see [troubleshooting](troubleshooting.md)).
