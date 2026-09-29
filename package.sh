#!/usr/bin/env bash
# Builds a release archive for Nexus Mods / Vortex.
#
# The archive wraps the files in a folder named after the mod id, matching both the
# game's own loader layout (mods/<id>/<id>.dll) and how existing StS2 mods on Nexus
# are packaged, so it installs correctly through Vortex and by hand.
#
# Usage: ./package.sh [path to "Slay the Spire 2"]
set -euo pipefail

GAME_DIR="${1:-/mnt/t/SteamLibrary/steamapps/common/Slay the Spire 2}"
MOD_ID="DeckSearch"
PROJECT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
VERSION="$(tr -d '[:space:]' < "$PROJECT_DIR/version.txt")"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"

STAGE="$PROJECT_DIR/dist/stage"
OUT="$PROJECT_DIR/dist/$MOD_ID-$VERSION.zip"

# The manifest version is what players see in the game's mod list; keep it honest.
if ! grep -q "\"version\": \"$VERSION\"" "$PROJECT_DIR/$MOD_ID.json"; then
    echo "error: $MOD_ID.json version does not match version.txt ($VERSION)" >&2
    exit 1
fi

"$DOTNET" build "$PROJECT_DIR/$MOD_ID.csproj" -c Release -p:Sts2Dir="$GAME_DIR"

rm -rf "$STAGE" "$OUT"
mkdir -p "$STAGE/$MOD_ID"
cp "$PROJECT_DIR/bin/Release/net9.0/$MOD_ID.dll" "$STAGE/$MOD_ID/"
cp "$PROJECT_DIR/$MOD_ID.json" "$STAGE/$MOD_ID/"
cp "$PROJECT_DIR/README.md" "$STAGE/$MOD_ID/"
cp "$PROJECT_DIR/LICENSE" "$STAGE/$MOD_ID/"
cp "$PROJECT_DIR/$MOD_ID.config.jsonc" "$STAGE/$MOD_ID/"

# Python's zipfile rather than the zip binary, which is not installed by default on WSL.
python3 - "$STAGE" "$OUT" <<'PYEOF'
import os, sys, zipfile

stage, out = sys.argv[1], sys.argv[2]
with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as archive:
    for root, _, files in os.walk(stage):
        for name in sorted(files):
            full = os.path.join(root, name)
            archive.write(full, os.path.relpath(full, stage).replace(os.sep, "/"))

with zipfile.ZipFile(out) as archive:
    for info in archive.infolist():
        print(f"  {info.file_size:>8}  {info.filename}")
PYEOF

rm -rf "$STAGE"
echo "Built $OUT"
