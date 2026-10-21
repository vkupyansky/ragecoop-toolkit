#!/usr/bin/env bash
set -euo pipefail

# Public source check: tools/check-public.sh [CrossOver bottle name]
script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
root_dir="$(cd -- "$script_dir/.." && pwd)"
project_dir="$root_dir/work/RageCoopAdmin"
output_root="$root_dir/outputs/public-build"
bottle_name="${1:-Rockstar Games Launcher}"
if [[ $# -gt 1 ]]; then
    printf '%s\n' 'Usage: tools/check-public.sh [CrossOver bottle name]' >&2
    exit 2
fi
if ! command -v dotnet >/dev/null 2>&1 || ! command -v python3 >/dev/null 2>&1; then
    printf '%s\n' 'Put the SDK pinned in global.json and Python 3.11+ on PATH.' >&2
    exit 2
fi
if [[ ! -f "$project_dir/References.props" ]]; then
    printf '%s\n' 'Create References.props from References.example.props with matching dependency paths.' >&2
    exit 2
fi
platform_name="$(uname -s)"
case "$platform_name" in
    Darwin)
        wine_path='/Applications/CrossOver.app/Contents/SharedSupport/CrossOver/bin/wine'
        if [[ ! -x "$wine_path" ]]; then
            printf '%s\n' 'CrossOver/.NET Framework 4.8 is required for the actual client assembly check.' >&2
            exit 2
        fi
        ;;
    MINGW*|MSYS*|CYGWIN*) ;;
    *)
        printf '%s\n' 'The actual client assembly check requires Windows or macOS with CrossOver.' >&2
        exit 2
        ;;
esac

cd -- "$project_dir"
for component in Client Server Bootstrap EarlyClient; do
    case "$component" in
        Client) project='Client/RageCoopAdmin.Client.csproj' ;;
        Server) project='Server/CoopAdmin.Server.csproj' ;;
        Bootstrap) project='Bootstrap/CoopAdmin.Startup.csproj' ;;
        EarlyClient) project='EarlyClient/RageCoop.EarlyClient.csproj' ;;
    esac
    dotnet restore "$project" --locked-mode -warnaserror -p:PublicRelease=true
    dotnet build "$project" -c Release --no-restore -warnaserror -p:PublicRelease=true \
        "-p:BaseOutputPath=$output_root/$component/"
done
dotnet format whitespace . --folder --verify-no-changes --verbosity quiet \
    --include Client Server Shared Bootstrap EarlyClient Checks --exclude Client/bin Client/obj Server/bin Server/obj Bootstrap/bin Bootstrap/obj EarlyClient/bin EarlyClient/obj

project='Checks/AddonVehicles/AddonVehiclesCheck.csproj'
dotnet restore "$project" --locked-mode -warnaserror -p:PublicRelease=true
dotnet build "$project" -c Release --no-restore -warnaserror -p:PublicRelease=true \
    "-p:BaseOutputPath=$output_root/AddonVehicles/"
dotnet "$output_root/AddonVehicles/Release/net10.0/AddonVehiclesCheck.dll"

project='Checks/EngineComposition/EngineCompositionCheck.csproj'
dotnet restore "$project" --locked-mode -warnaserror -p:PublicRelease=true
dotnet build "$project" -c Release --no-restore -warnaserror -p:PublicRelease=true \
    "-p:ClientResourcePath=$output_root/Client/Release/net48/RageCoopAdmin.Client.dll" \
    "-p:BaseOutputPath=$output_root/EngineComposition/"
runner="$output_root/EngineComposition/Release/net48/EngineCompositionCheck.exe"
if [[ "$platform_name" == 'Darwin' ]]; then
    "$wine_path" --bottle "$bottle_name" --no-update --no-gui "$runner" --public-release
    "$wine_path" --bottle "$bottle_name" --no-update --no-gui "$runner" --public-engine
else
    "$runner" --public-release
    "$runner" --public-engine
fi
cd -- "$root_dir"
python3 -W error -m unittest discover -s tools -p test_prepare_public_release.py
printf '%s\n' 'Public builds and targeted public catalog/client/archive checks passed. GTA and a live server were not exercised.'
