# Requirements

`install.cmd --check` tests all of this without changing anything.

## Computer

| | Minimum | Notes |
|---|---|---|
| Windows | 10 version 1803 or newer, or 11 | Tested on Windows 11 (build 26200). |
| Architecture | 64-bit: x64 or ARM64 | The installer downloads the matching libmpv. |
| .NET Framework | 4.7.2 or newer | Built into Windows 10 1803+ and Windows 11. Includes the C# compiler used to build. |
| `tar.exe` | Windows 10 1803+ | Only to unpack the libmpv download. |
| Session | your normal desktop | The overlay is a desktop window; it cannot run as a service or over SSH. |
| GPU | Direct3D 11 | Hardware decoding (D3D11VA) is strongly recommended for the 4-camera grid: Intel, AMD and NVIDIA GPUs from the last decade decode H.264 and most decode H.265 (HEVC). Without it, decoding falls back to the CPU. |
| Disk | about 100 MB | Mostly libmpv. |

No Visual Studio, SDK, Python or other tools are needed.

### Fullscreen programs

The overlay stays above normal, maximised and **borderless / windowed fullscreen** programs (most modern games
default to borderless; videos in browsers and media players are borderless). Games running in **exclusive
fullscreen** own the display, and Windows draws no other window over them; switch those games to borderless.

## Network and cameras

| | Requirement |
|---|---|
| Protocol | **RTSP** (H.264 or H.265), from an NVR/DVR or directly from IP cameras on your network. |
| Account | One user name and password for all cameras. Create a **view-only** user on the recorder if you can. |
| Firewall | Outbound TCP to the recorder's RTSP port (554 by default) and, with `transport = udp`, inbound UDP for the media. Windows Firewall allows outbound connections by default. |

### Camera systems

| System | Preset | RTSP path |
|---|---|---|
| Dahua, Amcrest, Lorex (Dahua-based) | `dahua` | `/cam/realmonitor?channel=N&subtype=S` (tested on a real Dahua recorder) |
| Hikvision, HiLook, Annke | `hikvision` | `/Streaming/Channels/{channel}0{stream}` |
| Reolink NVR | `reolink` | `/h264Preview_{channel02}_{mainsub}` |
| Uniview (UNV) | `uniview` | `/unicast/c{channel}/s{subtype}/live` |
| Separate IP cameras | `standalone` | per camera make: Dahua/Amcrest, Hikvision, Reolink, TP-Link Tapo/VIGI, Uniview, or custom |
| Anything else | `custom` | your own template |

Presets other than Dahua follow the vendors' documented RTSP paths but haven't been tested on real hardware yet.
RTSP usually has to be enabled in the recorder's network settings first. TP-Link Tapo cameras need a "camera
account" created in the Tapo app (Advanced settings).
