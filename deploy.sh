#!/usr/bin/env bash
# Builds the mod and installs it into the game's mods directory.
#
# Usage: ./deploy.sh [path to "Slay the Spire 2"]
set -euo pipefail

GAME_DIR="${1:-/mnt/t/SteamLibrary/steamapps/common/Slay the Spire 2}"
MOD_ID="DeckSearch"
PROJECT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"

if [[ ! -f "$GAME_DIR/data_sts2_windows_x86_64/sts2.dll" ]]; then
    echo "error: no StS2 install at '$GAME_DIR'" >&2
    exit 1
fi

TARGET="$GAME_DIR/mods/$MOD_ID"

"$DOTNET" build "$PROJECT_DIR/DeckSearch.csproj" -c Release -p:Sts2Dir="$GAME_DIR"

mkdir -p "$TARGET"

# Windows keeps the DLL locked while the game is running, so the copy fails and you end up
# testing the previous build without noticing. Say so plainly.
if ! cp "$PROJECT_DIR/bin/Release/net9.0/$MOD_ID.dll" "$TARGET/" 2>/dev/null; then
    echo "error: could not replace $MOD_ID.dll - close Slay the Spire 2 first (it locks the file while running)." >&2
    exit 1
fi
cp "$PROJECT_DIR/$MOD_ID.json" "$TARGET/"

# Never clobber a config that has already been tuned. New settings do not need to be added
# here by hand: any key missing from the file falls back to the code's default.
if [[ ! -f "$TARGET/$MOD_ID.config.jsonc" ]]; then
    cp "$PROJECT_DIR/$MOD_ID.config.jsonc" "$TARGET/"
else
    echo "Kept the existing $MOD_ID.config.jsonc (new settings fall back to their defaults)."
fi

echo "Installed $MOD_ID to $TARGET"
ls -la "$TARGET"
