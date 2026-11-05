param(
    [Parameter(Mandatory)][string]$FeedDir,
    [string]$VpsHost = 'root@178.18.249.39',
    [string]$SshKey = "$HOME\.ssh\tracking_shopee_codex",
    [string]$RemoteDir = '/opt/speaker-inventory/updates/audio-inspector',
    [switch]$WhatIf
)
$ErrorActionPreference = 'Stop'
if ($VpsHost -notmatch '^[a-zA-Z0-9_.@-]+$' -or $RemoteDir -notmatch '^/[a-zA-Z0-9_/-]+$' -or $RemoteDir.Contains('..')) { throw 'Host hoặc đường dẫn remote không hợp lệ.' }
$FeedDir = [IO.Path]::GetFullPath($FeedDir)
function External([string]$command, [string[]]$arguments) {
    Write-Output ($command + ' ' + (($arguments | ForEach-Object { '"' + $_.Replace('"','\"') + '"' }) -join ' '))
    if (-not $WhatIf) { & $command @arguments; if ($LASTEXITCODE -ne 0) { throw "$command thất bại: $LASTEXITCODE" } }
}
$sshOptions = @('-i', $SshKey, '-o', 'BatchMode=yes')
$rsyncSsh = 'ssh -i "' + $SshKey + '" -o BatchMode=yes'
External 'ssh' ($sshOptions + @($VpsHost, "mkdir -p -- '$RemoteDir/files' '$RemoteDir/versions' '$RemoteDir/packages'"))
# Immutable, content-addressed objects first. Existing hashes are never overwritten.
foreach ($directory in @('files','versions','packages')) {
    External 'rsync' @('-a', '--ignore-existing', '-e', $rsyncSsh, "$FeedDir/$directory/", "${VpsHost}:$RemoteDir/$directory/")
}
$suffix = [Guid]::NewGuid().ToString('N')
External 'scp' ($sshOptions + @("$FeedDir/latest.json.sig", "${VpsHost}:$RemoteDir/.latest.json.sig.$suffix"))
External 'scp' ($sshOptions + @("$FeedDir/latest.json", "${VpsHost}:$RemoteDir/.latest.json.$suffix"))
External 'ssh' ($sshOptions + @($VpsHost, "mv -f -- '$RemoteDir/.latest.json.sig.$suffix' '$RemoteDir/latest.json.sig' && mv -f -- '$RemoteDir/.latest.json.$suffix' '$RemoteDir/latest.json'"))
# Publish the HTML only after the signed latest pointers and all immutable packages.
External 'scp' ($sshOptions + @("$FeedDir/index.html", "${VpsHost}:$RemoteDir/.index.html.$suffix"))
External 'ssh' ($sshOptions + @($VpsHost, "mv -f -- '$RemoteDir/.index.html.$suffix' '$RemoteDir/index.html'"))
$url = 'https://inventory.acnos.store/updates/audio-inspector/latest.json'
if ($WhatIf) { Write-Output "GET $url; compare SHA-256 with $FeedDir/latest.json"; Write-Output 'GET https://inventory.acnos.store/update (follow redirect); check version'; return }
$art = & "$PSScriptRoot/new-artifact.ps1" -Description 'U2 deployed update manifest verification' -Slug 'deploy-update'
Invoke-WebRequest -Uri $url -OutFile "$art/latest-downloaded.json" -UseBasicParsing
$localHash = (Get-FileHash -LiteralPath "$FeedDir/latest.json" -Algorithm SHA256).Hash
if ((Get-FileHash -LiteralPath "$art/latest-downloaded.json" -Algorithm SHA256).Hash -ne $localHash) { throw 'SHA-256 latest.json trên server khác bản local.' }
Write-Output "PASS remote latest.json SHA-256: $localHash"
$expected = ([IO.File]::ReadAllText("$FeedDir/latest.json") | ConvertFrom-Json).version
Invoke-WebRequest -Uri 'https://inventory.acnos.store/update' -MaximumRedirection 5 -OutFile "$art/history-downloaded.html" -UseBasicParsing
if (-not [IO.File]::ReadAllText("$art/history-downloaded.html").Contains("data-version=`"$expected`"")) { throw 'Trang /update không có phiên bản vừa phát hành.' }
Write-Output "PASS /update followed redirect and contains version $expected"
