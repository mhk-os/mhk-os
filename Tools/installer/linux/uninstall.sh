#!/usr/bin/env bash
# Removes the Moorhuhn Kart OSE install this script belongs to: the build's entries listed in entries.txt, the menu
# entry with its mhkos:// link handler and the folder when nothing else is left in it. The player's data in ~/.config/unity3d stays. Option: --yes.
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
if [ -f "$desktop" ] && grep -qxF "Path=$dir" "$desktop"; then
  rm -f "$desktop"
  # The default handler entry of the scheme points at the removed menu entry: drop it.
  mimeapps="${XDG_CONFIG_HOME:-$HOME/.config}/mimeapps.list"
  if [ -f "$mimeapps" ]; then sed -i '/^x-scheme-handler\/mhkos=org\.mhkose\.moorhuhnkart\.desktop;\?$/d' "$mimeapps"; fi
  apps="$(dirname "$desktop")"
  if command -v update-desktop-database > /dev/null; then update-desktop-database "$apps" > /dev/null 2>&1 || true; fi
fi
rmdir "$dir/Mods" 2> /dev/null || true
rmdir "$dir" 2> /dev/null || true
echo "Moorhuhn Kart OSE was removed from $dir"
