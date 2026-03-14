[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = (Resolve-Path (Join-Path $scriptDir "../..")).Path
Set-Location $repoRoot

$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$reportRoot = Join-Path $repoRoot "artifacts/code-metrics"
$reportDir = Join-Path $reportRoot $timestamp
New-Item -ItemType Directory -Path $reportDir -Force | Out-Null

Write-Host "Restoring solution packages for metrics analysis..."
dotnet restore aevatar.slnx --nologo

Write-Host "Restoring code metrics tool package..."
dotnet restore tools/Aevatar.Tools.CodeMetrics/Aevatar.Tools.CodeMetrics.csproj --nologo

$packagesProps = [xml](Get-Content -Path (Join-Path $repoRoot "Directory.Packages.props"))
$metricsPackage = $packagesProps.Project.ItemGroup.PackageVersion |
    Where-Object { $_.Include -eq "Microsoft.CodeAnalysis.Metrics" } |
    Select-Object -First 1

if ($null -eq $metricsPackage -or [string]::IsNullOrWhiteSpace([string]$metricsPackage.Version)) {
    throw "Unable to resolve Microsoft.CodeAnalysis.Metrics version from Directory.Packages.props."
}

$nugetPackagesRoot = if ([string]::IsNullOrWhiteSpace($env:NUGET_PACKAGES)) {
    Join-Path $HOME ".nuget/packages"
}
else {
    $env:NUGET_PACKAGES
}

$metricsExe = Join-Path $nugetPackagesRoot "microsoft.codeanalysis.metrics/$($metricsPackage.Version)/Metrics/Metrics.exe"
if (-not (Test-Path $metricsExe)) {
    throw "Metrics executable was not restored: $metricsExe"
}

$projects = Get-ChildItem -Path (Join-Path $repoRoot "src") -Recurse -Filter *.csproj | Sort-Object FullName
if ($projects.Count -eq 0) {
    throw "No production projects were found under src/."
}

$manifest = New-Object System.Collections.Generic.List[object]

foreach ($project in $projects) {
    $relativeProjectPath = [System.IO.Path]::GetRelativePath($repoRoot, $project.FullName)
    $relativeProjectDirectory = [System.IO.Path]::GetRelativePath($repoRoot, $project.DirectoryName)
    $outputDirectory = Join-Path $reportDir $relativeProjectDirectory
    $outputFile = Join-Path $outputDirectory "$($project.BaseName).Metrics.xml"

    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null

    Write-Host "Generating code metrics for $relativeProjectPath"
    & $metricsExe "/project:$($project.FullName)" "/out:$outputFile"

    $manifest.Add([pscustomobject]@{
            Project = $relativeProjectPath
            Report = [System.IO.Path]::GetRelativePath($reportDir, $outputFile)
        })
}

$manifest |
    ConvertTo-Json -Depth 3 |
    Set-Content -Path (Join-Path $reportDir "manifest.json") -Encoding utf8

Write-Host "Code metrics reports written to $reportDir"
