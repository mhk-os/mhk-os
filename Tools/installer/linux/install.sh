#!/usr/bin/env bash
# Startup script of the self-extracting Linux installer (makeself runs it in the extracted folder). Asks for the
# install folder, copies the game, adds a menu entry and uninstall.sh. Options after "--": --dir <folder>, --yes.
set -euo pipefail

name="Moorhuhn Kart OSE"
exe="MoorhuhnKart.x86_64"
default="$HOME/Games/$name"
dir=""
yes=0
while [ $# -gt 0 ]; do
  case "$1" in
    --dir) dir="${2:?--dir needs a folder}"; shift 2 ;;
    --yes) yes=1; shift ;;
    *) echo "Unknown option $1 (use --dir <folder> and --yes)" >&2; exit 2 ;;
  esac
done

if [ -z "$dir" ]; then
  if [ "$yes" = 0 ] && [ -t 0 ]; then read -r -p "Install folder [$default]: " dir; fi
  dir="${dir:-$default}"
fi
case "$dir" in \~) dir="$HOME" ;; \~/*) dir="$HOME/${dir#\~/}" ;; esac
mkdir -p "$dir"
dir="$(cd "$dir" && pwd)"

# A folder holding other files is only accepted when it is an earlier install: its uninstaller removes the old build.
if [ -x "$dir/uninstall.sh" ]; then
  "$dir/uninstall.sh" --yes > /dev/null
  mkdir -p "$dir"
elif [ -n "$(ls -A "$dir")" ]; then
  echo "$dir is not empty. Choose an empty or new folder, or an earlier install of $name." >&2
  exit 1
fi

echo "Installing $name into $dir"
cp -R game/. "$dir/"
cp uninstall.sh entries.txt "$dir/"
cp icon.png "$dir/MoorhuhnKart.png"
chmod +x "$dir/$exe" "$dir/uninstall.sh"

apps="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
mkdir -p "$apps"
quoted="$(printf '%s' "$dir/$exe" | sed -e 's/\\/\\\\\\\\/g' -e 's/["`$]/\\\\&/g')"
cat > "$apps/org.mhkose.moorhuhnkart.desktop" << EOF
[Desktop Entry]
Type=Application
Name=$name
Comment=Open source remake of Moorhuhn Kart
Exec="$quoted"
Path=$dir
Icon=$dir/MoorhuhnKart.png
Terminal=false
Categories=Game;
EOF
if command -v update-desktop-database > /dev/null; then update-desktop-database "$apps" > /dev/null 2>&1 || true; fi

echo "Done. $name is in your application menu. Uninstall with: \"$dir/uninstall.sh\""
