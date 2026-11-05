param([string]$ItemManifest)
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if ($ItemManifest) {
    $taskItems = Get-Content -LiteralPath $ItemManifest -Raw | ConvertFrom-Json
}
else {
    $taskJson = & dotnet msbuild (Join-Path $taskRoot 'SoncaAudioInspector.csproj') -nologo -getItem:Compile,Page,ApplicationDefinition
    if ($LASTEXITCODE -ne 0) { throw 'Cannot evaluate application build items.' }
    $taskItems = ($taskJson -join [Environment]::NewLine) | ConvertFrom-Json
}
$taskCounts = [ordered]@{}
foreach ($taskKind in @('Compile','Page','ApplicationDefinition')) {
    $taskSeen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($taskItem in $taskItems.Items.$taskKind) {
        $taskPath = [IO.Path]::GetFullPath($taskItem.FullPath)
        if (-not (Test-Path -LiteralPath $taskPath -PathType Leaf)) { throw "Missing $taskKind item: $taskPath" }
        if (-not $taskSeen.Add($taskPath)) { throw "Duplicate $taskKind item: $taskPath" }
        $taskPrefix = $taskRoot + [IO.Path]::DirectorySeparatorChar
        if (-not $taskPath.StartsWith($taskPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw "Source outside repository: $taskPath" }
        $taskRelative = $taskPath.Substring($taskPrefix.Length).Replace('\','/')
        if (-not $taskRelative.StartsWith('src/') -and $taskRelative -notin @('App.xaml','App.xaml.cs')) {
            throw "Unexpected application source outside src/: $taskRelative"
        }
    }
    $taskCounts[$taskKind] = $taskSeen.Count
}
$taskSources = @(Get-ChildItem (Join-Path $taskRoot 'src') -Recurse -File | Where-Object Extension -In '.cs','.xaml')
$taskDuplicates = @($taskSources | Group-Object Name | Where-Object Count -gt 1)
if ($taskDuplicates.Count) { throw ('Conflicting source basenames: '+($taskDuplicates.Name -join ', ')) }
foreach ($taskXaml in $taskSources | Where-Object { $_.Extension -eq '.xaml' -and $_.Name -notlike '*Theme.xaml' }) {
    if (-not (Test-Path -LiteralPath ($taskXaml.FullName+'.cs'))) { throw "XAML/code-behind separated: $($taskXaml.FullName)" }
}
foreach ($taskPartial in @('AudioRouting','MainWindow','SoncaBleManager','SoncaBleMixerWindow','StandardMeasurementWindow','StandardAcousticMeasurement','ModelConfigEditorWindow','ServerEngine')) {
    $taskFolders = @($taskSources | Where-Object Name -Like ($taskPartial+'.*') | Select-Object -ExpandProperty DirectoryName -Unique)
    if ($taskFolders.Count -ne 1) { throw "Partial class split across folders: $taskPartial" }
}
[xml]$taskApp = Get-Content (Join-Path $taskRoot 'App.xaml') -Raw
$taskStartup = [IO.Path]::GetFullPath((Join-Path $taskRoot $taskApp.DocumentElement.GetAttribute('StartupUri')))
$taskPagePaths = @($taskItems.Items.Page.FullPath)
if ($taskStartup -notin $taskPagePaths) { throw 'Startup URI does not reference a compiled WPF Page.' }
foreach ($taskSource in $taskSources | Where-Object Extension -In '.cs','.xaml') {
    foreach ($taskMatch in [regex]::Matches([IO.File]::ReadAllText($taskSource.FullName),';component/(?<path>[^"'')]+\.xaml)')) {
        $taskResource = [IO.Path]::GetFullPath((Join-Path $taskRoot $taskMatch.Groups['path'].Value))
        if ($taskResource -notin $taskPagePaths) { throw "Uncompiled WPF resource: $taskResource" }
    }
}
foreach ($taskProject in Get-ChildItem (Join-Path $taskRoot 'tools') -Recurse -File -Filter '*.csproj' | Where-Object FullName -NotMatch '[\\/](bin|obj)[\\/]') {
    [xml]$taskXml = Get-Content -LiteralPath $taskProject.FullName -Raw
    foreach ($taskInclude in $taskXml.SelectNodes('//Compile/@Include | //ProjectReference/@Include')) {
        if ($taskInclude.Value -match '[*?;$]') { continue }
        $taskLinked = [IO.Path]::GetFullPath((Join-Path $taskProject.DirectoryName $taskInclude.Value))
        if (-not (Test-Path -LiteralPath $taskLinked -PathType Leaf)) { throw "Broken source/project link in $($taskProject.Name): $taskLinked" }
    }
}
Write-Output ("PASS source layout: {0} Compile / {1} Page / {2} ApplicationDefinition; unique paths/names, co-located partials/XAML, startup/themes and linked test projects." -f $taskCounts.Compile,$taskCounts.Page,$taskCounts.ApplicationDefinition)
