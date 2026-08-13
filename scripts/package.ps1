param(
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$Framework,
    [Parameter(Mandatory = $true)][string]$JellyfinVersion,
    [string]$PackageSuffix = ""
)

$ErrorActionPreference = "Stop"
$project = "Jellyfin.Plugin.ContinueWatchingDedupEnhanced/Jellyfin.Plugin.ContinueWatchingDedupEnhanced.csproj"
$assembly = "Jellyfin.Plugin.ContinueWatchingDedupEnhanced.dll"
$packageName = "continuewatchingdedupenhanced${PackageSuffix}_${Version}.zip"
$publishDir = "artifacts/publish-$Framework-$JellyfinVersion"
$archive = "artifacts/$packageName"

New-Item -ItemType Directory -Force -Path artifacts | Out-Null
dotnet restore $project -p:JellyfinTargetFramework=$Framework -p:JellyfinVersion=$JellyfinVersion
dotnet publish $project -c Release --no-restore -o $publishDir `
    -p:JellyfinTargetFramework=$Framework `
    -p:JellyfinVersion=$JellyfinVersion `
    -p:PluginVersion=$Version

Compress-Archive -Path "$publishDir/$assembly" -DestinationPath $archive -Force
$sha256 = (Get-FileHash -Algorithm SHA256 $archive).Hash.ToLowerInvariant()
Set-Content -Path "$archive.sha256" -Value "$sha256  $packageName" -Encoding ascii
