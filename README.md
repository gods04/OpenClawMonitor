# OpenClaw Monitor

OpenClaw Monitor is a native Windows WPF desktop monitor with a black, compact, btop-inspired interface.

It is not a web app. The build output is a double-clickable Windows `.exe`.

## Features

- Local Windows CPU and memory monitoring
- NVIDIA GPU utilization, temperature, VRAM, and power through `nvidia-smi`
- CPU package power when Windows exposes a compatible power counter, otherwise `N/A`
- Ubuntu LAN monitoring over SSH
- Simple remote setup: `user@host`, password, and LM Studio local API URL
- LM Studio monitor using the official local API/CLI direction
- Manual refresh intervals: `500ms`, `1000ms`, `2000ms`, `5000ms`, `10000ms`
- Auto mode that slows polling when the remote host is offline or the window is in the background
- Responsive panel grid that adjusts between one, two, and three columns

## Build

On this machine the project builds with the Windows .NET Framework compiler, so a full .NET SDK is not required.

```powershell
.\build.ps1
```

The script downloads SSH.NET from NuGet when needed for password-based SSH.

Output:

```text
bin\OpenClawMonitor.exe
```

## Configure

The setup panel intentionally keeps the MVP simple:

- `REMOTE`: for example `gods@192.168.0.9`
- `PASS`: the Ubuntu user's SSH password
- `LM API`: for example `http://localhost:1234` or another LAN URL

No LM Studio token is required by default. If your LM Studio server later enables API token authentication, token support can be brought back as an advanced setting.

Local app settings are stored under:

```text
%APPDATA%\OpenClawMonitor\settings.json
```

Do not commit real passwords, SSH keys, or local config files.

## LM Studio Notes

Official docs checked while implementing this monitor:

- https://lmstudio.ai/docs/developer/core/server
- https://lmstudio.ai/docs/developer/rest/list
- https://lmstudio.ai/docs/developer/rest/chat
- https://lmstudio.ai/docs/cli/local-models/ps
- https://lmstudio.ai/docs/cli/serve/log-stream

The app currently uses:

- `/api/v1/models` with `/api/v0/models` fallback for loaded model state
- `lms ps --json` as a best-effort processing hint
- `lms log stream --source model --filter output --json --stats` for token/sec and token usage stats when available

## License

MIT License. See [LICENSE](LICENSE).
