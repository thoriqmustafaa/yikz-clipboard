#!/usr/bin/env bash
set -euo pipefail

if [ $# -lt 1 ]; then
    echo "usage: $0 <version> [changelog]" >&2
    exit 2
fi

version="$1"
changelog="${2:-CHANGELOG.md}"

notes="$(awk -v ver="$version" '
    BEGIN { found = 0; inside = 0 }
    /^## / {
        if (inside) { exit }
        if (index($0, "## " ver " (") == 1) { found = 1; inside = 1; next }
    }
    inside { print }
    END { if (!found) exit 3 }
' "$changelog")" || {
    echo "version $version not found in $changelog" >&2
    exit 1
}

notes="$(printf '%s\n' "$notes" | sed -e '/./,$!d' | awk '{ lines[NR] = $0 } END { last = NR; while (last > 0 && lines[last] ~ /^[[:space:]]*$/) last--; for (i = 1; i <= last; i++) print lines[i] }')"

if [ -z "$notes" ]; then
    echo "notes for $version are empty" >&2
    exit 1
fi

printf '%s\n' "$notes"
