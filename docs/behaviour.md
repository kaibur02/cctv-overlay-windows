# cctv-overlay — behaviour

cctv-overlay is one program in two forms: `cctv-overlay.exe` (a console program: the CLI, and `daemon` in the
foreground) and `cctv-overlayw.exe` (the same program without a console window, used for the background daemon).
`cctv-overlay daemon` runs the long-lived process that owns the overlay window, the video players and the global
keys; every other command is a thin client that sends one request to the daemon over a named pipe. Video and
window details are in [`overlay.md`](overlay.md); this page covers commands, states and configuration semantics.

## Commands

| Command | Effect |
|---|---|
| `daemon [--config PATH] [--no-overlay]` | Run the daemon in the foreground (logs to the terminal). The daemon always starts hidden. |
| `toggle` | Hidden → open on the **grid** view (or on the current camera when there is no grid, see `[grid]`). Visible (connecting/live/reconnecting) → hide. In the error state, toggle retries instead of hiding; use `hide` to close. |
| `next` / `prev` | Cycle the views **grid → camera 1 → … → camera N → grid**, wrapping both ways (camera order = config-file order). While hidden they open the overlay on the grid. Without a grid they cycle cameras only, and while hidden open the neighbouring camera. |
| `grid` | Open or switch to the grid view. Fails when there is no grid (fewer than 2 cameras or `[grid] enabled = false`). |
| `audio [on\|off]` | Toggle (or set) camera audio. The setting lasts until the daemon stops (`[stream] audio` sets the start value). Audio plays only in the single-camera view; a speaker icon shows while it is on (dimmed with a cross in the grid, crossed out when the stream has no audio track). |
| `show [CAMERA]` | Open on CAMERA (name, case-insensitive; or id; or zero-based index). Without an argument, open on the current camera. If it is already showing, nothing happens (unless it is in error or reconnecting, in which case it retries now). |
| `hide` | Close the overlay from any state. Stops the streams, so nothing is decoded or connected while hidden. |
| `status [--json]` | State, camera, index, count, last error. Never prints credentials or URLs. |
| `list` | Configured cameras (`*` marks the current one). |
| `reload` | Re-read `config.ini` and `secrets.ini`, and re-register the keys. Keeps the current camera if it still exists (matched by id); otherwise selects the first one and reconnects only if visible. An invalid file is rejected and the old configuration stays active. |
| `quit` | Stop the daemon. |
| `ping` | Health check. Prints the daemon version (`--json` shows the raw reply). |
| `check` | Validate `config.ini` and `secrets.ini` offline (no daemon). Lists cameras, grid tiles and keys, and whether a username and password were found, never their values. Exit 0 ok, 1 credentials missing, 2 invalid configuration. |

Global options: `-c/--config PATH`, `--socket NAME` (control pipe name), `--no-spawn`, `--json`, `--version`,
`-h/--help`.

If the daemon is not running, any client command except `quit` starts it (`cctv-overlayw.exe daemon`, detached,
with only the config path on its command line) and waits up to 2 s for the pipe. `--no-spawn` disables that. A
background daemon writes its log to `%LOCALAPPDATA%\cctv-overlay\daemon.log` (rotated once above 512 KiB); for
live logs run `cctv-overlay daemon` in a terminal instead.

`install.cmd` registers `cctv-overlayw.exe daemon` to start at sign-in, because the global keys only work while the
daemon runs. Hidden, it holds no player, no network connection and no window except an invisible message window.

### States

`hidden → connecting → live`. Stream errors or stalls go to `reconnecting` (retries after 0.5, 1, 2, 4 and 8 s, at
most 5 attempts), then to `error`. The backoff is reset only after the stream has stayed live for 15 s, so a stream
that delivers one frame and drops cannot loop forever. `hide` always returns to `hidden`. If the stream cannot even
be started (for example no password configured), the state is `error` immediately, without retries.

A stream counts as stalled when no new video arrives for 10 s while live, or when no first picture arrives within
15 s of connecting (the RTSP socket timeout is 5 s).

### Exit codes

| Code | Meaning |
|---|---|
| 0 | Success |
| 1 | The command failed (unknown camera, invalid reload, a daemon is already running, overlay unavailable at daemon start, …) |
| 2 | Usage or configuration error |
| 3 | Daemon unavailable (not running and could not be started, or `--no-spawn`) |
| 4 | Timed out waiting for the daemon (2 s) |

### `status --json`

Example reply (values are illustrative):

```json
{"ok":true,"visible":true,"state":"live","view":"grid","camera":"Front Door","camera_index":0,
 "camera_count":5,"audio":false,"error":null,"retries":0,"uptime_ms":4210,
 "tiles":[{"camera":"Front Door","state":"live"},{"camera":"Driveway","state":"live"},
          {"camera":"Garden","state":"reconnecting"},{"camera":"Garage","state":"live"}],"version":"0.1.0"}
```

`view` is `grid` or `camera`; `camera` is the current single camera (the one `next` from the grid leaves for is
camera 1). `tiles` lists the visible tiles (one in the single view, empty while hidden). In the grid, `state` is the
best tile state (`live` if any tile is live) and `error` is the first tile error. `error` is `null` unless a stream
error is set. The reply never contains the RTSP URL, the username or the password. Replies are pure ASCII (other
characters are `\u` escapes), so `ConvertFrom-Json` reads them in any console code page.

## Configuration

The daemon reads `%APPDATA%\cctv-overlay\config.ini`; `--config` overrides it. All keys are described in
[`configuration.md`](configuration.md). Key points:

- Values are strictly validated. Errors name the file, section and key.
- Camera order is the order of the `[camera.<id>]` sections; names must be unique (case-insensitive).
- `channel` ≥ 1 is required unless the camera has an `rtsp://` `url` override or a `path`.
- `[recorder] host` has no default; it is required unless every camera has a `url` or `host`.
- Unknown sections and keys are errors (this catches typos). A `password` key in `config.ini` is an error on purpose.
- A `password` for the recorder lives in `secrets.ini` (or the environment), never in `config.ini`.

A missing password is **not** a startup error: the daemon, `list` and `status` work, and the error appears only when
a stream is about to start.

## Security model

- Credentials live only in `secrets.ini` (keep it readable only by you; a warning names the file and the other
  accounts that can read it) or in the environment (`CCTV_OVERLAY_PASSWORD`, `CCTV_OVERLAY_USERNAME`). They are
  never on a command line.
- The RTSP URL (with percent-encoded credentials) is built only at connect time, in a buffer that is wiped after
  libmpv copies it. It is never in status output, logs or error messages. Cameras resolved from recorder settings
  store no URL; an explicit `url =` override is stored as written (it may contain credentials and is discouraged;
  such credentials are registered with the redactor and a warning is logged).
- All log output and error replies pass through a redactor: `scheme://user:pass@` becomes `scheme://***:***@`,
  `Authorization:` headers become `Authorization: ***`, and the registered password (raw, percent-encoded and as
  Basic-auth base64) becomes `***`. Control characters are replaced.
- The control channel is the named pipe `\\.\pipe\cctv-overlay-<your account SID>`. Its access list allows only your
  account and denies network logons (named pipes can otherwise be reached over SMB). Every client is also checked
  by impersonation, and the client checks that the pipe belongs to your account before sending anything. Requests
  are one JSON line of at most 4 KiB with a 2 s timeout; at most 8 connections at once. There is no network listener.
- One daemon per pipe (a named mutex). A crashed daemon leaves nothing stale behind.
- The installer writes only under your profile (`%LOCALAPPDATA%\Programs\cctv-overlay`, `%APPDATA%\cctv-overlay`) and
  the per-user `Run` key and `PATH`. No administrator rights.
- The daemon excludes its heap from Windows Error Reporting crash dumps and turns off the crash dialog, so a crash
  does not write the password to a dump file. The password still exists in process memory while the daemon runs
  (.NET strings cannot be wiped). Trace mode (`CCTV_OVERLAY_MPV_TRACE=1|trace`) is developer-only and its output
  must be treated as sensitive even though the redactor removes URLs and Authorization headers.

## Settings that are accepted but do nothing yet

These keys are parsed and validated, but the current build does not act on them (the same as the Linux version):

- `[general] start_hidden`: the daemon always starts hidden.
- `[stream] preview_snapshot`, `[stream] preload_next`: no snapshot preview and no preload yet.
- `[overlay] opacity`: not applied.

## Test knobs

For development only. The stub build (`build.ps1 check`) reads `CCTV_OVERLAY_STUB_FAIL` (`1`, `always` or
`first:N`), `CCTV_OVERLAY_STUB_FAIL_SLOT`, `CCTV_OVERLAY_STUB_DELAY_MS`, `CCTV_OVERLAY_STUB_DROP_AFTER_MS`. Both builds
read `CCTV_OVERLAY_BACKOFF_SCALE` to shorten the reconnect delays, `CCTV_OVERLAY_LIVE_STABLE_MS`, and
`CCTV_OVERLAY_STATE_DIR` (where a background daemon writes its log). Do not rely on these in normal use.
