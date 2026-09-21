#!/bin/bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

APP_NAME="YikzClipboard"
DISPLAY_NAME="Yikz Clipboard"
BUNDLE_ID="dev.yikz.clipboard"
VERSION="$(tr -d '[:space:]' < VERSION)"
BUILD_NUMBER="${BUILD_NUMBER:-$(git rev-list --count HEAD 2>/dev/null || echo 1)}"
OUT="$ROOT/build"
APP="$OUT/$APP_NAME.app"
ZIP="$OUT/$APP_NAME.zip"
WORK="$OUT/work"

rm -rf "$APP" "$ZIP" "$WORK"
mkdir -p "$WORK"

echo "==> Building $APP_NAME $VERSION ($BUILD_NUMBER)"
BINARY=""
if swift build -c release --arch arm64 --arch x86_64 --product "$APP_NAME" >"$WORK/universal.log" 2>&1; then
    BIN_DIR="$(swift build -c release --arch arm64 --arch x86_64 --product "$APP_NAME" --show-bin-path)"
    BINARY="$BIN_DIR/$APP_NAME"
    echo "    universal build ok"
else
    echo "    universal build failed, trying per-architecture builds"
    swift build -c release --arch arm64 --product "$APP_NAME"
    ARM_BIN="$(swift build -c release --arch arm64 --product "$APP_NAME" --show-bin-path)/$APP_NAME"
    cp "$ARM_BIN" "$WORK/$APP_NAME-arm64"
    if swift build -c release --arch x86_64 --product "$APP_NAME" >"$WORK/x86.log" 2>&1; then
        X86_BIN="$(swift build -c release --arch x86_64 --product "$APP_NAME" --show-bin-path)/$APP_NAME"
        lipo -create "$WORK/$APP_NAME-arm64" "$X86_BIN" -output "$WORK/$APP_NAME"
        echo "    combined arm64 and x86_64 with lipo"
    else
        cp "$WORK/$APP_NAME-arm64" "$WORK/$APP_NAME"
        echo "    x86_64 build unavailable, shipping arm64 only"
    fi
    BINARY="$WORK/$APP_NAME"
fi
[ -f "$BINARY" ] || { echo "binary not found: $BINARY" >&2; exit 1; }
echo "    architectures: $(lipo -archs "$BINARY")"

echo "==> Assembling bundle"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BINARY" "$APP/Contents/MacOS/$APP_NAME"
chmod 755 "$APP/Contents/MacOS/$APP_NAME"
strip -x "$APP/Contents/MacOS/$APP_NAME" 2>/dev/null || true
printf 'APPL????' > "$APP/Contents/PkgInfo"

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleDevelopmentRegion</key>
    <string>en</string>
    <key>CFBundleExecutable</key>
    <string>$APP_NAME</string>
    <key>CFBundleIdentifier</key>
    <string>$BUNDLE_ID</string>
    <key>CFBundleInfoDictionaryVersion</key>
    <string>6.0</string>
    <key>CFBundleName</key>
    <string>$DISPLAY_NAME</string>
    <key>CFBundleDisplayName</key>
    <string>$DISPLAY_NAME</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleShortVersionString</key>
    <string>$VERSION</string>
    <key>CFBundleVersion</key>
    <string>$BUILD_NUMBER</string>
    <key>CFBundleIconFile</key>
    <string>AppIcon</string>
    <key>CFBundleIconName</key>
    <string>AppIcon</string>
    <key>LSMinimumSystemVersion</key>
    <string>14.0</string>
    <key>LSUIElement</key>
    <true/>
    <key>LSApplicationCategoryType</key>
    <string>public.app-category.productivity</string>
    <key>NSHighResolutionCapable</key>
    <true/>
    <key>NSPrincipalClass</key>
    <string>NSApplication</string>
    <key>NSSupportsAutomaticTermination</key>
    <false/>
    <key>NSSupportsSuddenTermination</key>
    <false/>
    <key>NSHumanReadableCopyright</key>
    <string>Copyright 2026 Thoriq Mustafa Akmal. All rights reserved.</string>
</dict>
</plist>
PLIST
plutil -lint "$APP/Contents/Info.plist" >/dev/null

echo "==> Generating icon"
ICONSET="$WORK/AppIcon.iconset"
mkdir -p "$ICONSET"
swiftc -O "$ROOT/scripts/make_icon.swift" -o "$WORK/make_icon" 2>"$WORK/icon.log" || { cat "$WORK/icon.log" >&2; exit 1; }
"$WORK/make_icon" "$WORK/icon-1024.png"
for s in 16 32 128 256 512; do
    sips -z "$s" "$s" "$WORK/icon-1024.png" --out "$ICONSET/icon_${s}x${s}.png" >/dev/null
    d=$((s * 2))
    sips -z "$d" "$d" "$WORK/icon-1024.png" --out "$ICONSET/icon_${s}x${s}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/AppIcon.icns"

echo "==> Signing (ad-hoc)"
codesign --force --sign - --timestamp=none \
    --identifier "$BUNDLE_ID" \
    --entitlements "$ROOT/scripts/YikzClipboard.entitlements" \
    "$APP"
codesign --verify --strict --verbose=1 "$APP"

echo "==> Packaging"
ditto -c -k --sequesterRsrc --keepParent "$APP" "$ZIP"
rm -rf "$WORK"

APP_SIZE="$(du -sh "$APP" | cut -f1)"
ZIP_SIZE="$(du -sh "$ZIP" | cut -f1)"
echo "==> Done"
echo "    app: $APP ($APP_SIZE)"
echo "    zip: $ZIP ($ZIP_SIZE)"
