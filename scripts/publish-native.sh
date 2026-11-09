#!/bin/sh
set -eu
repo_dir=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
cd "$repo_dir"
if [ "$#" -ne 1 ]; then
    echo "Usage: scripts/publish-native.sh <runtime-id> (e.g. osx-arm64, linux-arm64)" >&2
    exit 1
fi
exec .dotnet/dotnet publish examples/BackRazor.EdgeNode/BackRazor.EdgeNode.csproj -c Release -r "$1" -o artifacts/native -p:OptimizationPreference=Size
