#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)"
deploy=false

usage() {
  echo "usage: ./build.sh [--ssh-deploy]" >&2
  exit 1
}

while (( $# )); do
  case "$1" in
    --ssh-deploy)
      deploy=true
      shift
      ;;
    *)
      usage
      ;;
  esac
done

refs="$root/refs"
output_dir="$root/src/bin/Release"
output_dll="$output_dir/OpenXRUnderswingFix.dll"
output_pdb="$output_dir/OpenXRUnderswingFix.pdb"

[[ -f "$refs/Beat Saber_Data/Managed/IPA.Loader.dll" ]] || {
  echo "Refs are incomplete at $refs; expected Beat Saber_Data/Managed/IPA.Loader.dll." >&2
  exit 1
}

rm -rf "$output_dir/Artifact" "$output_dir/zip"

dotnet build "$root/OpenXRUnderswingFix.sln" -c Release \
  -p:GameReferences="$refs" \
  -p:DisableCopyToGame=True

$deploy || exit 0

prop() { sed -nE "s:.*<$1[^>]*>(.*)</$1>.*:\\1:p" "$root/Directory.Build.local.props" 2>/dev/null | tail -n 1; }

target="$(prop OpenXRUnderswingFixSshTarget)"
game="$(prop OpenXRUnderswingFixSshBeatSaberDir)"
[[ -n "$target" && -n "$game" ]] || {
  echo "Directory.Build.local.props must set OpenXRUnderswingFixSshTarget and OpenXRUnderswingFixSshBeatSaberDir." >&2
  exit 1
}

remote_dir="${game}\\Plugins"
remote_dll="${remote_dir}\\OpenXRUnderswingFix.dll"
remote_pdb="${remote_dir}\\OpenXRUnderswingFix.pdb"
remote_staging_dll="OpenXRUnderswingFix.deploy.dll"
remote_staging_pdb="OpenXRUnderswingFix.deploy.pdb"
mkdir_command="\$ErrorActionPreference = 'Stop'; \$ProgressPreference = 'SilentlyContinue'; New-Item -ItemType Directory -Force -Path '$remote_dir' | Out-Null; [void]0"
ps_encode() { printf "%s" "$1" | iconv -f UTF-8 -t UTF-16LE | base64 | tr -d '\n'; }

ssh "$target" powershell -NoProfile -NonInteractive -EncodedCommand "$(ps_encode "$mkdir_command")"
[[ -f "$output_dll" ]] || {
  echo "Build output missing: $output_dll" >&2
  exit 1
}
scp "$output_dll" "$target:$remote_staging_dll"
copy_command="\$ErrorActionPreference = 'Stop'; \$ProgressPreference = 'SilentlyContinue'; Copy-Item -Force -Path (Join-Path \$env:USERPROFILE '$remote_staging_dll') -Destination '$remote_dll'; Remove-Item -Force -ErrorAction SilentlyContinue -Path (Join-Path \$env:USERPROFILE '$remote_staging_dll')"
if [[ -f "$output_pdb" ]]; then
  scp "$output_pdb" "$target:$remote_staging_pdb"
  copy_command="$copy_command; Copy-Item -Force -Path (Join-Path \$env:USERPROFILE '$remote_staging_pdb') -Destination '$remote_pdb'; Remove-Item -Force -ErrorAction SilentlyContinue -Path (Join-Path \$env:USERPROFILE '$remote_staging_pdb')"
fi
copy_command="$copy_command; [void]0"
ssh "$target" powershell -NoProfile -NonInteractive -EncodedCommand "$(ps_encode "$copy_command")"
