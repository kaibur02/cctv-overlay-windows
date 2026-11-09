# Troubleshooting

Start with:

```bat
cctv-overlay check
cctv-overlay status
```

`check` validates the configuration offline and says whether a username and password were found. `status` shows the
state of every tile and the last error.

## Logs

- The background daemon (started at sign-in, by a key, or by any command) logs to
  `%LOCALAPPDATA%\cctv-overlay\daemon.log` (the previous log is `daemon.log.1`).
- For a live log, stop it and run it in a terminal:

  ```bat
  cctv-overlay quit
  cctv-overlay daemon
  ```

  Then press your keys or run commands in a second terminal. Set `log_level = debug` in `[general]` for more detail
  (`cctv-overlay reload` applies it).

### Sharing logs

Logs are redacted (URLs with credentials, Authorization headers and your password become `***`), but still read
them before posting: they contain your recorder's address and camera names.

## Common problems

| Symptom | Cause and fix |
|---|---|
| `overlay unavailable: libmpv-2.dll was not found` | Run `install.cmd` again (it downloads libmpv), or copy `libmpv-2.dll` next to `cctv-overlay.exe`. |
| The keys do nothing | The daemon is not running (`cctv-overlay ping` starts it), or the key is taken: look for `cannot use key` in the log and choose another key (re-run `install.cmd`, keep your setup and change the keys; or edit `[keys]` and run `cctv-overlay reload`). Many `Win + …` keys are reserved by Windows. Number-pad digits (`Num 0`…`Num 9`) only work with NumLock on. |
| A key stopped working in another program | A key without Ctrl, Alt or Win (such as `Num *`) is taken from every other program while cctv-overlay runs. Add a modifier or set it to `none`. |
| The installer window closed | It now stays open with the result when started by double-click. If it still closes, open a Command Prompt in the folder and run `install.cmd` there to see the message. |
| `error: no recorder password configured` | Run `install.cmd --set-password`. |
| `authentication failed` | Wrong user name or password. Check the user exists on the recorder and is allowed to view live video, then `install.cmd --set-password`. Some recorders lock an account after several failed logins. |
| `stream not found (check channel and path)` | Wrong channel number or path template for your system. Check the channel numbers in the recorder's channel list. |
| `connection refused` / `recorder unreachable` / `could not open the stream` | Wrong address or port, RTSP disabled on the recorder, a firewall in between, or the recorder is offline. Try the same URL in VLC (Media > Open Network Stream) to confirm. |
| `no video received (timed out)` | The recorder accepted the connection but sent no picture within 15 s. Try `subtype = 0`, or `transport = tcp` if you set `udp`. |
| `stream stalled (no video for 10 s)` | The camera stopped sending; the tile reconnects automatically (5 attempts, then `error`; `toggle` retries). |
| One tile is black with "Reconnecting…" | That camera is offline or misconfigured; the others keep running. `cctv-overlay status` names it. |
| The overlay is on the wrong display | Set `[general] monitor` to `primary`, `focused` or a display name (`install.cmd` lists them), then `cctv-overlay hide` and `toggle`. The position is chosen each time the overlay opens. |
| The overlay is hidden behind a game | The game uses exclusive fullscreen. Switch it to borderless or windowed fullscreen. |
| The overlay is too small/large | `width`/`height` are logical pixels and scale with the display's scaling (Settings > System > Display > Scale). |
| Video is choppy, high CPU | Hardware decoding is not in use. The log shows `decoder '…'` per tile: `d3d11va` is hardware, `no` is software. Update the GPU driver; try `hwdec = d3d11va-copy` or `auto-safe`. With four main streams, use `[grid] subtype = 1`. |
| `secrets file … can be read by other accounts` | Run `install.cmd --set-password`, which rewrites the file readable only by you. |
| `another cctv-overlay daemon is already running` | Only one daemon runs per user. `cctv-overlay quit` stops it. |
| `cannot start the daemon` / did not start within 2 s | Run `cctv-overlay daemon` in a terminal to see the reason. |
| Antivirus blocks the program | It is built on your own machine from the source in this repository. Allow `%LOCALAPPDATA%\Programs\cctv-overlay\`. |

## Measuring start-up latency

`CCTV_OVERLAY_MPV_TRACE=1` (or `trace`) logs mpv's verbose messages with millisecond offsets, through the redactor.
It is for development only: treat the output as sensitive.

```bat
cctv-overlay quit
set CCTV_OVERLAY_MPV_TRACE=1
cctv-overlay daemon
```
