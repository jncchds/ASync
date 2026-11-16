#!/usr/bin/env bash
# Builds the Android app (src/ASync.App.Android) as a signed, sideloadable APK (CI's android job runs it).
#
#   scripts/package-android.sh [--version X.Y.Z] [--out DIR] [--abis "android-arm64;android-x64"] [--aab] [--debug]
#
# Output in DIR (default artifacts/packages/android):
#   ASync-<version>-android.apk        one APK for arm64 phones and x86_64 emulators/Chromebooks (Release, signed)
#   ASync-<version>-android.aab        with --aab: an App Bundle as well (for a store later; sideloading needs the APK)
#
# Needs a .NET SDK with the android workload (`dotnet workload install android`), an Android SDK (ANDROID_HOME or
# ~/Android/Sdk: platform 36, build-tools) and a JDK 17. DOTNET=/path/to/dotnet picks the SDK.
#
# Signing: with ASYNC_ANDROID_KEYSTORE (a .jks/.keystore file), ASYNC_ANDROID_KEY_ALIAS, ASYNC_ANDROID_STORE_PASSWORD and
# ASYNC_ANDROID_KEY_PASSWORD set (CI: from secrets), the release key signs it. Otherwise the SDK's debug keystore
# (~/.local/share/Xamarin/Mono for Android/debug.keystore, made on first use) does: fine for trying it out, but Android
# installs an update only over an APK with the same signature, so a debug-signed APK from another machine cannot update
# one installed from here (uninstall first).
set -euo pipefail
cd "$(dirname "$0")/.."

version="$(tr -d '[:space:]' < VERSION)"
out=""
abis="android-arm64;android-x64"
formats="apk"
config="Release"
while [[ $# -gt 0 ]]; do
  case "$1" in
    --version) version="$2"; shift 2 ;;
    --out) out="$2"; shift 2 ;;
    --abis) abis="$2"; shift 2 ;;
    --aab) formats="apk;aab"; shift ;;
    --debug) config="Debug"; shift ;;
    *) echo "unknown option $1" >&2; exit 2 ;;
  esac
done
out="${out:-artifacts/packages/android}"
dotnet="${DOTNET:-dotnet}"
sdk="${ANDROID_HOME:-${ANDROID_SDK_ROOT:-$HOME/Android/Sdk}}"
[[ -d "$sdk" ]] || { echo "error: no Android SDK at $sdk (set ANDROID_HOME)" >&2; exit 2; }
"$dotnet" workload list 2>/dev/null | grep -q '^android ' || { echo "error: the android workload is missing: $dotnet workload install android" >&2; exit 2; }

if [[ -n "${ASYNC_ANDROID_KEYSTORE:-}" ]]; then
  for v in ASYNC_ANDROID_KEY_ALIAS ASYNC_ANDROID_STORE_PASSWORD ASYNC_ANDROID_KEY_PASSWORD; do
    [[ -n "${!v:-}" ]] || { echo "error: ASYNC_ANDROID_KEYSTORE is set but $v is not" >&2; exit 2; }
  done
  echo "Signing with the release keystore ($ASYNC_ANDROID_KEY_ALIAS)."
else
  echo "Signing with the debug keystore (no ASYNC_ANDROID_KEYSTORE)."
fi

build="artifacts/android-build"
rm -rf "$build"
mkdir -p "$out"
# The lists go in as environment variables with commas (the project turns them into ;-lists): -p: would split them, and
# MSBuild keeps a ; from the environment escaped.
AndroidAbis="${abis//;/,}" AndroidPackageFormats="${formats//;/,}" "$dotnet" build src/ASync.App.Android/ASync.App.Android.csproj -c "$config" -o "$build" --nologo \
  -p:Version="$version" -p:AndroidSdkDirectory="$sdk" \
  ${ASYNC_ANDROID_KEYSTORE:+-p:ASYNC_ANDROID_KEYSTORE="$ASYNC_ANDROID_KEYSTORE" -p:ASYNC_ANDROID_KEY_ALIAS="$ASYNC_ANDROID_KEY_ALIAS"}

apk=$(find "$build" -maxdepth 1 -name '*-Signed.apk' | head -1)
[[ -n "$apk" ]] || { echo "error: no signed APK in $build" >&2; ls -la "$build" >&2; exit 1; }
cp "$apk" "$out/ASync-$version-android.apk"
if [[ "$formats" == *aab* ]]; then
  aab=$(find "$build" -maxdepth 1 -name '*-Signed.aab' | head -1)
  [[ -n "$aab" ]] && cp "$aab" "$out/ASync-$version-android.aab"
fi
rm -rf "$build"

# The signature, so a person can compare it with the one the release notes name.
apksigner=$(find "$sdk/build-tools" -name apksigner -type f 2>/dev/null | sort -V | tail -1)
if [[ -n "$apksigner" ]]; then
  "$apksigner" verify --print-certs "$out/ASync-$version-android.apk" | grep -E 'Signer #1 certificate (DN|SHA-256)' || true
fi
ls -la "$out"
