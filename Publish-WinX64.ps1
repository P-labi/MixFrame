$ErrorActionPreference = "Stop"

$projectRoot = $PSScriptRoot
$projectFile = Join-Path $projectRoot "MixFrame\MixFrame.csproj"
$releaseOutput = Join-Path $projectRoot "MixFrame\bin\x64\Release\net10.0-windows10.0.26100.0\win-x64"
$ffmpegSource = Join-Path $projectRoot "ffmpeg-8.1.2-essentials_build"
$distributionRoot = Join-Path $projectRoot "dist"
$distribution = Join-Path $distributionRoot "MixFrame-win-x64"

dotnet restore $projectFile -p:Platform=x64 -p:NuGetAudit=false --ignore-failed-sources
if ($LASTEXITCODE -ne 0) { throw "Release restore failed." }

dotnet build $projectFile -c Release -p:Platform=x64 -p:NuGetAudit=false --no-restore
if ($LASTEXITCODE -ne 0) { throw "Release build failed." }

$requiredSources = @(
    (Join-Path $releaseOutput "MixFrame.exe"),
    (Join-Path $releaseOutput "MixFrame.pri"),
    (Join-Path $releaseOutput "Pages\ImageWorkspacePage.xbf"),
    (Join-Path $releaseOutput "Pages\VideoWorkspacePage.xbf"),
    (Join-Path $releaseOutput "Styles\ThemeResources.xbf"),
    (Join-Path $ffmpegSource "bin\ffmpeg.exe"),
    (Join-Path $ffmpegSource "bin\ffprobe.exe")
)
foreach ($path in $requiredSources)
{
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required release file is missing: $path" }
}

if (Test-Path -LiteralPath $distribution)
{
    $resolvedDistribution = (Resolve-Path -LiteralPath $distribution).Path
    $resolvedRoot = (Resolve-Path -LiteralPath $distributionRoot).Path
    if (-not $resolvedDistribution.StartsWith($resolvedRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase))
    {
        throw "Refusing to replace a distribution outside the dist directory."
    }
    Remove-Item -LiteralPath $resolvedDistribution -Recurse -Force
}

New-Item -ItemType Directory -Path $distribution -Force | Out-Null
Copy-Item -Path (Join-Path $releaseOutput "*") -Destination $distribution -Recurse -Force
Copy-Item -LiteralPath $ffmpegSource -Destination (Join-Path $distribution "ffmpeg-8.1.2-essentials_build") -Recurse -Force

Write-Host "MixFrame distribution created at: $distribution"
