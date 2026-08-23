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
    # LIMPEZA OBRIGATORIA antes de publicar.
    #
    # O dotnet publish NAO remove arquivos que sobraram de um publish anterior.
    # Um publish self-contained antigo deixa hostfxr.dll/hostpolicy.dll/coreclr.dll
    # na pasta; ao publicar framework-dependent por cima, o .exe encontra esse
    # host LOCAL antigo em vez do host do sistema, nao consegue localizar o
    # runtime compartilhado e falha com "You must install or update .NET to run
    # this application" — numa maquina que TEM o .NET instalado.
    # Ja aconteceu em campo. Nao remova esta limpeza.
    echo "== Limpando dist/ =="
    rm -rf dist
    mkdir -p dist

    echo "== Publish win-x64 (Release) =="
    dotnet publish src/SmbSpeedDoctor.Cli -c Release -r win-x64 --self-contained false \
        -p:EnableWindowsTargeting=true -o dist
    dotnet publish src/SmbSpeedDoctor.Gui -c Release -r win-x64 --self-contained false \
        -p:EnableWindowsTargeting=true -o dist/Gui

    echo "== Artefatos =="
    echo "-- CLI em dist/ --"
    ls -la dist/*.exe 2>/dev/null || true
    echo "-- GUI em dist/Gui/ --"
    ls -la dist/Gui/*.exe 2>/dev/null || true
    echo
    echo "Publish e FRAMEWORK-DEPENDENT: a maquina alvo precisa do"
    echo ".NET 8 Desktop Runtime (x64). Para endpoints sem .NET, gere"
    echo "self-contained com: ./build.sh publish-selfcontained"
fi

if [[ "${1:-}" == "publish-selfcontained" ]]; then
    # Self-contained: ~150 MB por app, mas nao exige .NET instalado no alvo.
    # Util para deploy em endpoints via RMM.
    echo "== Limpando dist-selfcontained/ =="
    rm -rf dist-selfcontained
    mkdir -p dist-selfcontained

    echo "== Publish win-x64 self-contained (Release) =="
    dotnet publish src/SmbSpeedDoctor.Cli -c Release -r win-x64 --self-contained true \
        -p:EnableWindowsTargeting=true -o dist-selfcontained/Cli
    dotnet publish src/SmbSpeedDoctor.Gui -c Release -r win-x64 --self-contained true \
        -p:EnableWindowsTargeting=true -o dist-selfcontained/Gui

    echo "== Artefatos em dist-selfcontained/ =="
    ls -la dist-selfcontained/Cli/*.exe dist-selfcontained/Gui/*.exe 2>/dev/null || true
fi

echo "== Concluído =="
