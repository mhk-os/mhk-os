#!/bin/sh
# Runs UnityYAMLMerge (Unity Smart Merge) of the editor version in ProjectSettings/ProjectVersion.txt, so the git
# merge driver / mergetool keep working after Unity upgrades. Set UNITY_YAML_MERGE to use another binary.
# Configured per clone, see README.md ("Version control").
root=$(git rev-parse --show-toplevel) || exit 1
version=$(sed -n 's/^m_EditorVersion: *//p' "$root/ProjectSettings/ProjectVersion.txt" | tr -d '\r')
tool=$UNITY_YAML_MERGE
if [ -z "$tool" ]; then
  for candidate in \
    "/Applications/Unity/Hub/Editor/$version/Unity.app/Contents/Helpers/UnityYAMLMerge" \
    "/Applications/Unity/Hub/Editor/$version/Unity.app/Contents/Tools/UnityYAMLMerge" \
    "$HOME/Unity/Hub/Editor/$version/Editor/Data/Tools/UnityYAMLMerge" \
    "/c/Program Files/Unity/Hub/Editor/$version/Editor/Data/Tools/UnityYAMLMerge.exe" \
    "C:/Program Files/Unity/Hub/Editor/$version/Editor/Data/Tools/UnityYAMLMerge.exe"; do
    if [ -x "$candidate" ]; then tool=$candidate; break; fi
  done
fi
if [ -z "$tool" ]; then
  echo "unityyamlmerge.sh: UnityYAMLMerge of Unity $version not found; set UNITY_YAML_MERGE" >&2
  exit 1
fi
exec "$tool" "$@"
