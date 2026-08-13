#!/bin/bash
# Build script for Continue Watching Deduplicator Enhanced

set -e

PLUGIN_NAME="Jellyfin.Plugin.ContinueWatchingDedupEnhanced"
VERSION="1.0.0.0"

echo "Building $PLUGIN_NAME v$VERSION..."
rm -rf "$PLUGIN_NAME/bin" "$PLUGIN_NAME/obj" "dist"

dotnet publish "$PLUGIN_NAME/$PLUGIN_NAME.csproj" \
    -c Release \
    -o "dist/$PLUGIN_NAME" \
    --no-self-contained

(cd "dist/$PLUGIN_NAME" && zip "../continuewatchingdedupenhanced_${VERSION}.zip" "$PLUGIN_NAME.dll")

echo "Build complete"
echo "Package: dist/continuewatchingdedupenhanced_${VERSION}.zip"
