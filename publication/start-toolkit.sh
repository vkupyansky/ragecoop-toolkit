#!/bin/sh
set -eu
server_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
cd "$server_dir"
for file in RageCoop.Server.dll Bootstrap/CoopAdmin.Startup.dll Bootstrap/0Harmony.dll; do
    if [ ! -f "$file" ]; then
        printf '%s\n' "Missing $file. Install the compatible server and complete addon." >&2
        exit 1
    fi
done
command -v dotnet >/dev/null 2>&1 || {
    printf '%s\n' 'Install the x64 .NET 10 runtime and put dotnet on PATH.' >&2
    exit 1
}
DOTNET_STARTUP_HOOKS="$server_dir/Bootstrap/CoopAdmin.Startup.dll"
DOTNET_ROLL_FORWARD=Major
export DOTNET_STARTUP_HOOKS DOTNET_ROLL_FORWARD
exec dotnet RageCoop.Server.dll
