#!/usr/bin/env bash
# Packs one desktop build (CiBuild layout: the player and Mods/<id>/ per shipped mod) into its installer.
# Usage: package.sh <target> <build folder> <output file> <build number> <sha7>
#   windows-x64, windows-arm64: NSIS setup .exe (makensis), linux-x64: self-extracting .run (makeself),
#   macos-arm64, macos-x64: the ad hoc signed .app and Mods/ in a .dmg (macOS only: codesign, hdiutil).
set -euo pipefail
shopt -s nullglob

if [ $# -ne 5 ]; then echo "Usage: $0 <target> <build folder> <output file> <build number> <sha7>" >&2; exit 2; fi
target="$1"
build="$(cd "$2" && pwd)"
mkdir -p "$(dirname "$3")"
out="$(cd "$(dirname "$3")" && pwd)/$(basename "$3")"
number="$4"
sha7="$5"
here="$(cd "$(dirname "$0")" && pwd)"
name="Moorhuhn Kart OSE"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

# What an uninstall or update removes: the build's top-level entries and each Mods/<id>, so other files stay.
entries() {
  local entry
  for entry in "$build"/*; do
    entry="${entry##*/}"
    case "$entry" in Mods|*_DoNotShip|*_ButDontShipItWithYourGame) continue ;; esac
    echo "$entry"
  done
  for entry in "$build"/Mods/*/; do entry="${entry%/}"; echo "Mods/${entry##*/}"; done
}

require() {
  if [ ! -e "$build/$1" ]; then echo "$build has no $1" >&2; exit 1; fi
}

windows() {
  require MoorhuhnKart.exe
  local entry
  entries | while IFS= read -r entry; do
    if [ -d "$build/$entry" ]; then echo "RMDir /r \"\$INSTDIR\\${entry//\//\\}\""; else echo "Delete \"\$INSTDIR\\$entry\""; fi
  done > "$work/entries.nsh"
  makensis -V2 -INPUTCHARSET UTF8 -DSOURCE="$build" -DOUTFILE="$out" -DICON="$here/icon.ico" -DARCH="${target#windows-}" \
    -DBUILD="$number" -DSHA7="$sha7" -DENTRIES="$work/entries.nsh" "$here/windows/installer.nsi"
}

linux() {
  require MoorhuhnKart.x86_64
  local payload="$work/payload"
  mkdir -p "$payload/game"
  cp -al "$build/." "$payload/game/" 2> /dev/null || cp -R "$build/." "$payload/game/"
  find "$payload/game" -maxdepth 1 \( -name '*_DoNotShip' -o -name '*_ButDontShipItWithYourGame' \) -exec rm -rf {} +
  entries > "$payload/entries.txt"
  cp "$here/linux/install.sh" "$here/linux/uninstall.sh" "$here/icon.png" "$payload/"
  chmod +x "$payload/install.sh" "$payload/uninstall.sh" "$payload/game/MoorhuhnKart.x86_64"
  makeself --quiet --sha256 "$payload" "$out" "$name build $number | $sha7" ./install.sh
}

macos() {
  require MoorhuhnKart.app
  if [ "$(uname)" != Darwin ]; then echo "The .dmg needs macOS (codesign, hdiutil)" >&2; exit 1; fi
  local folder="$work/dmg/$name"
  mkdir -p "$folder"
  ditto "$build/MoorhuhnKart.app" "$folder/MoorhuhnKart.app"
  if [ -d "$build/Mods" ]; then ditto "$build/Mods" "$folder/Mods"; fi
  find "$folder/MoorhuhnKart.app/Contents/MacOS" -type f -exec chmod +x {} +
  # Bundles built on Linux carry no valid bundle signature, Apple Silicon Macs call such apps damaged.
  codesign --force --deep --sign - "$folder/MoorhuhnKart.app"
  codesign --verify --deep --strict "$folder/MoorhuhnKart.app"
  ln -s /Applications "$work/dmg/Applications"
  hdiutil create -quiet -ov -volname "$name" -srcfolder "$work/dmg" -fs HFS+ -format ULMO "$out"
}

case "$target" in
  windows-x64|windows-arm64) windows ;;
  linux-x64) linux ;;
  macos-arm64|macos-x64) macos ;;
  *) echo "Unknown target $target" >&2; exit 2 ;;
esac
echo "$out: $(du -h "$out" | cut -f1)"
