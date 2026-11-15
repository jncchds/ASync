#!/usr/bin/env bash
# Checks the Linux packages in throw-away containers: the .deb on Debian, the .rpm on Fedora. Installs async-desktop
# and async-cli with the package manager and checks the files, the /usr/bin links, the desktop entry
# (desktop-file-validate), the systemd user unit (systemd-analyze verify) and that async-cli runs. Then plays a user
# who set the agent up (a user unit written by `async-cli service install`, the packaged unit enabled for another user,
# the app's start-at-login entry, a config directory), removes both packages and checks that the pre-removal scripts
# removed what pointed at them and kept the config directories. Upgrade: installs the same package again over itself
# (prerm upgrade) and checks that nothing was removed.
#
#   scripts/smoke-packages.sh [packages-dir]     # default: artifacts/packages/linux-x64 (scripts/package.sh linux-x64)
#
# Needs docker. The containers are removed afterwards.
set -euo pipefail
cd "$(dirname "$0")/.."
dir="$(cd "${1:-artifacts/packages/linux-x64}" && pwd)"
ls "$dir"/async-desktop_*.deb "$dir"/async-cli_*.deb "$dir"/async-desktop-*.rpm "$dir"/async-cli-*.rpm >/dev/null

check='
set -u
failures=0
pass() { printf "  \033[32m✓\033[0m %s\n" "$1"; }
fail() { printf "  \033[31m✗\033[0m %s\n" "$1"; failures=$((failures + 1)); }
t() { local what="$1"; shift; if "$@" >/tmp/out 2>&1; then pass "$what"; else fail "$what"; sed "s/^/      /" /tmp/out | head -20; fi; }

install_pkgs() { if command -v apt-get >/dev/null; then apt-get install -y -qq /pkg/async-desktop_*.deb /pkg/async-cli_*.deb >/tmp/install.log 2>&1;
                 else dnf install -y -q /pkg/async-desktop-*.rpm /pkg/async-cli-*.rpm >/tmp/install.log 2>&1; fi; }
t "packages install with their dependencies" install_pkgs || cat /tmp/install.log
t "/opt/async-desktop/async-desktop is executable" test -x /opt/async-desktop/async-desktop
t "/usr/bin/async-desktop -> /opt/async-desktop/async-desktop" test "$(readlink /usr/bin/async-desktop)" = /opt/async-desktop/async-desktop
t "/usr/bin/async-cli runs (--version)" /usr/bin/async-cli --version
echo "      async-cli --version: $(/usr/bin/async-cli --version 2>&1 | head -1)"
t "desktop entry is valid (desktop-file-validate)" desktop-file-validate /usr/share/applications/ua.pp.chds.async.desktop
t "icons in hicolor (16 … 512)" test -f /usr/share/icons/hicolor/16x16/apps/ua.pp.chds.async.png -a -f /usr/share/icons/hicolor/512x512/apps/ua.pp.chds.async.png
t "user unit installed" test -f /usr/lib/systemd/user/async-agent.service
mkdir -p -m 0700 /tmp/xdg
t "user unit: systemd-analyze verify" env XDG_RUNTIME_DIR=/tmp/xdg systemd-analyze verify --user /usr/lib/systemd/user/async-agent.service
t "user unit runs the packaged app" grep -qx "ExecStart=/opt/async-desktop/async-desktop service run" /usr/lib/systemd/user/async-agent.service
t "no file is group- or world-writable" sh -c "! find /opt/async-desktop /usr/bin/async-cli /usr/lib/systemd/user/async-agent.service /usr/share/applications/ua.pp.chds.async.desktop -perm /022 -type f | grep ."
agent_runs() {
  ASYNC_CONTROL_SOCKET=/tmp/a.sock /opt/async-desktop/async-desktop --config /tmp/cfg service run >/tmp/agent.log 2>&1 &
  local pid=$! ok=1
  for _ in $(seq 1 40); do [ -S /tmp/a.sock ] && { ok=0; break; }; sleep 0.25; done
  kill -TERM $pid; wait $pid; local exit=$?
  [ $ok = 0 ] && [ $exit = 0 ] || { cat /tmp/agent.log; return 1; }
}
t "async-desktop service run opens its control socket and stops cleanly on SIGTERM" agent_runs

# Two users who set the agent up, as the app and the CLI do it.
useradd -m -u 1001 alice; useradd -m -u 1002 bob
a=/home/alice/.config; b=/home/bob/.config
mkdir -p $a/systemd/user/default.target.wants $a/autostart $a/async $b/systemd/user/default.target.wants $b/async
printf "[Service]\nExecStart=/usr/bin/async-cli --config /home/alice/.config/async service run\n" > $a/systemd/user/async-agent.service
ln -s $a/systemd/user/async-agent.service $a/systemd/user/default.target.wants/async-agent.service
printf "[Desktop Entry]\nExec=/opt/async-desktop/async-desktop --config /home/alice/.config/async --minimized\n" > $a/autostart/ua.pp.chds.async.desktop
ln -s /usr/lib/systemd/user/async-agent.service $b/systemd/user/default.target.wants/async-agent.service
echo "{}" > $a/async/identity.json; echo "{}" > $b/async/identity.json
printf "[Service]\nExecStart=/home/carol/bin/async-cli service run\n" > /tmp/foreign.service

reinstall() { if command -v apt-get >/dev/null; then apt-get install -y -qq --reinstall /pkg/async-desktop_*.deb /pkg/async-cli_*.deb;
              else dnf reinstall -y -q /pkg/async-desktop-*.rpm /pkg/async-cli-*.rpm; fi; }
t "reinstall (an upgrade to the same version)" reinstall
t "  … left alices unit, autostart entry and bobs enablement alone" test -f $a/systemd/user/async-agent.service -a -f $a/autostart/ua.pp.chds.async.desktop -a -L $b/systemd/user/default.target.wants/async-agent.service

remove_pkgs() { if command -v apt-get >/dev/null; then apt-get remove -y -qq async-desktop async-cli; else dnf remove -y -q async-desktop async-cli; fi; }
t "remove both packages" remove_pkgs
sed "s/^/      /" /tmp/out | grep -i -E "removed|kept|stopped" || true
t "alices user unit (async-cli) removed" test ! -e $a/systemd/user/async-agent.service
t "alices enablement link removed" test ! -L $a/systemd/user/default.target.wants/async-agent.service
t "alices start-at-login entry removed" test ! -e $a/autostart/ua.pp.chds.async.desktop
t "bobs enablement of the packaged unit removed" test ! -L $b/systemd/user/default.target.wants/async-agent.service
t "config directories kept" test -f $a/async/identity.json -a -f $b/async/identity.json
t "/opt/async-desktop, /usr/bin/async-cli and the unit are gone" test ! -e /opt/async-desktop -a ! -e /usr/bin/async-cli -a ! -e /usr/lib/systemd/user/async-agent.service
exit $failures
'

status=0
run() { # image setup
  local image="$1" setup="$2"
  printf '\n\033[1m── %s\033[0m\n' "$image"
  docker run --rm -v "$dir:/pkg:ro" "$image" bash -c "$setup >/dev/null 2>&1; $check" || status=1
}
run debian:trixie "apt-get update -qq && apt-get install -y -qq desktop-file-utils systemd"
run fedora:43 "dnf install -y -q desktop-file-utils systemd shadow-utils"
[[ $status == 0 ]] && printf '\nAll package checks passed.\n' || printf '\nSome package checks failed.\n'
exit $status
