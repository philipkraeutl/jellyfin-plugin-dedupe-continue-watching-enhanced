# Building Continue Watching Deduplicator Enhanced

Install the .NET 8 SDK, then run one of the provided scripts:

```powershell
.\build.ps1
```

```bash
./build.sh
```

Or publish manually:

```bash
dotnet publish Jellyfin.Plugin.ContinueWatchingDedupEnhanced/Jellyfin.Plugin.ContinueWatchingDedupEnhanced.csproj -c Release -o dist
```

The plugin assembly is:

```text
Jellyfin.Plugin.ContinueWatchingDedupEnhanced.dll
```

### Jellyfin 12 preview

The Jellyfin 12 variant requires the .NET 10 SDK and compiles against RC3:

```powershell
dotnet build Jellyfin.Plugin.ContinueWatchingDedupEnhanced/Jellyfin.Plugin.ContinueWatchingDedupEnhanced.csproj `
  -c Release `
  -p:JellyfinTargetFramework=net10.0 `
  -p:JellyfinVersion=12.0.0-rc3
```

## Manual installation

1. Stop Jellyfin.
2. Create a versioned plugin directory such as
   `Continue Watching Deduplicator Enhanced_1.0.0.0` below Jellyfin's plugin directory.
3. Copy `Jellyfin.Plugin.ContinueWatchingDedupEnhanced.dll` into it.
4. Remove any older development copy with the same enhanced plugin GUID.
5. Start Jellyfin.

The original Continue Watching Deduplicator has a different GUID and assembly,
so Jellyfin treats it as a separate plugin. Do not enable both simultaneously,
because both intercept the same Continue Watching responses.

## Release process

1. Ensure the version in the project and `build.yaml` is correct.
2. Push a four-part tag, for example `v1.0.0.0`.
3. GitHub Actions builds `Jellyfin.Plugin.ContinueWatchingDedupEnhanced.dll`.
4. The workflow creates `continuewatchingdedupenhanced_1.0.0.0.zip`.
5. The workflow creates a GitHub release and opens a manifest-update pull request.

The initial `manifest.json` intentionally contains no release versions. The
first release workflow run adds the first downloadable version and checksum.
