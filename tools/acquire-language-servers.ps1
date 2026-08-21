param(
    [Parameter(Position = 0)]
    [ValidateSet('', 'win-x64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')]
    [string]$Rid = '',
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
# Invoke-WebRequest renders a progress bar for every chunk on Windows PowerShell, which dominates the cost of a large download.
$ProgressPreference = 'SilentlyContinue'
$repo = Split-Path -Parent $PSScriptRoot
$assetRoot = Join-Path $repo 'src\NovaSharp\LanguageServers\Assets'
$manifestPath = Join-Path $repo 'src\NovaSharp\LanguageServers\assets.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json

if (-not $Rid) {
    $architecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
    if ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)) {
        $Rid = "win-$architecture"
    } elseif ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Linux)) {
        $Rid = "linux-$architecture"
    } elseif ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::OSX)) {
        $Rid = "osx-$architecture"
    } else {
        throw 'Unsupported operating system.'
    }
}

$output = Join-Path $assetRoot $Rid
$manifestHash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
$stamp = Join-Path $output '.source-manifest.sha256'
$required = @(
    'roslyn\Microsoft.CodeAnalysis.LanguageServer.dll'
    'razor\Microsoft.VisualStudioCode.RazorExtension.dll'
    $(if ($Rid.StartsWith('win-')) { 'node\node.exe' } else { 'node\bin\node' })
    'node_modules\vscode-html-languageservice\package.json'
    'node_modules\vscode-css-languageservice\package.json'
    'node_modules\typescript-language-server\package.json'
    'server.cjs'
) | ForEach-Object { Join-Path $output $_ }
if (-not $Force -and (Test-Path -LiteralPath $stamp) -and
    (Get-Content -LiteralPath $stamp -Raw).Trim() -eq $manifestHash -and
    -not ($required | Where-Object { -not (Test-Path -LiteralPath $_) })) {
    Write-Output "Language-server assets for $Rid already match the pinned manifest."
    exit 0
}

$artifact = $manifest.roslynRazor.artifacts.$Rid
if (-not $artifact) {
    throw "Unsupported RID: $Rid"
}

$work = Join-Path ([System.IO.Path]::GetTempPath()) ("novasharp-language-servers-" + [guid]::NewGuid())
$stage = Join-Path $work 'asset'
try {
    New-Item -ItemType Directory -Path $work, $stage | Out-Null
    $vsix = Join-Path $work 'csharp.vsix'
    $version = $manifest.roslynRazor.version
    $gallery = 'https://marketplace.visualstudio.com/_apis/public/gallery'
    $uri = "$gallery/publishers/ms-dotnettools/vsextensions/csharp/$version/vspackage?targetPlatform=$($artifact.platform)"
    Invoke-WebRequest -UseBasicParsing -Uri $uri -OutFile $vsix
    $actual = (Get-FileHash -LiteralPath $vsix -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $artifact.sha256) {
        throw "Roslyn/Razor hash mismatch. Expected $($artifact.sha256), received $actual."
    }

    $vsixOutput = Join-Path $work 'vsix'
    Expand-Archive -LiteralPath $vsix -DestinationPath $vsixOutput
    New-Item -ItemType Directory -Force -Path "$stage\roslyn", "$stage\razor", "$stage\licenses" | Out-Null
    Copy-Item "$vsixOutput\extension\.roslyn\*" "$stage\roslyn" -Recurse -Force
    Copy-Item "$vsixOutput\extension\.razorExtension\*" "$stage\razor" -Recurse -Force
    Copy-Item "$vsixOutput\extension\LICENSE.txt" "$stage\licenses\csharp-MIT.txt" -Force
    Copy-Item "$vsixOutput\extension\ThirdPartyNotices.txt" "$stage\licenses\csharp-ThirdPartyNotices.txt" -Force

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
    Invoke-WebRequest -UseBasicParsing -Uri "https://nodejs.org/dist/v$nodeVersion/$archive" -OutFile $nodeArchive
    $actual = (Get-FileHash -LiteralPath $nodeArchive -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $manifest.node.sha256.$Rid) {
        throw "Node.js hash mismatch. Expected $($manifest.node.sha256.$Rid), received $actual."
    }

    $nodeOutput = Join-Path $work 'node'
    New-Item -ItemType Directory -Path $nodeOutput | Out-Null
    if ($archive.EndsWith('.zip')) {
        Expand-Archive -LiteralPath $nodeArchive -DestinationPath $nodeOutput
    } else {
        & tar -xf $nodeArchive -C $nodeOutput
        if ($LASTEXITCODE -ne 0) {
            throw 'Failed to extract the Node.js archive.'
        }
    }
    Copy-Item "$nodeOutput\node-v$nodeVersion-$nodePlatform" "$stage\node" -Recurse

    $web = Join-Path $repo 'src\NovaSharp\LanguageServers\Web'
    Copy-Item "$web\server.cjs", "$web\package.json", "$web\package-lock.json" $stage
    $node = if ($Rid.StartsWith('win-')) { "$stage\node\node.exe" } else { "$stage\node\bin\node" }
    $npmCli = if ($Rid.StartsWith('win-')) {
        "$stage\node\node_modules\npm\bin\npm-cli.js"
    } else {
        "$stage\node\lib\node_modules\npm\bin\npm-cli.js"
    }
    & $node $npmCli ci --omit=dev --ignore-scripts --no-audit --no-fund --prefix $stage
    if ($LASTEXITCODE -ne 0) {
        throw 'npm ci failed for web language servers.'
    }
    Copy-Item "$stage\node\LICENSE" "$stage\licenses\node-MIT.txt"
    [System.IO.File]::WriteAllText((Join-Path $stage '.source-manifest.sha256'), "$manifestHash`n")

    $resolvedRoot = [System.IO.Path]::GetFullPath($assetRoot).TrimEnd('\') + '\'
    $resolvedOutput = [System.IO.Path]::GetFullPath($output)
    if (-not $resolvedOutput.StartsWith($resolvedRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to replace an output outside $assetRoot."
    }
    if (Test-Path -LiteralPath $output) {
        Remove-Item -LiteralPath $output -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $assetRoot | Out-Null
    Move-Item -LiteralPath $stage -Destination $output
    Write-Output "Acquired and verified language servers for $Rid in $output."
} finally {
    if (Test-Path -LiteralPath $work) {
        Remove-Item -LiteralPath $work -Recurse -Force
    }
}
