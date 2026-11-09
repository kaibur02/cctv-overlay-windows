# Security policy

## Reporting a vulnerability

Please report security problems **privately** through GitHub's
[private vulnerability reporting](../../security/advisories/new) for this repository, not in a public issue.
Include the version (`cctv-overlay --version`), what an attacker can do, and steps to reproduce. Do not include
real camera addresses, credentials or footage.

## What the project protects

- **Recorder credentials.** The password lives only in `%APPDATA%\cctv-overlay\secrets.ini` (permissions: your
  account only) or the `CCTV_OVERLAY_PASSWORD` environment variable. It is never accepted in `config.ini`, on a
  command line, in an installer answers file, in status output or in logs (all log and error text is redacted). The
  installer reads it from a hidden console prompt or a password box only.
- **Local control only.** The daemon listens on a named pipe whose access list allows only your account and denies
  network logons; clients are checked by impersonation, and the client verifies the pipe's owner. It opens no
  network listener; its only network traffic is the RTSP connection to your cameras.
- **No input capture.** The overlay never takes keyboard focus and is click-through. Global keys are registered with
  `RegisterHotKey` (no keyboard hook; no other keystrokes are seen).

## Known limits

- RTSP is unencrypted. Use a dedicated view-only account on a trusted network.
- While the daemon runs, the password is in its memory (its heap is excluded from Windows Error Reporting dumps).
- A per-camera `url =` override can contain credentials in `config.ini`; this is discouraged and warned about.
- Any program running as your account can read `secrets.ini` and talk to the control pipe; that is the same trust
  boundary as the Linux version.

Details: [`docs/behaviour.md`](docs/behaviour.md#security-model).
