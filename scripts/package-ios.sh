#!/usr/bin/env bash
# Builds the iOS app (src/ASync.App.iOS) on a Mac with Xcode and the ios workload (.github/workflows/ios.yml does this on
# GitHub's macOS runners; on Linux, scripts/check-ios.sh only compiles the C#). Into artifacts/packages/ios:
#
#   scripts/package-ios.sh               ASync-<version>-unsigned.ipa: Release for devices (ios-arm64), not signed, for
#                                        sideloading with SideStore or AltStore (they sign it with the person's Apple ID)
#   scripts/package-ios.sh --simulator   ASync-<version>-simulator.app.zip: Release for the simulator of this Mac's CPU
#                                        (iossimulator-arm64 or -x64), signed ad hoc as the simulator needs; the .app is left
#                                        in artifacts/packages/ios/simulator/ASync.app for `xcrun simctl install`
#   scripts/package-ios.sh --signed      ASync-<version>.ipa signed for App Store Connect / TestFlight, with the identity
#                                        and provisioning profile already in the keychain (ios.yml's signing step):
#                                        ASYNC_IOS_CODESIGN_KEY ("Apple Distribution: …") and ASYNC_IOS_PROVISION (the
#                                        profile's name or UUID). With the push entitlement (aps-environment, production:
#                                        Entitlements.Push.plist, -p:AsyncPush=true): the App ID and the profile need the Push
#                                        Notifications capability. ASYNC_IOS_PUSH=0 builds it without (a profile without push).
#                                        ASYNC_IOS_FP_PROVISION: the File Provider extension's own profile
#                                        (ua.pp.chds.async.fileprovider, with the app group and the Keychain group).
#
# The unsigned .ipa and the simulator build never carry aps-environment: the app then does not register for APNs at all.
# Every mode embeds the File Provider extension (task 25) as PlugIns/ASyncFileProvider.appex; its size is printed. Unsigned,
# neither the app nor the extension has the app group, so the app registers no domain (Settings says why).
#
# The version comes from VERSION (CFBundleShortVersionString and CFBundleVersion).
set -euo pipefail
cd "$(dirname "$0")/.."

mode=unsigned
case "${1:-}" in
  "") ;;
  --simulator) mode=simulator ;;
  --signed) mode=signed ;;
  *) echo "usage: $0 [--simulator|--signed]" >&2; exit 2 ;;
esac
[[ "$(uname -s)" == Darwin ]] || { echo "error: the iOS app builds only on macOS (scripts/check-ios.sh compiles it on Linux)" >&2; exit 2; }

version=$(tr -d '[:space:]' < VERSION)
project=src/ASync.App.iOS/ASync.App.iOS.csproj
out=artifacts/packages/ios
mkdir -p "$out"
# C# warnings are errors in scripts/check-ios.sh (and ios.yml's check job); here the iOS SDK's own build and link warnings
# (Xcode versions, native libraries) only show.
build=(dotnet build "$project" -c Release -f net10.0-ios -nologo)

# The extension in the built app, and its size (an extension has a memory limit; its binary is most of what it maps).
appex_size() {
  local appex="$app/PlugIns/ASyncFileProvider.appex"
  [[ -d "$appex" ]] || { echo "error: the File Provider extension is not in $app/PlugIns" >&2; exit 1; }
  echo "File Provider extension: $(du -sh "$appex" | cut -f1) ($(du -sh "$appex/ASyncFileProvider" 2>/dev/null | cut -f1) executable)"
}

case $mode in
  simulator)
    rid=iossimulator-$([[ "$(uname -m)" == arm64 ]] && echo arm64 || echo x64)
    # Ad hoc ("-"): the simulator needs a signature (for the entitlements: the Keychain group) but no identity.
    "${build[@]}" -r "$rid" -p:CodesignKey=-
    app=src/ASync.App.iOS/bin/Release/net10.0-ios/$rid/ASync.app
    rm -rf "$out/simulator" && mkdir -p "$out/simulator"
    cp -R "$app" "$out/simulator/ASync.app"
    appex_size
    (cd "$out/simulator" && ditto -c -k --keepParent ASync.app "../ASync-$version-simulator.app.zip")
    echo "$out/ASync-$version-simulator.app.zip ($rid)"
    ;;
  unsigned)
    # No signing identity on the runner and none wanted: SideStore/AltStore re-sign the app on the person's device.
    "${build[@]}" -r ios-arm64 -p:EnableCodeSigning=false
    app=src/ASync.App.iOS/bin/Release/net10.0-ios/ios-arm64/ASync.app
    staging=$(mktemp -d)
    mkdir -p "$staging/Payload"
    cp -R "$app" "$staging/Payload/ASync.app"
    rm -rf "$staging/Payload/ASync.app/_CodeSignature" "$staging/Payload/ASync.app/embedded.mobileprovision"
    rm -rf "$staging/Payload/ASync.app/PlugIns/"*.appex/_CodeSignature "$staging/Payload/ASync.app/PlugIns/"*.appex/embedded.mobileprovision
    app=$staging/Payload/ASync.app appex_size
    ipa="$PWD/$out/ASync-$version-unsigned.ipa"
    rm -f "$ipa"
    (cd "$staging" && zip -qry "$ipa" Payload)
    rm -rf "$staging"
    echo "$out/ASync-$version-unsigned.ipa ($(du -h "$ipa" | cut -f1))"
    ;;
  signed)
    : "${ASYNC_IOS_CODESIGN_KEY:?set ASYNC_IOS_CODESIGN_KEY to the signing identity}"
    : "${ASYNC_IOS_PROVISION:?set ASYNC_IOS_PROVISION to the provisioning profile}"
    : "${ASYNC_IOS_FP_PROVISION:?set ASYNC_IOS_FP_PROVISION to the File Provider extension profile}"
    dotnet publish "$project" -c Release -f net10.0-ios -r ios-arm64 -nologo \
      -p:ArchiveOnBuild=false -p:BuildIpa=true -p:IpaPackageDir="$PWD/$out/signed" \
      -p:CodesignKey="$ASYNC_IOS_CODESIGN_KEY" -p:CodesignProvision="$ASYNC_IOS_PROVISION" \
      -p:AsyncFileProviderProvision="$ASYNC_IOS_FP_PROVISION" \
      -p:AsyncPush="$([[ "${ASYNC_IOS_PUSH:-1}" == 0 ]] && echo false || echo true)"
    ipa=$(find "$out/signed" -name '*.ipa' | head -1)
    mv "$ipa" "$out/ASync-$version.ipa"
    rm -rf "$out/signed"
    echo "$out/ASync-$version.ipa"
    ;;
esac
