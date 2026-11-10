#!/bin/bash
# Builds the release zip for GitHub Releases: Screeny-mac-<version>.zip with Screeny.app inside.
# The in-app updater picks the release asset whose name contains "mac" and ends with .zip.
set -euo pipefail
cd "$(dirname "$0")"
app="$(SCREENY_TIMESTAMP=1 ./build.sh)"
version="$(/usr/libexec/PlistBuddy -c 'Print CFBundleShortVersionString' "$app/Contents/Info.plist")"
out="$PWD/build/Screeny-mac-$version.zip"
rm -f "$out"
ditto -c -k --keepParent "$app" "$out"
echo "$out"
