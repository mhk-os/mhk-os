#!/usr/bin/env bash
# Removes the Moorhuhn Kart OSE install this script belongs to: the build's entries listed in entries.txt, the menu
# entry and the folder when nothing else is left in it. The player's data in ~/.config/unity3d stays. Option: --yes.
set -euo pipefail

dir="$(cd "$(dirname "$0")" && pwd)"
if [ "${1:-}" != "--yes" ]; then
  read -r -p "Remove Moorhuhn Kart OSE from $dir? [y/N] " answer
  case "$answer" in y|Y|yes|Yes) ;; *) exit 1 ;; esac
fi

while IFS= read -r entry; do
  case "$entry" in ""|/*|*..*) continue ;; esac
  rm -rf "${dir:?}/$entry"
done < "$dir/entries.txt"
rm -f "$dir/MoorhuhnKart.png" "$dir/entries.txt" "$dir/uninstall.sh"
desktop="${XDG_DATA_HOME:-$HOME/.local/share}/applications/org.mhkose.moorhuhnkart.desktop"
if [ -f "$desktop" ] && grep -qxF "Path=$dir" "$desktop"; then rm -f "$desktop"; fi
rmdir "$dir/Mods" 2> /dev/null || true
rmdir "$dir" 2> /dev/null || true
echo "Moorhuhn Kart OSE was removed from $dir"
