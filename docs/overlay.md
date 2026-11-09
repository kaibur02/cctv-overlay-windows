# Overlay window and video engine (Windows)

This page describes how the Windows edition works and how each part maps to the Linux design
([cctv-overlay](https://github.com/kaibur02/cctv-overlay), `ARCHITECTURE.md`).

```
┌──────────────────────────── cctv-overlayw.exe daemon ─────────────────────────────┐
│ Windows message loop (UI thread)                                                  │
│                                                                                   │
│  ┌────────────────┐      ┌──────────────────┐      ┌───────────────────────────┐  │
│  │ Control server │─────▶│ App: views,      │─────▶│ Overlay                   │  │
│  │ named pipe     │      │ per-tile state   │      │ video window (tiles)      │  │
│  │ JSON lines     │◀─────│ machine, backoff │◀─────│ + HUD window (labels)     │  │
│  └────────────────┘      └────────┬─────────┘      └────────────┬──────────────┘  │
│  ┌────────────────┐               │                             │                 │
│  │ Global keys    │──────────────▶│                ┌────────────▼──────────────┐  │
│  │ RegisterHotKey │               │                │ Video engine (libmpv)     │  │
│  └────────────────┘      ┌────────▼─────────┐      │ one player per tile, wid  │──┼──▶ RTSP
│  ┌────────────────┐      │ Camera list,     │─────▶│ embedding, D3D11/D3D11VA  │  │
│  │ Config/secrets │─────▶│ URL builder      │      └───────────────────────────┘  │
│  └────────────────┘      └──────────────────┘                                     │
└───────────────────────────────────────────────────────────────────────────────────┘
          ▲ \\.\pipe\cctv-overlay-<SID>  (your account only, no network)
┌─────────┴──────────────────┐
│ cctv-overlay.exe (CLI)     │ toggle | next | prev | grid | audio | show | hide | status | list | reload | quit
└────────────────────────────┘
```

## Component mapping

| Linux (C, GLib/GTK 4) | Windows (C# 5, .NET Framework 4.8, Win32) |
|---|---|
| `main.c`: CLI, daemon, `check` | `Program.cs` (same commands, options, exit codes, messages) |
| `app.c`: views, tiles, backoff, IPC handler | `App.cs` (line-by-line port; same states, timings and JSON) |
| `config.c` with GKeyFile | `Config.cs` with a GKeyFile-compatible reader (same validation and messages) plus `[keys]` |
| `camera.c` | `Camera.cs` (URL built as wipeable bytes) |
| `logging.c` | `Logging.cs` (same redaction rules) |
| `ipc.c`: Unix socket 0600, `SO_PEERCRED`, `flock` | `Ipc.cs`: named pipe with an ACL for your account only (network denied), peer check by impersonation, server-ownership check in the client, named mutex for one daemon |
| `overlay_gtk.c`: layer-shell surface, empty input region | `Overlay.cs`: layered, click-through, no-activate topmost tool windows |
| `video_mpv.c`: libmpv render API into a `GtkGLArea` | `VideoMpv.cs`: libmpv `wid` embedding into a child window per tile |
| Hyprland key bindings (installer-managed block) | `Hotkeys.cs`: `RegisterHotKey`, configured in `[keys]` |
| systemd user unit / auto-spawn | `HKCU\…\Run` entry for `cctv-overlayw.exe daemon` / auto-spawn |
| `RLIMIT_CORE=0` | `WerSetFlags(NOHEAP)` + no crash dialog |
| `malloc_trim` after hide | GC + working-set trim after hide |
| `install.sh` | `install.ps1` / `install.cmd` |
| `make check` (stub video/overlay) | `build.ps1 check` (same stub knobs; ported unit and IPC tests) |

## The overlay window

Two top-level windows, created when the overlay opens and destroyed when it hides (nothing stays mapped while
hidden):

- The **video window** is black, with rounded corners (an 8 px window region). It holds one child window per tile:
  one filling the frame, or a 2×2 grid with 2 px gaps (empty cells stay black).
- The **HUD window** is owned by the video window, so Windows keeps it directly above. It is a per-pixel-alpha
  layered window drawn with GDI+: per tile a chip with the status dot (amber connecting/reconnecting, green live,
  red error) and the camera name, a status line while not live (`Connecting…`, `Reconnecting… <reason>`,
  `Error: <reason>`), and the speaker icon in the top-right corner while audio is on (crossed out without an audio
  track, dimmed with a cross in the grid). Sizes and colours follow the Linux CSS.

Both windows use:

| Style | Effect |
|---|---|
| `WS_EX_TOPMOST` | above normal windows; with `layer = overlay` the z-order is re-asserted every second while visible |
| `WS_EX_LAYERED` + `WS_EX_TRANSPARENT` | click-through: hit-testing skips the window, mouse input reaches the program underneath |
| `WS_EX_NOACTIVATE`, `WM_MOUSEACTIVATE → MA_NOACTIVATE`, shown without activation | never takes focus; the keyboard stays where it was |
| `WS_EX_TOOLWINDOW` | no taskbar button, no Alt+Tab entry |

Placement: the display from `monitor` (`focused` = the display of the foreground window), the corner from `anchor`,
margins from the edge of that display's **work area** (so the overlay never covers the taskbar). Sizes are logical
pixels, scaled by the display's DPI (the program is per-monitor DPI aware).

## The video engine

One libmpv player per tile (up to four), each with its own session, error classification and stall watchdog:

- A fresh child window per session is passed to mpv as `wid`; mpv creates its D3D11 video window inside it.
  `d3d11-flip=no` is set because flip-model swap chains do not present into layered windows.
- Options as on Linux: `profile=low-latency`, `cache=no`, `demuxer-readahead-secs=0.2`,
  `demuxer-lavf-o=fflags=+nobuffer,timeout=5000000`, `rtsp-transport`, `hwdec` (default `d3d11va`, zero-copy),
  `aid=no` unless audio is on, no config files, scripts, OSC, OSD, input or subtitles. Windows-only additions:
  `taskbar-progress=no`, `stop-screensaver=no` (the overlay must not keep the screen awake), `input-cursor=no`.
- First frame = mpv's playback-restart event. Stall watchdog: no first frame within 15 s, or `time-pos` not
  advancing for 10 s while live.
- Errors are classified from mpv/FFmpeg messages into the same user messages as on Linux (authentication, not found,
  refused, unreachable), including the numeric errno messages FFmpeg prints on Windows.
- The credentialed URL exists only as a byte buffer passed to `loadfile`, then zeroed.
- Teardown runs `mpv_terminate_destroy` on a worker thread while the UI thread keeps pumping messages (mpv's video
  window is a child of ours, and closing it sends window messages to our thread). The tile's child window is
  parked in a hidden window until mpv has released it, then destroyed. Typical teardown: 10–60 ms.

## Verified on Windows 11

`build.ps1 smoke` runs the real overlay and libmpv with four synthetic streams and checks: every tile reports a
first frame and shows video (pixel capture), `WindowFromPoint` at the overlay's centre returns the window
underneath (click-through), the foreground window is unchanged (no focus), an unreachable RTSP URL reports an error,
and every player shuts down after `hide`. `build.ps1 check` runs 48 unit tests and 96 end-to-end checks of the CLI
and control pipe with the stub engine.

## Demo cameras and media

The demo build (`build.ps1 demo`, `/define:DEMO`) additionally accepts `url = demo:<scene>`
(`front`, `driveway`, `backyard`, `garage`, `alley`). `tools/demo/DemoScenes.cs` turns a scene into a libavfilter
graph (flat shapes, moving people and cars, sensor noise, an NVR-style timestamp, a quiet audio track) that libmpv
plays through its `lavfi` input, so the demo needs no RTSP server. Normal builds reject that URL. `run-demo.ps1` runs
a separate daemon with its own pipe, config and log, and `record-media.ps1` uses `tools/demo/Director.cs` to record
`docs/media`: a full-screen backdrop, the overlay driven through its commands with key captions, and the installer
in a throwaway profile, answered by typing and real key presses; encoding uses libmpv's encoder.
