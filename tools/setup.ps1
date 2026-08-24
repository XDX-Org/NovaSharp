param([switch]$ForceAssets)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$repo = Split-Path -Parent $PSScriptRoot
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'The .NET 10 SDK is required. Install it from https://dotnet.microsoft.com/download/dotnet/10.0.'
}
$sdkVersion = (& dotnet --version).Trim()
if (-not $sdkVersion.StartsWith('10.')) {
    throw "The .NET 10 SDK is required; dotnet resolved to $sdkVersion."
}
$architecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
if (-not [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [System.Runtime.InteropServices.OSPlatform]::Windows)) {
    throw 'Use tools/setup.sh on Linux or macOS.'
}
$rid = "win-$architecture"
if ($rid -notin 'win-arm64', 'win-x64') {
    throw "The current Windows development asset set does not support $rid."
}

& (Join-Path $PSScriptRoot 'acquire-language-servers.ps1') -Rid $rid -Force:$ForceAssets
$assets = Join-Path $repo "src\NovaSharp\LanguageServers\Assets\$rid"
$node = Join-Path $assets 'node\node.exe'
$npmCli = Join-Path $assets 'node\node_modules\npm\bin\npm-cli.js'

Push-Location $repo
try {
    & $node $npmCli ci --ignore-scripts --no-audit --no-fund
    if ($LASTEXITCODE -ne 0) {
        throw 'npm ci failed for Monaco.'
    }
    & $node tools/build-monaco.mjs
    if ($LASTEXITCODE -ne 0) {
        throw 'Monaco asset build failed.'
    }
    & $node tools/build-monaco.mjs --check
    if ($LASTEXITCODE -ne 0) {
        throw 'Monaco asset verification failed.'
    }
    & $node tools/build-workbench-assets.mjs
    if ($LASTEXITCODE -ne 0) {
        throw 'Workbench asset build failed.'
    }
    & $node tools/build-workbench-assets.mjs --check
    if ($LASTEXITCODE -ne 0) {
        throw 'Workbench asset verification failed.'
    }
    & dotnet restore NovaSharp.slnx
    if ($LASTEXITCODE -ne 0) {
        throw 'dotnet restore failed.'
    }
    & dotnet build NovaSharp.slnx --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw 'dotnet build failed.'
    }
    & dotnet test NovaSharp.slnx --no-build
    if ($LASTEXITCODE -ne 0) {
        throw 'dotnet test failed.'
    }
} finally {
    Pop-Location
}

Write-Output 'NovaSharp dependencies and local assets are ready.'
Write-Output 'Run: dotnet run --project src/NovaSharp/NovaSharp.csproj --no-build'
