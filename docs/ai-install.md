# Installing with an AI assistant

This page is written **for an AI coding assistant** (Claude Code, Codex, …) that a user has asked to install
cctv-overlay on Windows. A person can follow it too. Hand your assistant this sentence:

> Install cctv-overlay from this repository by following `docs/ai-install.md` exactly.

---

## Instructions for the assistant

You install everything except the password. **Never ask the user for their camera/recorder password in chat,
never put it in a file, command line or environment variable you create, and never read
`%APPDATA%\cctv-overlay\secrets.ini`.** The user types it into the installer's password box themselves (step 4).

Run the commands from the repository folder. In PowerShell use `.\install.cmd`; in `cmd` use `install.cmd`.

### 1. Check the machine

```bat
install.cmd --check
```

- Exit 0: continue.
- Otherwise read the listed problem. Windows 10 1803+/11 64-bit with .NET Framework 4.7.2+ is required; there is
  nothing else to install. If the session is not an interactive desktop (a service, SSH), stop: the overlay needs
  the user's desktop.

### 2. Ask the user these questions (in chat)

Ask them together, offer the defaults, and accept "default" for anything they don't care about.

1. **Camera system**: one of `dahua` (also Amcrest, Lorex), `hikvision` (also HiLook, Annke), `reolink`, `uniview`,
   `standalone` (separate IP cameras, no recorder) or `custom` (they know the RTSP path).
2. Recorder **IP address** (not for `standalone`) and RTSP **port** (default 554).
3. Recorder **user name** (not the password). Suggest a view-only user.
4. **How many cameras**, and for each one: a **name** to show on screen and its recorder **channel number**
   (default: 1, 2, 3, …). For `standalone`: each camera's IP address and make (`dahua`, `hikvision`, `reolink`,
   `tapo`, `uniview`, or `custom` with a path).
5. **Stream**: `main` (sharper; on many NVRs it also appears faster) or `sub` (lighter). Default `main`.
6. **Display** for the overlay: `primary` (default), `focused` (follows the window they are working in), or a
   display name. List the names with:
   `powershell -c "Add-Type -A System.Windows.Forms; [Windows.Forms.Screen]::AllScreens | % DeviceName"`
   (use the part after `\\.\`, e.g. `DISPLAY2`).
7. **Size**: `480x270`, `600x338`, `720x405` (default) or `960x540`, and **corner**: `top-right` (default),
   `top-left`, `bottom-right`, `bottom-left`.
8. **Keys** for: show/hide the overlay, next camera, previous camera, audio on/off. Defaults: `Ctrl + Num *`,
   `Num *`, `Num /`, `Ctrl + Num -` (Num = number pad). Write them as people say them: `Ctrl + Shift + -`,
   `Alt + F9`, `Win + F10`. Mention that a key without Ctrl/Alt/Win stops working in other programs. `none` skips a
   key. You can check each one before writing the answers file:
   `%LOCALAPPDATA%\Programs\cctv-overlay\cctv-overlay.exe check-key Ctrl + Shift + -` (after step 3 has installed
   the program, or `build\cctv-overlay.exe` after `build.ps1`): exit 0 = fine, 2 = not a valid key (the message says
   why), 3 = another program already uses it.
9. Whether to **start cctv-overlay at sign-in** (default yes; the keys only work while it runs) and to **add it to
   PATH** (default yes).

If a configuration already exists (`%APPDATA%\cctv-overlay\config.ini`), ask whether to **keep** it (default) or
replace it (the installer backs it up). Keeping it skips questions 1–8.

### 3. Write the answers file and run the installer

Copy [`examples/install.answers.example`](../examples/install.answers.example) to a temporary file, fill it in with
the user's answers (it must not contain a password; the installer refuses one), then run:

```bat
install.cmd --answers C:\path\to\answers.txt --yes
```

With an answers file (or without a console) the installer never prompts. It builds, installs to
`%LOCALAPPDATA%\Programs\cctv-overlay`, downloads libmpv (`DOWNLOAD_MPV = yes`; about 30 MB from
github.com/shinchiro/mpv-winbuild-cmake, SHA-256 checked), writes `%APPDATA%\cctv-overlay\config.ini`, validates it,
sets up the sign-in start and PATH as answered, starts the daemon, reports keys that another program already uses,
and says what to do about the password.

Show the user the installer's final summary. On an error, read the message: it names the field to fix.

### 4. The user sets the password themselves

Run:

```bat
install.cmd --set-password
```

Without a console (as when you run it), this opens a **password box on the user's desktop** and waits. Tell the user
to type the password there (twice) and click **Save**; the box may open behind other windows. The password goes
straight from that box into `secrets.ini` (readable only by their account) and never passes through you. Do not try
to supply input to it. If the user prefers a terminal, they can run the same command in their own terminal window,
where it asks with hidden input.

### 5. Test

```bat
install.cmd --test
```

It opens the overlay on the 4-camera grid, waits for the cameras, prints one line per camera (`OK Front: live` or
`X Garage: error`) and closes the overlay. On failure, `cctv-overlay status` and
`%LOCALAPPDATA%\cctv-overlay\daemon.log` contain the reason; they never contain the password. Common fixes: wrong
channel numbers, RTSP disabled on the recorder, wrong user name or password (`install.cmd --set-password` again), or
a firewall.

### 6. Tell the user how to use it

- The **show/hide** key opens the overlay on the grid of the first four cameras.
- **Next/previous** step through the grid, then each camera on its own, then back to the grid.
- The **audio** key turns camera sound on and off. A small speaker icon appears in the corner while it is on. The
  grid view never plays sound.
- Change settings by running `install.cmd` again, or by editing `config.ini` and running `cctv-overlay reload`.
- Remove everything with `install.cmd --uninstall` (it asks before deleting the configuration).
