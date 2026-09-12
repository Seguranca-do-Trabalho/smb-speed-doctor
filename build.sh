#!/usr/bin/env bash
# SMB Speed Doctor — build and win-x64 publication
# Usage: ./build.sh          → Debug + tests
#        ./build.sh publish  → Release win-x64 in dist/
set -euo pipefail
cd "$(dirname "$0")"

export PATH="$HOME/.dotnet:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1

echo "== Restore =="
dotnet restore

echo "== Build =="
dotnet build --no-restore -c Debug

echo "== Tests =="
dotnet test --no-build -c Debug

if [[ "${1:-}" == "publish" ]]; then
    # MANDATORY CLEANUP before publishing.
    #
    # dotnet publish does NOT remove files left over from previous publishes.
    # An older self-contained publish leaves hostfxr.dll/hostpolicy.dll/coreclr.dll
    # in the directory; publishing framework-dependent over it causes the .exe to find
    # that local host instead of the system host, failing with
    # "You must install or update .NET to run this application" — even on machines with .NET installed.
    echo "== Cleaning dist/ =="
    rm -rf dist
    mkdir -p dist

    echo "== Publish win-x64 (Release) =="
    dotnet publish src/SmbSpeedDoctor.Cli -c Release -r win-x64 --self-contained false \
        -p:EnableWindowsTargeting=true -o dist
    dotnet publish src/SmbSpeedDoctor.Gui -c Release -r win-x64 --self-contained false \
        -p:EnableWindowsTargeting=true -o dist/Gui

    echo "== Artifacts =="
    echo "-- CLI in dist/ --"
    ls -la dist/*.exe 2>/dev/null || true
    echo "-- GUI in dist/Gui/ --"
    ls -la dist/Gui/*.exe 2>/dev/null || true
    echo
    echo "Publish is FRAMEWORK-DEPENDENT: target machine requires"
    echo ".NET 8 Desktop Runtime (x64). For endpoints without .NET, generate"
    echo "self-contained build using: ./build.sh publish-selfcontained"
fi

if [[ "${1:-}" == "publish-selfcontained" ]]; then
    # Self-contained: ~150 MB per app, but does not require .NET installed on target.
    # Useful for endpoint deployments via RMM.
    echo "== Cleaning dist-selfcontained/ =="
    rm -rf dist-selfcontained
    mkdir -p dist-selfcontained

    echo "== Publish win-x64 self-contained (Release) =="
    dotnet publish src/SmbSpeedDoctor.Cli -c Release -r win-x64 --self-contained true \
        -p:EnableWindowsTargeting=true -o dist-selfcontained/Cli
    dotnet publish src/SmbSpeedDoctor.Gui -c Release -r win-x64 --self-contained true \
        -p:EnableWindowsTargeting=true -o dist-selfcontained/Gui

    echo "== Artifacts in dist-selfcontained/ =="
    ls -la dist-selfcontained/Cli/*.exe dist-selfcontained/Gui/*.exe 2>/dev/null || true
fi

echo "== Done =="
