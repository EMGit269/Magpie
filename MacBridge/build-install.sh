#!/bin/bash
set -euo pipefail
bridge_dir="$(cd "$(dirname "$0")" && pwd)"
dotnet_bin="${DOTNET:-dotnet}"
if ! command -v "$dotnet_bin" >/dev/null 2>&1; then
  echo 'Install the .NET 8 SDK or set DOTNET to its executable path.' >&2
  exit 1
fi
"$dotnet_bin" build "$bridge_dir/Magpie.MacBridge.csproj" --configuration Release "$@"
library_dir="${MAGPIE_GH_LIBRARIES:-$HOME/Library/Application Support/McNeel/Rhinoceros/8.0/Plug-ins/Grasshopper (b45a29b1-4343-4035-989e-044e8580d9cf)/Libraries}"
install -d "$library_dir/Magpie.MacBridge"
install -m 644 "$bridge_dir/bin/Release/net8.0/Magpie.MacBridge.gha" "$library_dir/Magpie.MacBridge/"
python3 "$bridge_dir/configure_service.py"
echo "Installed: $library_dir/Magpie.MacBridge/Magpie.MacBridge.gha"
echo 'Restart Rhino 8, open Grasshopper, then choose Magpie > 打开 Agent.'
