param(
    [ValidateSet("all", "x64", "x86")]
    [string]$Architecture = "all",
    [string]$OutputRoot = "artifacts",
    [string]$Description = "Portable SoncaAudioInspector release"
)

$ErrorActionPreference = "Stop"
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Split-Path -Parent $scriptRoot
$projectFile = Join-Path $projectRoot "SoncaAudioInspector.csproj"
$outputRootPath = & (Join-Path $scriptRoot 'new-artifact.ps1') -Description $Description -Slug "portable-$Architecture" -OutputRoot $OutputRoot

$requiredSourceFiles = @(
    (Join-Path $projectRoot "checking_config.json"),
    (Join-Path $projectRoot "PortableReadme.txt"),
    (Join-Path $projectRoot "PortableReadme-x86.txt")
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

    $publishArguments = @(
        "publish",
        $projectFile,
        "--configuration", "Release",
        "--runtime", $rid,
        "--self-contained", "true",
        "--output", $publishPath,
        "-p:PlatformTarget=$TargetArchitecture",
        "-p:BaseOutputPath=$(Join-Path $outputRootPath "build-$TargetArchitecture")/",
        "-p:BaseIntermediateOutputPath=$(Join-Path $outputRootPath "obj-$TargetArchitecture")/"
    )
    & dotnet @publishArguments

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish $rid thất bại với exit code $LASTEXITCODE"
    }

    $realRouteSource = Join-Path $projectRoot "checking_config_realRoute.json"
    if (Test-Path -LiteralPath $realRouteSource) {
        Copy-Item -LiteralPath $realRouteSource -Destination $publishPath -Force
    }
    $saveStandardsSource = Join-Path $projectRoot "save standards"
    if (Test-Path -LiteralPath $saveStandardsSource) {
        $saveStandardsTarget = Join-Path $publishPath 'save standards'
        New-Item -ItemType Directory -Path $saveStandardsTarget -Force | Out-Null
        # The cleanup archive contains historical local data, not live references.
        foreach ($reference in Get-ChildItem -LiteralPath $saveStandardsSource -Force | Where-Object Name -ne 'retained-artifact-data') {
            Copy-Item -LiteralPath $reference.FullName -Destination $saveStandardsTarget -Recurse -Force
        }
    }

    $requiredPublishFiles = @(
        (Join-Path $publishPath "SoncaAudioInspector.exe"),
        (Join-Path $publishPath "checking_config.json"),
        (Join-Path $publishPath "PortableReadme.txt")
    )

    if ($TargetArchitecture -eq "x64") {
        $requiredPublishFiles += (Join-Path $publishPath "drivers\FastTrackPro_x64 Driver.rar")
    }

    foreach ($requiredFile in $requiredPublishFiles) {
        if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
            throw "Publish $rid thiếu runtime file: $requiredFile"
        }
    }

    $forbiddenPublishFiles = Get-ChildItem -LiteralPath $publishPath -Recurse -File | Where-Object {
        $_.Extension -in @(".cs", ".pdb", ".sln", ".csproj", ".user") -or
        $_.Name -in @("verify.txt", ".env", "scratch_transcript.txt") -or
        $_.FullName -match "[\\/]\.git([\\/]|$)"
    }
    if ($forbiddenPublishFiles) {
        $forbiddenList = ($forbiddenPublishFiles.FullName -join [Environment]::NewLine)
        throw "Publish $rid chứa file không được phép phát hành:`n$forbiddenList"
    }


    # Dọn dẹp các file .lib không cần thiết cho runtime
    Get-ChildItem -LiteralPath $publishPath -Filter "*.lib" -File | Remove-Item -Force

    # Tạo file khởi động nhanh tiện lợi cho người dùng
    $launcherContent = "@echo off`r`nstart `"`" `"%~dp0SoncaAudioInspector.exe`""
    Set-Content -LiteralPath (Join-Path $publishPath "Chay_SoncaAudioInspector.bat") -Value $launcherContent -Encoding ascii

    # Đóng gói zip nguyên thư mục gốc để khi giải nén không bị bung rời rạc ra ngoài
    Compress-Archive -Path $publishPath -DestinationPath $archivePath -CompressionLevel Optimal
    $archive = Get-Item -LiteralPath $archivePath
    $archiveHash = Get-FileHash -LiteralPath $archivePath -Algorithm SHA256
    $checksumPath = "$archivePath.sha256"
    "$($archiveHash.Hash.ToLowerInvariant())  $($archive.Name)" | Set-Content -LiteralPath $checksumPath -Encoding ascii
    Write-Host "Portable package: $($archive.FullName)"
    Write-Host "Archive size: $([Math]::Round($archive.Length / 1MB, 1)) MB"
    Write-Host "SHA-256: $($archiveHash.Hash.ToLowerInvariant())"
    Add-Content -LiteralPath (Join-Path $outputRootPath 'ARTIFACT.md') -Encoding utf8 -Value @(
        '', "Architecture: $TargetArchitecture", "Package: $($archive.Name)",
        "SHA-256: $($archiveHash.Hash.ToLowerInvariant())", 'Validation: publish and required package files verified; hardware and REW comparison not performed.'
    )
}

$targets = if ($Architecture -eq "all") { @("x64", "x86") } else { @($Architecture) }
foreach ($target in $targets) {
    Publish-Portable -TargetArchitecture $target
}
