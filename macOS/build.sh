#!/bin/bash
# Builds Screeny.app, signs it with a stable identity and (with --install) puts it in ~/Applications.
#
# Why the signature matters: macOS remembers Screen Recording permission for the app's code signature.
# An ad-hoc signature changes with every build, so macOS forgets the permission after each rebuild.
# Signing with the same certificate every time keeps the permission across rebuilds.
set -euo pipefail
cd "$(dirname "$0")"
install=false; run=false
for a in "$@"; do
  case "$a" in
    --install) install=true ;;
    --run) install=true; run=true ;;
    *) echo "Usage: build.sh [--install] [--run]" >&2; exit 2 ;;
  esac
done

swift build -c release --arch arm64 >&2
bin="$(swift build -c release --arch arm64 --show-bin-path)"
app="$PWD/build/Screeny.app"
rm -rf "$app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp "$bin/Screeny" "$app/Contents/MacOS/Screeny"
cp -R "$bin/Screeny_ScreenyKit.bundle" "$app/Contents/Resources/"
cp Support/AppIcon.icns "$app/Contents/Resources/"
cp ../LICENSE "$app/Contents/Resources/LICENSE.txt"
sed "s/__BUILD__/$(date +%Y%m%d%H%M)/" Support/Info.plist > "$app/Contents/Info.plist"

# Signing identity: $SCREENY_SIGN_IDENTITY, else the first Apple Development / Developer ID certificate, else ad-hoc.
identity="${SCREENY_SIGN_IDENTITY:-}"
if [[ -z "$identity" ]]; then
  identity="$(security find-identity -v -p codesigning | awk -F'"' '/Developer ID Application|Apple Development/ {print $2; exit}')"
fi
if [[ -z "$identity" ]]; then
  echo "warning: no signing certificate found; using ad-hoc signing (Screen Recording permission resets on every rebuild)" >&2
  identity="-"
fi
codesign --force --deep --options runtime --timestamp=none --sign "$identity" "$app" >&2
codesign --verify --strict "$app" >&2
echo "Signed with: $identity" >&2

if $install; then
  dest="$HOME/Applications/Screeny.app"
  mkdir -p "$HOME/Applications"
  pkill -x Screeny 2>/dev/null && sleep 0.5 || true
  rm -rf "$dest"
  ditto "$app" "$dest"
  echo "Installed: $dest" >&2
  $run && open "$dest"
fi
echo "$app"
