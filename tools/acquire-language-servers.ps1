param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet('win-x64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')]
    [string]$Rid,
    [Parameter(Position = 1)]
    [string]$Output
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (-not $Output) { $Output = Join-Path $repo "src\NovaSharp\LanguageServers\Assets\$Rid" }
$manifest = Get-Content (Join-Path $repo 'src\NovaSharp\LanguageServers\assets.json') -Raw | ConvertFrom-Json
$work = Join-Path ([System.IO.Path]::GetTempPath()) ("novasharp-language-servers-" + [guid]::NewGuid())

try {
    New-Item -ItemType Directory -Path $work | Out-Null
    $artifact = $manifest.roslynRazor.artifacts.$Rid
    if (-not $artifact) { throw "Unsupported RID: $Rid" }

    $vsix = Join-Path $work 'csharp.zip'
    $version = $manifest.roslynRazor.version
    $uri = "https://marketplace.visualstudio.com/_apis/public/gallery/publishers/ms-dotnettools/vsextensions/csharp/$version/vspackage?targetPlatform=$($artifact.platform)"
    Invoke-WebRequest -Uri $uri -OutFile $vsix
    if ((Get-FileHash $vsix -Algorithm SHA256).Hash.ToLowerInvariant() -ne $artifact.sha256) {
        throw 'Roslyn/Razor hash mismatch'
    }

    $vsixOutput = Join-Path $work 'vsix'
    Expand-Archive -LiteralPath $vsix -DestinationPath $vsixOutput
    New-Item -ItemType Directory -Force -Path "$Output\roslyn", "$Output\razor", "$Output\licenses" | Out-Null
    Copy-Item "$vsixOutput\extension\.roslyn\*" "$Output\roslyn" -Recurse -Force
    Copy-Item "$vsixOutput\extension\.razorExtension\*" "$Output\razor" -Recurse -Force
    Copy-Item "$vsixOutput\extension\LICENSE.txt" "$Output\licenses\csharp-MIT.txt" -Force
    Copy-Item "$vsixOutput\extension\ThirdPartyNotices.txt" "$Output\licenses\csharp-ThirdPartyNotices.txt" -Force

    $nodeVersion = $manifest.node.version
    $platforms = @{
        'win-x64' = @("node-v$nodeVersion-win-x64.zip", 'win-x64')
        'linux-x64' = @("node-v$nodeVersion-linux-x64.tar.xz", 'linux-x64')
        'linux-arm64' = @("node-v$nodeVersion-linux-arm64.tar.xz", 'linux-arm64')
        'osx-x64' = @("node-v$nodeVersion-darwin-x64.tar.gz", 'darwin-x64')
        'osx-arm64' = @("node-v$nodeVersion-darwin-arm64.tar.gz", 'darwin-arm64')
    }
    $archive, $nodePlatform = $platforms[$Rid]
    $nodeArchive = Join-Path $work $archive
    Invoke-WebRequest -Uri "https://nodejs.org/dist/v$nodeVersion/$archive" -OutFile $nodeArchive
    if ((Get-FileHash $nodeArchive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $manifest.node.sha256.$Rid) {
        throw 'Node hash mismatch'
    }

    $nodeOutput = Join-Path $work 'node'
    New-Item -ItemType Directory -Path $nodeOutput | Out-Null
    if ($archive.EndsWith('.zip')) {
        Expand-Archive -LiteralPath $nodeArchive -DestinationPath $nodeOutput
    } else {
        tar -xf $nodeArchive -C $nodeOutput
        if ($LASTEXITCODE -ne 0) { throw 'Failed to extract Node archive' }
    }
    New-Item -ItemType Directory -Force -Path "$Output\node" | Out-Null
    Copy-Item "$nodeOutput\node-v$nodeVersion-$nodePlatform\*" "$Output\node" -Recurse -Force

    $web = Join-Path $repo 'src\NovaSharp\LanguageServers\Web'
    Copy-Item "$web\server.cjs", "$web\package.json", "$web\package-lock.json" $Output -Force
    if ($Rid.StartsWith('win-')) {
        & "$Output\node\npm.cmd" ci --omit=dev --ignore-scripts --no-audit --no-fund --prefix $Output
    } else {
        $env:PATH = (Join-Path $Output 'node\bin') + [System.IO.Path]::PathSeparator + $env:PATH
        & npm ci --omit=dev --ignore-scripts --no-audit --no-fund --prefix $Output
    }
    if ($LASTEXITCODE -ne 0) { throw 'npm ci failed' }
    Copy-Item "$Output\node\LICENSE" "$Output\licenses\node-MIT.txt" -Force
    Write-Output "Acquired and verified language servers for $Rid in $Output"
} finally {
    if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
}
