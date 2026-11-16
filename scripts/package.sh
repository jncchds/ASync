#!/usr/bin/env bash
# Builds the installable desktop app and the async-cli archive for one target (CI's release job runs it on each OS).
#
#   scripts/package.sh <rid> [--version X.Y.Z] [--out DIR] [--only desktop|cli] [--skip-native-packages]
#
# <rid>: win-x64, win-arm64, osx-arm64, linux-x64, linux-arm64. The version defaults to the VERSION file.
# Output in DIR (default artifacts/packages/<rid>):
#   velopack/                      the desktop app's Velopack release for channel <rid>: the update feed
#                                  (releases.<rid>.json, assets.<rid>.json, ASync.Desktop-<version>-<rid>-full.nupkg, and
#                                  deltas when an older release is in that folder already) plus the installers:
#                                  Windows ASync.Desktop-<rid>-Setup.exe and -Portable.zip; macOS -Setup.pkg and
#                                  -Portable.zip; Linux ASync.Desktop.AppImage
#   ASync-<version>-<rid>.dmg      macOS: ASync.app (from the Velopack bundle, so it updates itself) and /Applications
#   async-desktop_<version>_<arch>.deb, async-desktop-<version>-1.<arch>.rpm   Linux (nfpm), no self-update
#   async-cli-<version>-<rid>.tar.gz (.zip on Windows)   one self-contained file
#   async-cli_<version>_<arch>.deb, async-cli-<version>-1.<arch>.rpm           Linux (nfpm)
#
# Needs the .NET SDK; vpk (dotnet tool restore; VPK=/path/to/vpk to use another), nfpm on Linux (NFPM=/path/to/nfpm),
# hdiutil on macOS. Runs on Linux, macOS and Windows (Git Bash). Velopack builds Windows packages only on Windows and
# macOS packages only on macOS.
#
# Publishing: self-contained; ReadyToRun (precompiled) for both programs: start-up of every async-cli command drops from
# ~110 to ~70 ms and the app's window shows sooner, for ~15-30% more size. Not trimmed: the client library's JSON
# (sync state, identity, the server API) still uses reflection, and trimming the CLI gave 34 trim warnings there.
#
# Signing (only when set; CI sets them from secrets):
#   WINDOWS_SIGN_PARAMS             signtool parameters for vpk --signParams (e.g. /f cert.pfx /p … /fd sha256 /tr … /td sha256)
#   MACOS_SIGN_APP_IDENTITY         "Developer ID Application: …" (vpk --signAppIdentity, hardened runtime + entitlements)
#   MACOS_SIGN_INSTALL_IDENTITY     "Developer ID Installer: …" (the .pkg)
#   MACOS_NOTARY_PROFILE            a notarytool keychain profile (xcrun notarytool store-credentials); notarizes the app,
#                                   the .pkg and the .dmg
#   MACOS_KEYCHAIN                  the keychain holding the identities
set -euo pipefail
cd "$(dirname "$0")/.."
umask 022 # the packages' files: 0644/0755 whatever the checkout's umask

rid="${1:?usage: scripts/package.sh <rid> [--version X.Y.Z] [--out DIR] [--only desktop|cli] [--skip-native-packages]}"
shift
version="$(tr -d '[:space:]' < VERSION)"
out=""
only=""
native=1
while [[ $# -gt 0 ]]; do
  case "$1" in
    --version) version="$2"; shift 2 ;;
    --out) out="$2"; shift 2 ;;
    --only) only="$2"; shift 2 ;;
    --skip-native-packages) native=0; shift ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done
out="${out:-artifacts/packages/$rid}"
mkdir -p "$out"
out="$(cd "$out" && pwd)"
work="$(pwd)/artifacts/package-work/$rid"
rm -rf "$work"
mkdir -p "$work"

os="${rid%-*}"
arch="${rid#*-}"
case "$arch" in
  x64) deb_arch=amd64 ;;
  arm64) deb_arch=arm64 ;;
  *) echo "unsupported architecture: $arch" >&2; exit 2 ;;
esac
pack_id=ASync.Desktop
exe=""
[[ "$os" == win ]] && exe=.exe

if [[ -n "${VPK:-}" ]]; then vpk=("$VPK"); else dotnet tool restore >/dev/null; vpk=(dotnet vpk); fi
nfpm="${NFPM:-nfpm}"

step() { printf '\n\033[1m── %s\033[0m\n' "$1"; }

publish() { # project output-dir extra-args...
  local project="$1" dir="$2"
  shift 2
  dotnet publish "$project" -c Release -r "$rid" --self-contained -p:Version="$version" -p:PublishReadyToRun=true \
    -p:DebugType=none -p:DebugSymbols=false -o "$dir" -nologo -v quiet "$@"
  chmod -R u+rwX,go+rX,go-w "$dir" # files copied from the NuGet cache keep its modes
}

# The release notes of this version (the section under "## v<version>"), for Velopack's feed.
notes="$work/notes.md"
awk -v v="## v$version" 'index($0, v) == 1 { on = 1; next } on && /^## v/ { exit } on { print }' RELEASE_NOTES.md > "$notes"
[[ -s "$notes" ]] || echo "ASync $version" > "$notes"

nfpm_package() { # config publish-dir program
  local config="$1" dir="$2" program="$3"
  local scripts
  scripts="$work/scripts-$(basename "$config" .yaml)"
  mkdir -p "$scripts"
  sed "s|@PROGRAM@|$program|" packaging/linux/postinstall.sh > "$scripts/postinstall.sh"
  sed "s|@PROGRAM@|$program|" packaging/linux/preremove.sh > "$scripts/preremove.sh"
  # nfpm expands variables only in some fields: fill them all in here.
  sed -e "s|\${VERSION}|$version|g" -e "s|\${ARCH}|$deb_arch|g" -e "s|\${PUBLISH_DIR}|$dir|g" -e "s|\${SCRIPTS_DIR}|$scripts|g" \
    "$config" > "$scripts/nfpm.yaml"
  for packager in deb rpm; do
    "$nfpm" package --config "$scripts/nfpm.yaml" --packager "$packager" --target "$out/" >/dev/null
  done
}

# ── The desktop app ─────────────────────────────────────────────────────────
if [[ "$only" != cli ]]; then
  step "Publishing async-desktop $version for $rid"
  desktop="$work/desktop"
  publish src/ASync.App.Desktop/ASync.App.Desktop.csproj "$desktop"

  common=(-u "$pack_id" -v "$version" -c "$rid" -r "$rid" --packTitle ASync --packAuthors "Kirill Chekanov"
    -e "async-desktop$exe" --releaseNotes "$notes" -o "$out/velopack")
  case "$os" in
    linux)
      step "Velopack: AppImage"
      "${vpk[@]}" pack "${common[@]}" -p "$desktop" -i packaging/icons/async-512.png --categories "Network;FileTransfer;Utility"
      if [[ $native == 1 ]]; then
        step "nfpm: async-desktop .deb and .rpm"
        nfpm_package packaging/linux/nfpm-desktop.yaml "$desktop" /opt/async-desktop/async-desktop
      fi
      ;;
    win)
      step "Velopack: Setup.exe"
      sign=()
      [[ -n "${WINDOWS_SIGN_PARAMS:-}" ]] && sign=(--signParams "$WINDOWS_SIGN_PARAMS")
      # The Start-menu shortcut carries the app id, so toasts show under "ASync" (DesktopPlatform.Notify).
      "${vpk[@]}" pack "${common[@]}" -p "$desktop" -i packaging/icons/async.ico --aumid ua.pp.chds.async \
        --shortcuts StartMenuRoot,Desktop ${sign[@]+"${sign[@]}"}
      ;;
    osx)
      step "Velopack: ASync.app, .pkg"
      # Our own bundle (Info.plist with the bundle id and LSUIElement, the icon); Velopack adds its updater to it.
      app="$work/ASync.app"
      mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
      cp -R "$desktop/." "$app/Contents/MacOS/"
      sed "s|@VERSION@|$version|g" packaging/macos/Info.plist > "$app/Contents/Info.plist"
      cp packaging/icons/async.icns "$app/Contents/Resources/ASync.icns"
      sign=()
      if [[ -n "${MACOS_SIGN_APP_IDENTITY:-}" ]]; then
        sign+=(--signAppIdentity "$MACOS_SIGN_APP_IDENTITY" --signEntitlements packaging/macos/entitlements.plist)
        [[ -n "${MACOS_SIGN_INSTALL_IDENTITY:-}" ]] && sign+=(--signInstallIdentity "$MACOS_SIGN_INSTALL_IDENTITY")
        [[ -n "${MACOS_NOTARY_PROFILE:-}" ]] && sign+=(--notaryProfile "$MACOS_NOTARY_PROFILE")
        [[ -n "${MACOS_KEYCHAIN:-}" ]] && sign+=(--keychain "$MACOS_KEYCHAIN")
      fi
      "${vpk[@]}" pack "${common[@]}" -p "$app" -i packaging/icons/async.icns ${sign[@]+"${sign[@]}"}

      step "DMG"
      portable="$(ls "$out"/velopack/*-Portable.zip | head -1)"
      stage="$work/dmg"
      mkdir -p "$stage"
      ditto -x -k "$portable" "$stage"
      ln -s /Applications "$stage/Applications"
      dmg="$out/ASync-$version-$rid.dmg"
      rm -f "$dmg"
      hdiutil create -volname ASync -srcfolder "$stage" -ov -format UDZO "$dmg" >/dev/null
      if [[ -n "${MACOS_SIGN_APP_IDENTITY:-}" ]]; then
        codesign --sign "$MACOS_SIGN_APP_IDENTITY" ${MACOS_KEYCHAIN:+--keychain "$MACOS_KEYCHAIN"} --timestamp "$dmg"
        if [[ -n "${MACOS_NOTARY_PROFILE:-}" ]]; then
          xcrun notarytool submit "$dmg" --keychain-profile "$MACOS_NOTARY_PROFILE" ${MACOS_KEYCHAIN:+--keychain "$MACOS_KEYCHAIN"} --wait
          xcrun stapler staple "$dmg"
        fi
      fi
      ;;
  esac
fi

# ── async-cli ───────────────────────────────────────────────────────────────
if [[ "$only" != desktop ]]; then
  step "Publishing async-cli $version for $rid"
  cli="$work/cli"
  publish src/ASync.Cli/ASync.Cli.csproj "$cli" -p:PublishSingleFile=true
  cp LICENSE "$cli/LICENSE"
  if [[ "$os" == win ]]; then
    archive="$out/async-cli-$version-$rid.zip"
    rm -f "$archive"
    (cd "$cli" && powershell.exe -NoProfile -Command "Compress-Archive -Path async-cli.exe,LICENSE -DestinationPath '$(cygpath -w "$archive" 2>/dev/null || echo "$archive")'")
  else
    tar -czf "$out/async-cli-$version-$rid.tar.gz" -C "$cli" async-cli LICENSE
  fi
  if [[ "$os" == linux && $native == 1 ]]; then
    step "nfpm: async-cli .deb and .rpm"
    nfpm_package packaging/linux/nfpm-cli.yaml "$cli" /usr/bin/async-cli
  fi
fi

step "Done: $out"
(cd "$out" && find . -maxdepth 2 -type f -newer "$work/notes.md" -exec ls -l {} + | awk '{printf "  %8.1f MB  %s\n", $5 / 1048576, $9}')
