param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',
    [switch]$FrameworkDependent
)
$ErrorActionPreference = 'Stop'
$taskRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskOutput = Join-Path $taskRoot "artifacts/MDVieW-$Runtime"
$taskArchive = Join-Path $taskRoot "artifacts/MDVieW-$Runtime.zip"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'

dotnet publish (Join-Path $taskRoot 'src/MDVieW/MDVieW.csproj') -c Release -r $Runtime `
    --self-contained $((-not $FrameworkDependent).ToString().ToLowerInvariant()) `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none `
    -o $taskOutput
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
$taskReadme = [System.IO.File]::ReadAllText((Join-Path $taskRoot 'README.md')).Replace('src/MDVieW/Assets/AppIcon.png', 'AppIcon.png')
[System.IO.File]::WriteAllText((Join-Path $taskOutput 'README.md'), $taskReadme, [System.Text.UTF8Encoding]::new($false))
Copy-Item -LiteralPath (Join-Path $taskRoot 'src/MDVieW/Assets/AppIcon.png') -Destination $taskOutput
Copy-Item -LiteralPath (Join-Path $taskRoot 'src/MDVieW/Assets/AppIcon.ico') -Destination $taskOutput
Copy-Item -LiteralPath (Join-Path $taskRoot 'LICENSE') -Destination $taskOutput
Copy-Item -LiteralPath (Join-Path $taskRoot 'THIRD-PARTY-NOTICES.txt') -Destination $taskOutput
if (-not $FrameworkDependent) {
    $taskPackageInfo = dotnet nuget locals global-packages --list
    if ($LASTEXITCODE -ne 0) { throw 'Could not locate runtime licenses.' }
    $taskPackageRoot = ($taskPackageInfo | Select-String '^global-packages:\s*(.+)$').Matches.Groups[1].Value.Trim()
    if (-not $taskPackageRoot) { throw 'NuGet package directory was not found.' }
    $taskLicenses = Join-Path $taskOutput 'licenses'
    New-Item -ItemType Directory -Path $taskLicenses -Force | Out-Null
    foreach ($taskPack in @('microsoft.netcore.app.runtime', 'microsoft.windowsdesktop.app.runtime')) {
        $taskPackRoot = Join-Path $taskPackageRoot "$taskPack.$Runtime"
        $taskPackVersion = Get-ChildItem -LiteralPath $taskPackRoot -Directory | Sort-Object { [version]$_.Name } | Select-Object -Last 1
        if (-not $taskPackVersion) { throw "Missing runtime notices: $taskPack" }
        $taskLicenseTarget = Join-Path $taskLicenses $taskPack
        New-Item -ItemType Directory -Path $taskLicenseTarget -Force | Out-Null
        Get-ChildItem -LiteralPath $taskPackVersion.FullName -File | Where-Object { $_.Name -match '^(LICENSE|THIRD-PARTY-NOTICES)' } | Copy-Item -Destination $taskLicenseTarget
    }
}
Compress-Archive -Path (Join-Path $taskOutput '*') -DestinationPath $taskArchive -Force
Write-Output "App: $taskOutput\MDVieW.exe"
Write-Output "ZIP: $taskArchive"
