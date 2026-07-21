param(
    [ValidateSet("all", "x64", "x86")]
    [string]$Architecture = "all",
    [string]$OutputRoot = "artifacts"
)

$ErrorActionPreference = "Stop"
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Split-Path -Parent $scriptRoot
$projectFile = Join-Path $projectRoot "SoncaAudioInspector.csproj"
$outputRootPath = [System.IO.Path]::GetFullPath((Join-Path $projectRoot $OutputRoot))

$requiredSourceFiles = @(
    (Join-Path $projectRoot "checking_config.json"),
    (Join-Path $projectRoot "PortableReadme.txt"),
    (Join-Path $projectRoot "PortableReadme-x86.txt"),
    (Join-Path $projectRoot "models\visual-ai.onnx")
)

foreach ($requiredFile in $requiredSourceFiles) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
        throw "Thiếu file cần đóng gói: $requiredFile"
    }
}

New-Item -ItemType Directory -Path $outputRootPath -Force | Out-Null

function Publish-Portable {
    param([ValidateSet("x64", "x86")][string]$TargetArchitecture)

    $rid = "win-$TargetArchitecture"
    $packageSuffix = if ($TargetArchitecture -eq "x86") { "win-x86-lite" } else { "win-x64" }
    $publishPath = Join-Path $outputRootPath "SoncaAudioInspector-$packageSuffix"
    $archivePath = Join-Path $outputRootPath "SoncaAudioInspector-$packageSuffix.zip"

    if (Test-Path -LiteralPath $publishPath) {
        Remove-Item -LiteralPath $publishPath -Recurse -Force
    }
    if (Test-Path -LiteralPath $archivePath) {
        Remove-Item -LiteralPath $archivePath -Force
    }

    $publishArguments = @(
        "publish",
        $projectFile,
        "--configuration", "Release",
        "--runtime", $rid,
        "--self-contained", "true",
        "--output", $publishPath,
        "-p:PlatformTarget=$TargetArchitecture"
    )
    & dotnet @publishArguments

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish $rid thất bại với exit code $LASTEXITCODE"
    }

    $requiredPublishFiles = @(
        (Join-Path $publishPath "SoncaAudioInspector.exe"),
        (Join-Path $publishPath "checking_config.json"),
        (Join-Path $publishPath "PortableReadme.txt")
    )

    if ($TargetArchitecture -eq "x64") {
        $requiredPublishFiles += @(
            (Join-Path $publishPath "models\visual-ai.onnx"),
            (Join-Path $publishPath "onnxruntime.dll"),
            (Join-Path $publishPath "OpenCvSharpExtern.dll"),
            (Join-Path $publishPath "drivers\FastTrackPro_x64 Driver.rar")
        )
    }

    foreach ($requiredFile in $requiredPublishFiles) {
        if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
            throw "Publish $rid thiếu runtime file: $requiredFile"
        }
    }

    if ($TargetArchitecture -eq "x86") {
        $x64OnlyFiles = @(
            (Join-Path $publishPath "onnxruntime.dll"),
            (Join-Path $publishPath "OpenCvSharpExtern.dll"),
            (Join-Path $publishPath "models\visual-ai.onnx")
        )
        foreach ($x64OnlyFile in $x64OnlyFiles) {
            if (Test-Path -LiteralPath $x64OnlyFile) {
                throw "Publish x86 chứa nhầm file x64: $x64OnlyFile"
            }
        }
    }

    Compress-Archive -Path (Join-Path $publishPath "*") -DestinationPath $archivePath -CompressionLevel Optimal
    $archive = Get-Item -LiteralPath $archivePath
    Write-Host "Portable package: $($archive.FullName)"
    Write-Host "Archive size: $([Math]::Round($archive.Length / 1MB, 1)) MB"
}

$targets = if ($Architecture -eq "all") { @("x64", "x86") } else { @($Architecture) }
foreach ($target in $targets) {
    Publish-Portable -TargetArchitecture $target
}
