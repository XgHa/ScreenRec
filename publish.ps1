# Build distributables for ScreenRec.
#
#   publish\thin\        needs .NET 9 Desktop Runtime (x64)
#   publish\portable\    self-contained, extract and run
#   Both packages include the ~305 KB native ScreenRec.Merger.exe.
#   publish\ScreenRec-thin.zip
#   publish\ScreenRec-portable.zip
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File publish.ps1            # both
#   powershell -ExecutionPolicy Bypass -File publish.ps1 -Only thin # one of thin|portable
#
# Note: PublishSingleFile is NOT used on purpose. ScreenRecorderLib is a C++/CLI
# mixed-mode assembly; the .NET single-file host cannot load it (BadImageFormatException),
# even when the dll is placed next to the exe.

param(
    [ValidateSet('both', 'thin', 'portable')]
    [string]$Only = 'both'
)

$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$proj = Join-Path $root 'src\ScreenRec.App\ScreenRec.App.csproj'
$out = Join-Path $root 'publish'
$mergerProject = Join-Path $root 'src\ScreenRec.Merger\ScreenRec.Merger.vcxproj'
$mergerExe = Join-Path $root 'src\ScreenRec.Merger\x64\Release\ScreenRec.Merger.exe'

# MSBuild launches CL.exe with ProcessStartInfo.EnvironmentVariables, a case-insensitive
# dictionary. If the process environment holds both HTTPS_PROXY and https_proxy, that
# throws "An item with the same key has already been added" and the native build fails
# (measured). Proxy clients and git-bash often export both spellings, so collapse each
# pair into a single upper-case entry before building.
# NOTE: keep this file ASCII-only - Windows PowerShell 5.1 reads .ps1 as ANSI without a BOM.
try {
    $envVars = [Environment]::GetEnvironmentVariables()
    $lowerProxyCopies = @($envVars.Keys | Where-Object { $_ -cmatch '^(http|https|all|no)_proxy$' })
    foreach ($name in $lowerProxyCopies) {
        $value = $envVars[$name]
        [Environment]::SetEnvironmentVariable($name, $null, 'Process')
        [Environment]::SetEnvironmentVariable($name.ToUpperInvariant(), $null, 'Process')
        [Environment]::SetEnvironmentVariable($name.ToUpperInvariant(), $value, 'Process')
        Write-Host "==> merged duplicate env var $name into $($name.ToUpperInvariant())"
    }
}
catch {
    Write-Host "==> env normalisation skipped: $($_.Exception.Message)"
}

if (-not (Test-Path -LiteralPath $out)) { New-Item -ItemType Directory -Path $out | Out-Null }

function Find-MSBuild {
    $command = Get-Command msbuild -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }

    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path -LiteralPath $vswhere -PathType Leaf) {
        $installation = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
        if ($installation) {
            $candidate = Join-Path $installation 'MSBuild\Current\Bin\MSBuild.exe'
            if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
        }
    }
    throw 'Visual Studio 2022 with C++ desktop tools is required to build ScreenRec.Merger.exe.'
}

function Build-Merger {
    Write-Host '==> building native merger ...'
    $msbuild = Find-MSBuild
    & $msbuild $mergerProject /p:Configuration=Release /p:Platform=x64 /m /v:q /nologo
    if ($LASTEXITCODE -ne 0) { throw 'native merger build failed' }
    if (-not (Test-Path -LiteralPath $mergerExe -PathType Leaf)) { throw "Missing merger: $mergerExe" }
}

function Copy-RuntimeTools {
    param([string]$TargetDirectory)
    Copy-Item -LiteralPath $mergerExe -Destination (Join-Path $TargetDirectory 'ScreenRec.Merger.exe') -Force
}

function Build-Target {
    param([string]$Name, [bool]$SelfContained)

    $dir = Join-Path $out $Name
    # Use .NET directly: in some shells Remove-Item is shadowed by a safe-delete wrapper
    # that asks for confirmation once a folder holds many files, which fails non-interactively.
    if (Test-Path $dir) { [System.IO.Directory]::Delete($dir, $true) }
    Write-Host "==> publishing $Name ..."

    $dotnetArgs = @(
        'publish', $proj,
        '-c', 'Release',
        '-r', 'win-x64',
        '--self-contained', $SelfContained.ToString().ToLower(),
        '-o', $dir,
        '-v', 'q', '--nologo'
    )
    & dotnet @dotnetArgs
    if ($LASTEXITCODE -ne 0) { throw "publish failed: $Name" }

    # The agent is a separate exe on purpose: it must NOT load WPF (see ScreenRec.Agent.csproj).
    # Publishing it into the same folder puts it next to the UI exe.
    Write-Host "==> publishing agent into $Name ..."
    $agentArgs = @(
        'publish', (Join-Path $root 'src\ScreenRec.Agent\ScreenRec.Agent.csproj'),
        '-c', 'Release',
        '-r', 'win-x64',
        '--self-contained', $SelfContained.ToString().ToLower(),
        '-o', $dir,
        '-v', 'q', '--nologo'
    )
    & dotnet @agentArgs
    if ($LASTEXITCODE -ne 0) { throw "publish failed: agent ($Name)" }

    Copy-RuntimeTools -TargetDirectory $dir
}

function Zip-Target {
    param([string]$Name)

    $dir = Join-Path $out $Name
    $zip = Join-Path $out "ScreenRec-$Name.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Write-Host "==> zipping $Name ..."

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($dir, $zip, 'Optimal', $false)
}

Build-Merger

if ($Only -eq 'both' -or $Only -eq 'thin') {
    Build-Target -Name 'thin' -SelfContained $false
    Zip-Target -Name 'thin'
}

if ($Only -eq 'both' -or $Only -eq 'portable') {
    Build-Target -Name 'portable' -SelfContained $true
    Zip-Target -Name 'portable'
}

Write-Host ''
Write-Host '==> done'
Get-ChildItem (Join-Path $out '*.zip') | ForEach-Object {
    '{0,-28} {1,8:N2} MB' -f $_.Name, ($_.Length / 1MB)
}
