#!/usr/bin/env bash
# SMB Speed Doctor — build e publicação win-x64
# Uso: ./build.sh          → Debug + testes
#      ./build.sh publish  → Release win-x64 em dist/
set -euo pipefail
cd "$(dirname "$0")"

export PATH="$HOME/.dotnet:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1

echo "== Restore =="
dotnet restore

echo "== Build =="
dotnet build --no-restore -c Debug

echo "== Testes =="
dotnet test --no-build -c Debug

if [[ "${1:-}" == "publish" ]]; then
    echo "== Publish win-x64 (Release) =="
    dotnet publish src/SmbSpeedDoctor.Cli -c Release -r win-x64 --self-contained false \
        -p:EnableWindowsTargeting=true -o dist
    dotnet publish src/SmbSpeedDoctor.Gui -c Release -r win-x64 --self-contained false \
        -p:EnableWindowsTargeting=true -o dist
    echo "== Artefatos em dist/ =="
    ls -la dist/
fi

echo "== Concluído =="
