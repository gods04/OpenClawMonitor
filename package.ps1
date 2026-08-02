$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$bin = Join-Path $root "bin"
$dist = Join-Path $root "dist"
$stage = Join-Path $dist "OpenClawMonitor"

& (Join-Path $root "build.ps1")

New-Item -ItemType Directory -Force $dist | Out-Null
if (Test-Path $stage) {
    $resolvedDist = (Resolve-Path $dist).Path
    $resolvedStage = (Resolve-Path $stage).Path
    if (-not $resolvedStage.StartsWith($resolvedDist, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean unexpected stage path: $resolvedStage"
    }
    Remove-Item -LiteralPath $stage -Recurse -Force
}
New-Item -ItemType Directory -Force $stage | Out-Null

Copy-Item -LiteralPath (Join-Path $bin "OpenClawMonitor.exe") -Destination $stage -Force
Copy-Item -LiteralPath (Join-Path $bin "Renci.SshNet.dll") -Destination $stage -Force
Copy-Item -LiteralPath (Join-Path $root "README.md") -Destination $stage -Force
Copy-Item -LiteralPath (Join-Path $root "LICENSE") -Destination $stage -Force
Copy-Item -LiteralPath (Join-Path $root "THIRD-PARTY-NOTICES.md") -Destination $stage -Force

$version = "dev"
try {
    $gitVersion = git -C $root describe --tags --always --dirty 2>$null
    if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($gitVersion)) {
        $version = $gitVersion.Trim()
    }
}
catch {
}

$zip = Join-Path $dist ("OpenClawMonitor-" + $version + "-win-x64.zip")
if (Test-Path $zip) {
    Remove-Item -LiteralPath $zip -Force
}
Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip -Force
Write-Host "Packaged $zip"
