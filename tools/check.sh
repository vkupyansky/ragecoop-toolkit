#!/usr/bin/env bash
set -euo pipefail

# Usage: tools/check.sh [--public-release] [CrossOver bottle name]
# The SDK version is pinned by work/RageCoopAdmin/global.json.
script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
if [[ "${1-}" == '--public-release' ]]; then
    shift
    exec bash "$script_dir/check-public.sh" "$@"
fi
project_dir="$(cd -- "$script_dir/../work/RageCoopAdmin" && pwd)"
bottle_name="${1:-Rockstar Games Launcher}"

if [[ $# -gt 1 ]]; then
    printf '%s\n' 'Usage: tools/check.sh [--public-release] [CrossOver bottle name]' >&2
    exit 2
fi
if ! command -v dotnet >/dev/null 2>&1; then
    printf '%s\n' 'Install the SDK version in work/RageCoopAdmin/global.json and put dotnet on PATH.' >&2
    exit 2
fi
if [[ ! -f "$project_dir/References.props" ]]; then
    printf '%s\n' 'Create work/RageCoopAdmin/References.props from References.example.props with your installed dependency paths.' >&2
    exit 2
fi

cd -- "$project_dir"
for component in Client/RageCoopAdmin.Client.csproj Server/CoopAdmin.Server.csproj Bootstrap/CoopAdmin.Startup.csproj EarlyClient/RageCoop.EarlyClient.csproj; do
    dotnet restore "$component" --locked-mode -warnaserror
    dotnet build "$component" -c Release --no-restore -warnaserror
    dotnet format whitespace "$component" --no-restore --verify-no-changes --verbosity quiet
done

platform_name="$(uname -s)"
for check_project in Checks/*/*.csproj; do
    [[ -f "$check_project" ]] || continue
    dotnet restore "$check_project" --locked-mode -warnaserror
    dotnet build "$check_project" -c Release --no-restore -warnaserror
    framework="$(dotnet msbuild "$check_project" -nologo -getProperty:TargetFramework)"
    assembly_name="$(dotnet msbuild "$check_project" -nologo -getProperty:AssemblyName)"
    output_dir="$(dirname -- "$check_project")/bin/Release/$framework"
    if [[ "$framework" == 'net48' ]]; then
        case "$platform_name" in
            Darwin)
                wine_path='/Applications/CrossOver.app/Contents/SharedSupport/CrossOver/bin/wine'
                if [[ ! -x "$wine_path" ]]; then
                    printf '%s\n' 'CrossOver is required to run the .NET Framework checks on macOS.' >&2
                    exit 2
                fi
                "$wine_path" --bottle "$bottle_name" --no-update --no-gui "$output_dir/$assembly_name.exe"
                ;;
            MINGW*|MSYS*|CYGWIN*) "$output_dir/$assembly_name.exe" ;;
            *)
                printf '%s\n' 'The .NET Framework checks need Windows or macOS with CrossOver.' >&2
                exit 2
                ;;
        esac
    else
        if [[ "$platform_name" == 'Darwin' && -f "$output_dir/ScriptHookVDotNet3.dll" ]]; then
            # SHVDN's managed assembly is x64, just like the installed RageCoop server.
            server_reference_dir="$(dotnet msbuild Server/CoopAdmin.Server.csproj -nologo -getProperty:RageCoopServerDir)"
            server_runtime="$server_reference_dir/.dotnet/dotnet"
            if [[ ! -x "$server_runtime" ]]; then
                printf '%s\n' 'The protocol check needs the x64 runtime used by the Mac server at RageCoopServerDir/.dotnet/dotnet.' >&2
                exit 2
            fi
            arch -x86_64 "$server_runtime" "$output_dir/$assembly_name.dll"
        else
            dotnet "$output_dir/$assembly_name.dll"
        fi
    fi
done

printf '%s\n' 'All resource builds, the server bootstrap and isolated checks passed.'
