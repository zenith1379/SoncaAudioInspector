param([ValidateRange(1,365)][int]$KeepDays = 7, [switch]$Apply)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$cutoff = [DateTime]::UtcNow.AddDays(-$KeepDays)
$reportPath = & (Join-Path $PSScriptRoot 'new-artifact.ps1') -Description 'Artifact retention inventory and cleanup' -Slug 'artifact-cleanup'
function Assert-SafePath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    if (-not $full.StartsWith($projectRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw "Outside repository: $full" }
    $parent = $full
    while ($parent) {
        if ((Get-Item -LiteralPath $parent -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse point: $parent" }
        $parent = Split-Path -Parent $parent
    }
}
$candidates = @()
foreach ($name in @('artifacts','.artifacts','.codex-artifacts','demo-builds')) {
    $container = Join-Path $projectRoot $name
    if (Test-Path -LiteralPath $container) {
        Assert-SafePath $container
        $candidates += Get-ChildItem -LiteralPath $container -Force
    }
}
$candidates += Get-ChildItem -LiteralPath $projectRoot -Force -Directory | Where-Object {
    $_.Name -match '^(\.artifacts-|artifacts-|\.codex-bin-|\.codex-obj|bin-|obj-)' -or $_.Name -in @('.codex-build','hardware-channel-fix')
}
$rows = foreach ($item in $candidates) {
    Assert-SafePath $item.FullName
    $entries = if ($item.PSIsContainer) { @(Get-ChildItem -LiteralPath $item.FullName -Recurse -Force) } else { @($item) }
    if ($entries | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) { throw "Artifact contains a reparse point: $($item.FullName)" }
    $files = @($entries | Where-Object { -not $_.PSIsContainer })
    $when = $item.CreationTimeUtc
    $basis = 'legacy-latest-write-or-directory-creation'
    foreach ($file in $files) { if ($file.LastWriteTimeUtc -gt $when) { $when = $file.LastWriteTimeUtc } }
    $manifest = Join-Path $item.FullName 'artifact.json'
    if ($item.PSIsContainer -and (Test-Path -LiteralPath $manifest -PathType Leaf)) {
        $meta = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
        $when = [DateTimeOffset]::Parse($meta.createdUtc).UtcDateTime
        $basis = 'manifest-createdUtc'
    }
    $bytes = ($files | Measure-Object Length -Sum).Sum
    [pscustomobject]@{ path=$item.FullName; timestampUtc=$when.ToString('o'); basis=$basis; bytes=[long]$bytes; files=$files.Count; action=$(if ($when -lt $cutoff) {'delete'} else {'keep'}); deleted=$false; error=$null }
}
# Save the complete concrete plan before any deletion.
$rows | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $reportPath 'inventory.json') -Encoding utf8
if ($Apply) {
    foreach ($row in $rows | Where-Object action -eq 'delete') {
        Assert-SafePath $row.path
        try {
            Remove-Item -LiteralPath $row.path -Recurse -Force
            $row.deleted = $true
        }
        catch {
            # A protected/locked old build must not prevent cleanup of the rest.
            # Keep the failed path visible; never change its ACL or ownership.
            $row.error = $_.Exception.Message
        }
        $rows | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $reportPath 'inventory.json') -Encoding utf8
    }
}
$expired = @($rows | Where-Object action -eq 'delete')
$summary = "Cutoff UTC: $($cutoff.ToString('o')); apply=$Apply; expired=$($expired.Count); kept=$(@($rows | Where-Object action -eq 'keep').Count); expired GiB=$([Math]::Round(($expired | Measure-Object bytes -Sum).Sum/1GB,3))"
$summary += "; deleted=$(@($rows | Where-Object deleted).Count); failed=$(@($rows | Where-Object error).Count)"
Add-Content -LiteralPath (Join-Path $reportPath 'ARTIFACT.md') -Value @('', $summary) -Encoding utf8
Write-Output $summary
Write-Output "Report: $reportPath"
