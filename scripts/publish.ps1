[CmdletBinding()]
param(
    [string]$Project,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$OutputRoot = 'artifacts',
    [string]$Version
)

$ErrorActionPreference = 'Stop'

function Resolve-RepositoryPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    $resolved = Resolve-Path -LiteralPath $Path -ErrorAction Stop
    return $resolved.Path
}

function Resolve-PublishProject {
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [string]$RequestedProject
    )

    if (-not [string]::IsNullOrWhiteSpace($RequestedProject)) {
        $requestedPath = if ([IO.Path]::IsPathRooted($RequestedProject)) {
            $RequestedProject
        }
        else {
            Join-Path $RepositoryRoot $RequestedProject
        }

        $resolvedRequested = Resolve-RepositoryPath -Path $requestedPath
        if ([IO.Path]::GetExtension($resolvedRequested) -ne '.csproj') {
            throw "-Project must point to a .csproj file: $resolvedRequested"
        }

        return $resolvedRequested
    }

    $preferred = @(
        @(
            'src/Codex.ProcessMonitor/Codex.ProcessMonitor.csproj',
            'src/Codex.ProcessMonitor.App/Codex.ProcessMonitor.App.csproj',
            'src/Codex.ProcessMonitor.Cli/Codex.ProcessMonitor.Cli.csproj'
        ) | ForEach-Object {
            $candidate = Join-Path $RepositoryRoot $_
            if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                Resolve-RepositoryPath -Path $candidate
            }
        }
    )

    if ($preferred.Count -eq 1) {
        return $preferred[0]
    }

    $projects = @(
        Get-ChildItem -LiteralPath $RepositoryRoot -Filter '*.csproj' -File -Recurse -ErrorAction SilentlyContinue |
            Where-Object {
                $_.FullName -notmatch '\\(?:bin|obj|artifacts|TestResults|\.git|\.codex)\\' -and
                $_.FullName -notmatch '\\(?:tests?|test)\\' -and
                $_.BaseName -notmatch '(?i)(Infrastructure|Tests?|TestProject)$'
            }
    )

    if ($projects.Count -eq 1) {
        return $projects[0].FullName
    }

    if ($projects.Count -eq 0) {
        throw 'No publishable .csproj was found. Add an application project or pass -Project <path-to-csproj>.'
    }

    throw "More than one publishable .csproj was found. Pass -Project explicitly: $($projects.FullName -join ', ')"
}

function Resolve-Version {
    param([string]$RequestedVersion)

    $candidate = $RequestedVersion
    if ([string]::IsNullOrWhiteSpace($candidate)) {
        $candidate = $env:GITHUB_REF_NAME
    }
    if ([string]::IsNullOrWhiteSpace($candidate)) {
        $candidate = '0.0.0-local'
    }

    $candidate = $candidate.Trim()
    if ($candidate.StartsWith('v', [StringComparison]::OrdinalIgnoreCase)) {
        $candidate = $candidate.Substring(1)
    }

    $safeVersion = [Regex]::Replace($candidate, '[^0-9A-Za-z._-]', '-')
    if ([string]::IsNullOrWhiteSpace($safeVersion)) {
        throw 'Version must contain at least one usable character.'
    }

    return $safeVersion
}

$repositoryRoot = Resolve-RepositoryPath -Path (Join-Path $PSScriptRoot '..')
$publishProject = Resolve-PublishProject -RepositoryRoot $repositoryRoot -RequestedProject $Project
$resolvedVersion = Resolve-Version -RequestedVersion $Version

$outputPath = if ([IO.Path]::IsPathRooted($OutputRoot)) {
    $OutputRoot
}
else {
    Join-Path $repositoryRoot $OutputRoot
}
$outputPath = [IO.Path]::GetFullPath($outputPath)
$publishPath = Join-Path $outputPath "publish\$Runtime"
$packageBaseName = "Codex.ProcessMonitor-$resolvedVersion-$Runtime"
$zipPath = Join-Path $outputPath "$packageBaseName.zip"
$checksumPath = "$zipPath.sha256"

if (Test-Path -LiteralPath $publishPath) {
    Remove-Item -LiteralPath $publishPath -Recurse -Force
}
if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}
if (Test-Path -LiteralPath $checksumPath) {
    Remove-Item -LiteralPath $checksumPath -Force
}
New-Item -ItemType Directory -Path $publishPath -Force | Out-Null

Write-Host "Publishing $publishProject for $Runtime ($Configuration)"
& dotnet publish $publishProject `
    --configuration $Configuration `
    --runtime $Runtime `
    --self-contained true `
    --output $publishPath `
    -p:PublishTrimmed=false
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$publishEntries = @(Get-ChildItem -LiteralPath $publishPath -Force -ErrorAction SilentlyContinue)
if ($publishEntries.Count -eq 0) {
    throw "dotnet publish produced no files in $publishPath"
}

Compress-Archive -Path (Join-Path $publishPath '*') -DestinationPath $zipPath -CompressionLevel Optimal
$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $([IO.Path]::GetFileName($zipPath))" | Set-Content -LiteralPath $checksumPath -Encoding ascii

Write-Host "Package:  $zipPath"
Write-Host "SHA-256:  $checksumPath"
