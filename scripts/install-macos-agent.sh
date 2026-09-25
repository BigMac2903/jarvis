#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/../agents/macos"
command -v swift >/dev/null || { echo "Xcode Command Line Tools zuerst installieren: xcode-select --install"; exit 1; }
swift build -c release
target="$HOME/Library/Application Support/Jarvis/bin"
mkdir -p "$target"
cp .build/release/jarvis-mac "$target/jarvis-mac"
echo "Installiert: $target/jarvis-mac"
echo "Zum Pairing ausführen. Danach config.json und macOS-Datenschutzfreigaben prüfen."
