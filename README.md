# OpenClaw Monitor

Native Windows WPF monitor with a black btop-style interface.

## Build

This workspace currently builds with the Windows .NET Framework compiler:

```powershell
.\build.ps1
```

Output:

```text
bin\OpenClawMonitor.exe
```

## Runtime notes

- Windows CPU and memory are read locally.
- NVIDIA metrics use `nvidia-smi` when available.
- CPU package power is shown only if Windows exposes a compatible Power Meter performance counter; otherwise it stays `N/A`.
- Ubuntu LAN monitoring uses `ssh.exe` with the configured key path and runs a short Python probe over stdin.
- LM Studio monitoring targets official LM Studio local APIs and CLI:
  - REST server default: `http://localhost:1234`
  - Models: `/api/v1/models`, with `/api/v0/models` fallback
  - Token stats: `lms log stream --source model --filter output --json --stats`
  - Processing hint: `lms ps --json` when available

Official docs checked:

- https://lmstudio.ai/docs/developer/core/server
- https://lmstudio.ai/docs/developer/rest/list
- https://lmstudio.ai/docs/developer/rest/chat
- https://lmstudio.ai/docs/cli/local-models/ps
- https://lmstudio.ai/docs/cli/serve/log-stream
