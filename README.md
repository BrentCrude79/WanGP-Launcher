# WanGP Launcher

A small Windows system-tray launcher for [WanGP (Wan2GP)](https://github.com/deepbeepmeep/Wan2GP) by deepbeepmeep.

WanGP holds a lot of RAM and VRAM while it runs. This launcher keeps it **off until it is needed**: the tray listens on
WanGP's ports, starts WanGP when someone opens the web UI or an AI agent calls one of its MCP tools, forwards the traffic
to it, and stops it again after 10 idle minutes. WanGP runs without a console window, and its output is shown in a log
window you can open from the tray.

> This is an unofficial companion tool and is not affiliated with the WanGP project.

## Features

- **On-demand web UI.** Open `http://localhost:7860` (or `http://<this-pc>:7860` from the LAN) and WanGP starts. A
  "starting" page is shown until it is ready.
- **On-demand MCP server.** Port `7861` serves WanGP's MCP server for coding agents (for example Claude Code). The first
  tool call starts WanGP in MCP mode.
- **Idle stop.** After 10 minutes with no requests and no generation running, WanGP is stopped to free memory. A running
  generation is never interrupted.
- **Keep MCP server running.** Optional tray toggle that keeps WanGP up in MCP mode with no idle stop.
- **Keep WanGP updated.** Optional tray toggle that checks GitHub every 6 hours and fast-forwards the WanGP checkout
  when WanGP is not generating, then installs any changed requirements (with `uv` for uv environments, `pip` otherwise).
  "Update WanGP now" does the same on demand.
- **Install WanGP.** If no WanGP copy is found, the tray offers to install it: it clones the official repository and runs
  WanGP's own `setup.py install --auto`. It also offers to locate an existing copy instead.
- **Uses WanGP's own Python environment.** The active environment is read from WanGP's `envs.json` (uv, venv, conda or
  none), falling back to `.venv` in the WanGP folder.
- **Change WanGP folder**, **Start with Windows**, **Edit launch arguments**, **Open WanGP folder**, live console log,
  start/stop/restart from the menu.
- Tray icon colour shows the state: grey stopped, amber starting, green running.

## Sharing the MCP server online (optional)

Choose **Share MCP server online (Cloudflare tunnel)** in the tray menu when a cloud agent needs WanGP. This requires
`cloudflared`; install it with `winget install --id Cloudflare.cloudflared`. The launcher starts a Cloudflare quick
tunnel for the MCP port and copies its URL, including a secret path token, to the clipboard. The URL changes every time
the tunnel starts. Anyone who has the full URL can use WanGP while the tunnel is on, so turn it off as soon as the
agent is finished.

Cloudflare quick tunnels have no uptime guarantee. Cloudflare also documents that quick tunnels do not support
Server-Sent Events, so streaming MCP responses may not work through them.

## Requirements

- Windows 10 or 11 with .NET Framework 4.x (included with Windows).
- An existing WanGP install, or, to let the launcher install it:
  - [Git for Windows](https://git-scm.com/download/win), and
  - [uv](https://docs.astral.sh/uv/getting-started/installation/) (recommended) or
    [Python 3.11+](https://www.python.org/downloads/) on `PATH`.
- Plenty of disk space: roughly 50 GB for WanGP itself plus tens to hundreds of GB for models.

The launcher never installs Git, uv or Python itself.

## Download

Get `WanGP-Launcher.exe` from the [latest release](https://github.com/BrentCrude79/WanGP-Launcher/releases/latest), put it anywhere and run it.
It is a single self-contained file. It is not code-signed, so SmartScreen may ask you to confirm on first run.

## Build

No SDK or Visual Studio is needed. The script uses the C# compiler that ships with .NET Framework:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1
```

This writes `WanGP Launcher.exe` next to the script. Run it; it appears in the notification area. Only one copy runs at
a time; starting it again opens the log window.

The source is a single file, `WanGPLauncher.cs`, limited to C# 5 so it compiles with the built-in compiler.

## Finding WanGP

At startup the launcher looks for a folder containing `wgp.py`, in this order:

1. The folder saved in the registry (`HKCU\Software\WanGP Launcher`, value `WanGPFolder`).
2. The launcher's own folder and its parent.
3. `%USERPROFILE%\Wan2GP`, `%USERPROFILE%\Apps\Wan2GP`, `%USERPROFILE%\Apps\WanGP`.

Whatever it finds is saved to the registry. Use **Change WanGP folder...** in the tray menu to switch.

## Ports and launch arguments

| Port | Purpose |
|---|---|
| 7860 | WanGP web UI (set by `--server-port` in the launch arguments) |
| 7861 | WanGP MCP server |
| public port + 10000 | Where WanGP itself listens (17860 / 17861); the tray forwards to it |

The arguments passed to Python are kept in `<WanGP folder>\launcher\launcher-args.txt` (created on first run; open it
with **Edit launch arguments**). The default is:

```
wgp.py --listen --server-port 7860 --perc-reserved-mem-max 0.25
```

`--listen` makes both ports reachable from the LAN; remove it to keep them on this PC only. Restart the tray to apply.

## Files the launcher writes

Per WanGP install, in `<WanGP folder>\launcher\`:

| File | Purpose |
|---|---|
| `launcher-args.txt` | Python launch arguments |
| `logs\console.log` | WanGP console output |
| `mcp-cache.json` | Cached MCP tool list, so agents can list tools without starting WanGP |
| `force-mcp.flag` | Present when "Keep MCP server running" is on |
| `auto-update.flag` | Present when "Keep WanGP updated" is on |

Install log (setup mode): `%APPDATA%\WanGP Launcher\logs\install.log`.
Start with Windows: `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, value `WanGPTray`.

## Directing WanGP with Claude Code

[`docs/WANGP_H3_ART_DIRECTION_HANDOFF.md`](docs/WANGP_H3_ART_DIRECTION_HANDOFF.md) is a handoff for an AI coding agent
such as Claude Code acting as an art director for MiniMax H3 video in WanGP. It covers story structure, continuity and
drift control, staging, camera, light, voice and sound direction, the H3 prompt formats, and WanGP's
paragraph-per-window (PW) prompts for longer videos.

To use it, give the agent the file and tell it where WanGP's MCP server is (for example `http://<this-pc>:7861/mcp`).
The agent discovers WanGP's tools from there, and the launcher starts WanGP on the first tool call.

## Other tools

While WanGP is working, the launcher sets the named event `Local\WanGP.Busy.Tray`. Another tray app (for example a local
LLM launcher) can watch it to unload its own model and free VRAM for WanGP.

## Command-line options

- `--show-log` opens the log window on start.
- Any other arguments are appended to the Python launch arguments.

## License

[GPL-3.0](LICENSE). Copyright (C) 2026 BrentCrude79.
