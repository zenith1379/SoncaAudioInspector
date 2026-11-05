param(
    [string]$PortableDir,
    [string]$Version = '1.0.0',
    [Parameter(Mandatory)][string]$Notes,
    [string]$KeyPath = "$env:USERPROFILE\.sonca-update\update-signing-key.pem",
    [string]$Previous,
    [string]$FeaturesFile,
    [string]$Released
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (-not (Test-Path -LiteralPath $KeyPath -PathType Leaf)) { throw 'Thiếu khóa ký. Cung cấp -KeyPath; script không tự sinh khóa.' }
$art = & "$PSScriptRoot/new-artifact.ps1" -Description "Update feed $Version" -Slug 'update-feed'
New-Item -ItemType Directory -Force "$art/logs" | Out-Null
if (-not $PortableDir) {
    & "$PSScriptRoot/publish-portable.ps1" -Architecture x64 -OutputRoot $art -Description "Portable input for update $Version" *> "$art/logs/publish-portable.log"
    if ($LASTEXITCODE -ne 0) { throw 'Publish portable thất bại; xem log.' }
    $portableArtifact = Get-ChildItem -LiteralPath $art -Directory | Where-Object Name -Like '*-portable-x64' | Select-Object -Last 1
    $PortableDir = Join-Path $portableArtifact.FullName 'SoncaAudioInspector-win-x64'
}
$project = Join-Path $repo 'tools/UpdateFeedTool/UpdateFeedTool.csproj'
& dotnet build $project -c Release -m:1 -p:UseSharedCompilation=false "-p:BaseIntermediateOutputPath=$art/obj/" "-p:OutputPath=$art/bin/" "-p:RestoreSources=$env:USERPROFILE\.nuget\packages" -p:NuGetAudit=false *> "$art/logs/build-tool.log"
if ($LASTEXITCODE -ne 0) { throw 'Build UpdateFeedTool thất bại; xem log.' }
$tool = Join-Path $art 'bin/UpdateFeedTool.dll'
$buildArgs = @($tool, 'build', '--portable', $PortableDir, '--version', $Version, '--notes', $Notes, '--key', $KeyPath, '--out', "$art/feed")
if ($Previous) { $buildArgs += @('--previous', $Previous) }
if ($FeaturesFile) { $buildArgs += @('--features-file', $FeaturesFile) }
if ($Released) { $buildArgs += @('--released', $Released) }
& dotnet @buildArgs *> "$art/logs/build-feed.log"
if ($LASTEXITCODE -ne 0) { throw 'Build feed thất bại; xem log.' }
# Verification uses the embedded production public key; a wrong private key fails here.
$signatureSource = [IO.File]::ReadAllText((Join-Path $repo 'src/Infrastructure/Update/UpdateSignature.cs'))
$publicKey = [regex]::Match($signatureSource, '(?s)-----BEGIN PUBLIC KEY-----.*?-----END PUBLIC KEY-----').Value
[IO.File]::WriteAllText("$art/public-key.pem", $publicKey)
& dotnet $tool verify --feed "$art/feed" --public-key "$art/public-key.pem" *> "$art/logs/verify-feed.log"
if ($LASTEXITCODE -ne 0) { throw 'Verify feed thất bại; khóa ký phải khớp khóa công khai nhúng trong app.' }
Write-Output "Feed đã build + verify: $art/feed. Chưa deploy."
