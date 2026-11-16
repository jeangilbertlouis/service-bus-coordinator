<#
.SYNOPSIS
  Publishes Service Bus Coordinator as a self-contained single-file app and zips it.
.EXAMPLE
  ./build/publish.ps1                       # win-x64, version from the .csproj
  ./build/publish.ps1 -Runtime win-arm64 -Version 1.2.3
.NOTES
  Works in Windows PowerShell 5.1 and PowerShell 7. For macOS (.app bundle) and Linux use build/publish.sh
  on that OS so file permissions are kept.
#>
param(
    [string[]] $Runtime = @('win-x64'),
    [string] $Version,
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src/ServiceBusCoordinator.App/ServiceBusCoordinator.App.csproj'
$artifacts = Join-Path $root 'artifacts'

if (-not $Version) {
    $Version = ([xml](Get-Content $project -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}

foreach ($rid in $Runtime) {
    $out = Join-Path $artifacts "$rid/publish"
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }

    Write-Host "Publishing $Version for $rid..."
    dotnet publish $project -c $Configuration -r $rid --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:Version=$Version -p:DebugType=none -o $out
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $rid" }

    # Native packages (SkiaSharp, HarfBuzz) ship ~100 MB of debug symbols; users don't need them.
    Get-ChildItem $out -Filter *.pdb | Remove-Item -Force

    $zip = Join-Path $artifacts "ServiceBusCoordinator-$Version-$rid.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip
    Write-Host "Created $zip"
}
