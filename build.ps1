$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src = Join-Path $root "src"
$bin = Join-Path $root "bin"
New-Item -ItemType Directory -Force $bin | Out-Null

$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) {
    $csc = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe"
}
if (-not (Test-Path $csc)) {
    throw "C# compiler not found. Install .NET Framework developer tools or a .NET SDK."
}

$framework = Split-Path -Parent $csc

function Find-Assembly([string]$name) {
    $frameworkCandidate = Join-Path $framework $name
    if (Test-Path $frameworkCandidate) {
        return $frameworkCandidate
    }

    $referenceRoots = @(
        "C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework",
        "C:\Program Files\Reference Assemblies\Microsoft\Framework",
        "C:\Windows\Microsoft.NET\assembly"
    )

    foreach ($referenceRoot in $referenceRoots) {
        if (-not (Test-Path $referenceRoot)) {
            continue
        }
        $found = Get-ChildItem $referenceRoot -Recurse -Filter $name -ErrorAction SilentlyContinue |
            Sort-Object FullName -Descending |
            Select-Object -First 1
        if ($found) {
            return $found.FullName
        }
    }

    throw "Required assembly not found: $name"
}

$refs = @(
    "PresentationCore.dll",
    "PresentationFramework.dll",
    "WindowsBase.dll",
    "System.dll",
    "System.Core.dll",
    "System.Xaml.dll",
    "System.Web.Extensions.dll",
    "System.Management.dll"
) | ForEach-Object { Find-Assembly $_ }

$sources = Get-ChildItem -Path $src -Filter *.cs -File | ForEach-Object { $_.FullName }
if (-not $sources) {
    throw "No C# sources found in $src"
}

$out = Join-Path $bin "OpenClawMonitor.exe"
$args = @(
    "/nologo",
    "/target:winexe",
    "/platform:x64",
    "/optimize+",
    "/warn:4",
    "/out:$out"
)
$args += $refs | ForEach-Object { "/reference:$_" }
$args += $sources

& $csc @args
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

Write-Host "Built $out"
