# OpenClaw Monitor

OpenClaw Monitor is a native Windows desktop system monitor with a compact black `btop`-style interface.

It is not a web dashboard. The goal is a double-clickable Windows `.exe` that monitors your local Windows PC, one SSH-accessible Linux machine, NVIDIA GPU telemetry, and LM Studio status in one terminal-like window.

## What It Shows

- Local Windows CPU, memory, network, processes, uptime, and best-effort CPU package power
- NVIDIA GPU utilization, temperature, VRAM, and power through `nvidia-smi`
- One remote Linux machine over SSH for CPU, memory, network, power, and processes
- Configurable remote machine name, so the second host can be called `GPU Machine`, `Xiaobai`, `Render Box`, or anything else
- LM Studio server status, active/loaded model hints, processing state, tokens/sec, and token counts when LM Studio exposes them
- btop-like refresh controls from `100ms` to `2000ms` in `100ms` steps, plus Auto mode

## Download And Run

For normal use, download the latest Windows zip from:

[github.com/gods04/OpenClawMonitor/releases](https://github.com/gods04/OpenClawMonitor/releases)

Then:

1. Unzip the release.
2. Keep `OpenClawMonitor.exe` and `Renci.SshNet.dll` in the same folder.
3. Double-click `OpenClawMonitor.exe`.
4. Open `settings` and fill in your remote machine and LM Studio URL.

If Windows SmartScreen appears, choose the normal "more info / run anyway" flow for unsigned open-source builds.

## Build From Source

Requirements:

- Windows 10 or Windows 11
- PowerShell
- .NET Framework compiler or .NET Framework developer tools
- Internet access on first build so `build.ps1` can download SSH.NET from NuGet

Build:

```powershell
.\build.ps1
```

Output:

```text
bin\OpenClawMonitor.exe
bin\Renci.SshNet.dll
```

Create a redistributable zip:

```powershell
.\package.ps1
```

Output:

```text
dist\OpenClawMonitor-<version>-win-x64.zip
```

## Setup

Open the in-app `settings` panel:

- `NAME`: display name for the second machine, for example `GPU Machine` or `Xiaobai`
- `REMOTE`: SSH target in `user@host` or `user@host:port` format, for example `gods@192.168.0.9`
- `PASS`: SSH password for that Linux user
- `LM API`: LM Studio local server URL, usually `http://localhost:1234`

The main dashboard uses the same `NAME` everywhere: CPU, memory, network, footer, and process-table source switching.

## Remote Linux Requirements

The remote machine should have:

- SSH enabled
- `python3`
- standard Linux `/proc` files
- `ps` for process listing

Remote power readings are best effort. The app tries common Linux power sources such as `powercap`/RAPL when available; otherwise power displays as `N/A`.

## LM Studio

Start LM Studio's local server, then set `LM API` to its base URL. No token is required for the default local LM Studio server.

Implementation currently checks the official LM Studio local API/CLI direction:

- `/api/v1/models`, with `/api/v0/models` fallback
- `lms ps --json`, when the CLI is installed
- `lms log stream --source model --filter output --json --stats`, when available, for tokens/sec and token counts

## Settings And Privacy

Local settings are stored here:

```text
%APPDATA%\OpenClawMonitor\settings.json
```

The SSH password is currently stored in that local settings file. Do not commit your real `settings.json`, passwords, SSH keys, or private hostnames.

## Project Layout

```text
src\Program.cs              WPF app, UI, collectors, settings
build.ps1                   Compile native Windows exe
package.ps1                 Build and create a release zip
THIRD-PARTY-NOTICES.md      Dependency notices
LICENSE                     MIT license
```

## License

MIT License. See [LICENSE](LICENSE).
