#!/bin/sh
set -eu
repo_dir=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
cd "$repo_dir/examples/BackRazor.EdgeNode"
exec "$repo_dir/.dotnet/dotnet" run --project BackRazor.EdgeNode.csproj -- --urls http://127.0.0.1:5080 "$@"
