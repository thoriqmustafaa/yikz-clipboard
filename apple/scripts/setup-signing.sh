#!/bin/bash
set -euo pipefail

DIR="$HOME/.config/yikz-clipboard"
P12="${1:-$DIR/macos-signing.p12}"
P12_PASSWORD_FILE="$DIR/macos-cert-password.txt"
KEYCHAIN="$DIR/signing.keychain-db"
KEYCHAIN_PASSWORD_FILE="$DIR/signing-keychain-password.txt"

[ -f "$P12" ] || { echo "missing $P12" >&2; exit 1; }
[ -f "$P12_PASSWORD_FILE" ] || { echo "missing $P12_PASSWORD_FILE" >&2; exit 1; }

if [ ! -f "$KEYCHAIN_PASSWORD_FILE" ]; then
    umask 077
    openssl rand -hex 16 > "$KEYCHAIN_PASSWORD_FILE"
fi
KEYCHAIN_PASSWORD="$(cat "$KEYCHAIN_PASSWORD_FILE")"

if [ -f "$KEYCHAIN" ]; then
    security delete-keychain "$KEYCHAIN" 2>/dev/null || rm -f "$KEYCHAIN"
fi
security create-keychain -p "$KEYCHAIN_PASSWORD" "$KEYCHAIN"
security set-keychain-settings "$KEYCHAIN"
security unlock-keychain -p "$KEYCHAIN_PASSWORD" "$KEYCHAIN"
security import "$P12" -k "$KEYCHAIN" -P "$(cat "$P12_PASSWORD_FILE")" -T /usr/bin/codesign -A
security set-key-partition-list -S apple-tool:,apple:,codesign: -s -k "$KEYCHAIN_PASSWORD" "$KEYCHAIN" >/dev/null
security find-identity "$KEYCHAIN"
echo "Signing keychain ready at $KEYCHAIN"
