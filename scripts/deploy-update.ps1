param(
    [Parameter(Mandatory)][string]$FeedDir,
    [string]$VpsHost = 'root@178.18.249.39',
    [string]$SshKey = "$HOME\.ssh\tracking_shopee_codex",
    [string]$RemoteDir = '/opt/speaker-inventory/updates/audio-inspector',
    [switch]$WhatIf,
    [string]$OutputRoot = 'artifacts'
)
$ErrorActionPreference = 'Stop'
if ($VpsHost -notmatch '^[a-zA-Z0-9_.@-]+$' -or $RemoteDir -notmatch '^/[a-zA-Z0-9_/-]+$' -or $RemoteDir.Contains('..')) { throw 'Host hoặc đường dẫn remote không hợp lệ.' }
$RemoteDir = $RemoteDir.TrimEnd('/')
if (-not $RemoteDir) { throw 'Không dùng root filesystem làm thư mục feed.' }
$FeedDir = [IO.Path]::GetFullPath($FeedDir).TrimEnd('\','/')
function NativeTool([string]$relative, [string]$fallback) {
    $native = Join-Path $env:WINDIR "System32/$relative"
    if (Test-Path -LiteralPath $native -PathType Leaf) { return $native }
    return (Get-Command $fallback -ErrorAction Stop).Source
}
$ssh = NativeTool 'OpenSSH/ssh.exe' 'ssh'
$scp = NativeTool 'OpenSSH/scp.exe' 'scp'
$tar = NativeTool 'tar.exe' 'tar.exe'
# A missing/changed known host is a hard failure; never add or update a host key automatically.
$sshOptions = @('-i', $SshKey, '-o', 'BatchMode=yes', '-o', 'StrictHostKeyChecking=yes', '-o', 'UpdateHostKeys=no', '-o', 'ConnectTimeout=15')
function Display([string]$command, [string[]]$arguments) {
    Write-Host ($command + ' ' + (($arguments | ForEach-Object { '"' + $_.Replace('"','\"') + '"' }) -join ' '))
}
function External([string]$command, [string[]]$arguments) {
    Display $command $arguments
    if (-not $WhatIf) { & $command @arguments; if ($LASTEXITCODE -ne 0) { throw "$command thất bại: $LASTEXITCODE" } }
}
function ParseInventory([string[]]$lines, [string]$root) {
    $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $directory = ''
    foreach ($line in $lines) {
        if (-not $line) { continue }
        if ($line.EndsWith(':')) {
            $header = $line.Substring(0, $line.Length - 1)
            if ($header -ne $root -and -not $header.StartsWith($root + '/', [StringComparison]::Ordinal)) { throw 'Inventory ra ngoài thư mục feed.' }
            $directory = $header.Substring($root.Length).TrimStart('/')
        }
        elseif (-not $line.EndsWith('/')) { [void]$names.Add($(if ($directory) { "$directory/$line" } else { $line })) }
    }
    return ,$names
}
function SendArchive([string]$archive, [string]$command) {
    Display $ssh ($sshOptions + @($VpsHost, $command))
    Write-Host "Binary stdin <- $archive (FileStream -> SSH StandardInput.BaseStream)"
    if ($WhatIf) { return }
    $start = [Diagnostics.ProcessStartInfo]::new($ssh)
    $start.UseShellExecute = $false; $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $start.Arguments = (($sshOptions + @($VpsHost, $command) | ForEach-Object { '"' + $_.Replace('"','\"') + '"' }) -join ' ')
    $process = [Diagnostics.Process]::Start($start)
    $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
    $copyError = $null
    try { $inputFile = [IO.File]::OpenRead($archive); try { $inputFile.CopyTo($process.StandardInput.BaseStream) } finally { $inputFile.Dispose() } }
    catch { $copyError = $_ }
    finally { $process.StandardInput.Close() }
    $process.WaitForExit()
    $out = $stdout.GetAwaiter().GetResult(); $err = $stderr.GetAwaiter().GetResult()
    [IO.File]::WriteAllText("$art/logs/$([IO.Path]::GetFileNameWithoutExtension($archive))-extract.log", $out + $err)
    if ($process.ExitCode -ne 0 -or $copyError) { throw "SSH/tar thất bại ($($process.ExitCode)): $err $copyError" }
}
if ($WhatIf) { $art = '<new-deploy-artifact>' }
else {
    $art = & "$PSScriptRoot/new-artifact.ps1" -Description 'U2 update upload and verification' -Slug 'deploy-update' -OutputRoot $OutputRoot
    New-Item -ItemType Directory -Force "$art/logs", "$art/transfer" | Out-Null
}
External $ssh ($sshOptions + @($VpsHost, "mkdir -p -- '$RemoteDir/files' '$RemoteDir/versions' '$RemoteDir/packages'"))
foreach ($directory in @('files','versions','packages')) {
    $remoteRoot = "$RemoteDir/$directory"
    $listingCommand = "ls -1R -p -- '$remoteRoot'"
    Display $ssh ($sshOptions + @($VpsHost, $listingCommand))
    if ($WhatIf) { $existing = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal) }
    else {
        $lines = @(& $ssh @sshOptions $VpsHost $listingCommand)
        if ($LASTEXITCODE -ne 0) { throw "Không liệt kê được $remoteRoot" }
        $existing = ParseInventory $lines $remoteRoot
        [IO.File]::WriteAllLines("$art/transfer/$directory-existing.txt", [string[]]$lines)
    }
    $localRoot = Join-Path $FeedDir $directory
    $prefix = $localRoot + [IO.Path]::DirectorySeparatorChar
    $missing = @([IO.Directory]::EnumerateFiles($localRoot, '*', [IO.SearchOption]::AllDirectories) | ForEach-Object {
        $relative = $_.Substring($prefix.Length).Replace('\','/')
        if (-not $existing.Contains($relative)) { "$directory/$relative" }
    } | Sort-Object)
    Write-Host "$directory : $($missing.Count) local candidates; WhatIf assumes all missing, U2b computes the exact remote difference."
    if ($missing.Count -eq 0) { continue }
    $list = "$art/transfer/$directory-missing.nul"; $archive = "$art/transfer/$directory.tar"
    if (-not $WhatIf) { [IO.File]::WriteAllText($list, ($missing -join [char]0) + [char]0, [Text.UTF8Encoding]::new($false)) }
    External $tar @('-cf', $archive, '-C', $FeedDir, '--null', '-T', $list)
    SendArchive $archive "tar -xf - -C '$RemoteDir' --skip-old-files --no-same-owner --no-same-permissions"
}
$suffix = [Guid]::NewGuid().ToString('N')
External $scp ($sshOptions + @("$FeedDir/latest.json.sig", "${VpsHost}:$RemoteDir/.latest.json.sig.$suffix"))
External $scp ($sshOptions + @("$FeedDir/latest.json", "${VpsHost}:$RemoteDir/.latest.json.$suffix"))
External $ssh ($sshOptions + @($VpsHost, "mv -f -- '$RemoteDir/.latest.json.sig.$suffix' '$RemoteDir/latest.json.sig' && mv -f -- '$RemoteDir/.latest.json.$suffix' '$RemoteDir/latest.json'"))
External $scp ($sshOptions + @("$FeedDir/index.html", "${VpsHost}:$RemoteDir/.index.html.$suffix"))
External $ssh ($sshOptions + @($VpsHost, "mv -f -- '$RemoteDir/.index.html.$suffix' '$RemoteDir/index.html'"))
$url = 'https://inventory.acnos.store/updates/audio-inspector/latest.json'
if ($WhatIf) { Write-Output "GET $url; compare SHA-256 with $FeedDir/latest.json"; Write-Output 'GET https://inventory.acnos.store/update (follow redirect); check version'; return }
Invoke-WebRequest -Uri $url -OutFile "$art/latest-downloaded.json" -UseBasicParsing
$localHash = (Get-FileHash -LiteralPath "$FeedDir/latest.json" -Algorithm SHA256).Hash
if ((Get-FileHash -LiteralPath "$art/latest-downloaded.json" -Algorithm SHA256).Hash -ne $localHash) { throw 'SHA-256 latest.json trên server khác bản local.' }
Write-Output "PASS remote latest.json SHA-256: $localHash"
$expected = ([IO.File]::ReadAllText("$FeedDir/latest.json") | ConvertFrom-Json).version
Invoke-WebRequest -Uri 'https://inventory.acnos.store/update' -MaximumRedirection 5 -OutFile "$art/history-downloaded.html" -UseBasicParsing
if (-not [IO.File]::ReadAllText("$art/history-downloaded.html").Contains("data-version=`"$expected`"")) { throw 'Trang /update không có phiên bản vừa phát hành.' }
Write-Output "PASS /update followed redirect and contains version $expected"
