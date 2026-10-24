#!/usr/bin/env bash
#
# Builds MateMacNative.bundle as a universal (x86_64 + arm64) macOS plugin and
# installs it into the Unity project at Assets/Plugins/MacOS/.
#
# Run this on a Mac with the Xcode command line tools installed:
#   xcode-select --install      (one time)
#   ./macos-native/build.sh
#
# Requirements: clang, lipo (both ship with Xcode CLT). No Xcode project needed.
#
# Why universal: a Universal macOS player loads the arm64 slice on Apple silicon
# and the x86_64 slice on Intel, and macOS refuses to dlopen a plugin that has no
# slice for the running architecture. Building one binary keeps both happy.
#
# macOS 27 is the last release that ships Rosetta 2, so an x86_64-only plugin
# bundle would also break on Apple silicon down the road -- hence universal.

set -euo pipefail

cd "$(dirname "$0")"
HERE="$(pwd)"
ROOT="$(cd .. && pwd)"

PLUGIN_NAME="MateMacNative"
BUILD_DIR="$HERE/build"
BUNDLE="$BUILD_DIR/$PLUGIN_NAME.bundle"
INSTALL_DIR="$ROOT/Assets/Plugins/MacOS"
MIN_MACOS="11.0"

# Fixed GUID so the .meta stays stable across rebuilds and across machines.
PLUGIN_GUID="7c1d4a3e9b2f4c6e8a0d5f1b3c7e9a24"

command -v clang >/dev/null 2>&1 || {
  echo "error: clang not found. Install the Xcode command line tools:" >&2
  echo "       xcode-select --install" >&2
  exit 1
}

echo "==> Cleaning"
rm -rf "$BUNDLE"
mkdir -p "$BUNDLE/Contents/MacOS"

echo "==> Compiling (x86_64 + arm64)"
clang \
  -arch x86_64 \
  -arch arm64 \
  -mmacosx-version-min="$MIN_MACOS" \
  -fobjc-arc \
  -fmodules \
  -O2 \
  -Wall \
  -dynamiclib \
  -bundle \
  -install_name "@rpath/$PLUGIN_NAME.bundle/Contents/MacOS/$PLUGIN_NAME" \
  -framework Foundation \
  -framework AppKit \
  -framework CoreGraphics \
  -framework CoreAudio \
  -framework AudioToolbox \
  -framework ServiceManagement \
  -framework ApplicationServices \
  -o "$BUNDLE/Contents/MacOS/$PLUGIN_NAME" \
  MateMacNative.m \
  MateMacAudio.m

echo "==> Writing Info.plist"
cat > "$BUNDLE/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleDevelopmentRegion</key>
  <string>en</string>
  <key>CFBundleExecutable</key>
  <string>$PLUGIN_NAME</string>
  <key>CFBundleIdentifier</key>
  <string>com.shinymoon.mateengine.macnative</string>
  <key>CFBundleInfoDictionaryVersion</key>
  <string>6.0</string>
  <key>CFBundleName</key>
  <string>$PLUGIN_NAME</string>
  <key>CFBundlePackageType</key>
  <string>BNDL</string>
  <key>CFBundleShortVersionString</key>
  <string>1.0</string>
  <key>CFBundleVersion</key>
  <string>1</string>
  <key>LSMinimumSystemVersion</key>
  <string>$MIN_MACOS</string>
</dict>
</plist>
PLIST

echo "==> Verifying architectures"
lipo -info "$BUNDLE/Contents/MacOS/$PLUGIN_NAME"

echo "==> Installing into $INSTALL_DIR"
mkdir -p "$INSTALL_DIR"
rm -rf "$INSTALL_DIR/$PLUGIN_NAME.bundle"
cp -R "$BUNDLE" "$INSTALL_DIR/"

# Unity plugin importer settings: enabled for the macOS editor and the macOS
# standalone player only, and CPU set to AnyCPU so Unity keeps the universal
# binary intact instead of stripping a slice.
cat > "$INSTALL_DIR/$PLUGIN_NAME.bundle.meta" <<META
fileFormatVersion: 2
guid: $PLUGIN_GUID
PluginImporter:
  externalObjects: {}
  serializedVersion: 2
  iconMap: {}
  executionOrder: {}
  defineConstraints: []
  isPreloaded: 0
  isOverridable: 0
  isExplicitlyReferenced: 0
  validateReferences: 1
  platformData:
  - first:
      Any:
    second:
      enabled: 0
      settings: {}
  - first:
      Editor: Editor
    second:
      enabled: 0
      settings:
        DefaultValueInitialized: true
  - first:
      Standalone: OSXUniversal
    second:
      enabled: 1
      settings:
        CPU: AnyCPU
  userData:
  assetBundleName:
  assetBundleVariant:
META

echo
echo "Done."
echo "  bundle : $INSTALL_DIR/$PLUGIN_NAME.bundle"
echo "  meta   : $INSTALL_DIR/$PLUGIN_NAME.bundle.meta"
echo
echo "Commit both files so other contributors do not need Xcode to build."
