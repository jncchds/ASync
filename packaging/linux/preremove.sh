#!/bin/sh
# Before the package is removed (deb: prerm remove; rpm: %preun 0). Upgrades are left alone (deb: prerm upgrade; rpm:
# %preun 1). scripts/package.sh replaces the placeholder below with the packaged program.
#
# Uninstalls the agent service of every user whose service runs this program: stops and disables it, removes the
# user unit the app or `async-cli service install` wrote and the app's start-at-login entry. Each user's config
# directory (~/.config/async: the device's identity and keys) is kept; delete it to remove those too.
PROGRAM="@PROGRAM@"
PACKAGED_UNIT=/usr/lib/systemd/user/async-agent.service

case "$1" in
  remove|0) ;;
  *) exit 0 ;;
esac

# Users logged in now: stop their agent through their own service manager.
for user in $(loginctl list-users --no-legend 2>/dev/null | awk '{print $2}'); do
  if systemctl --user -M "$user@" show -p ExecStart async-agent.service 2>/dev/null | grep -qF "$PROGRAM"; then
    systemctl --user -M "$user@" disable --now async-agent.service >/dev/null 2>&1 || true
    echo "Stopped and disabled the ASync agent of $user."
  fi
done

# Every home: the files that point at this program (also for users who are not logged in).
getent passwd | while IFS=: read -r name _ uid _ _ home _; do
  [ "$uid" -ge 1000 ] 2>/dev/null || [ "$uid" = 0 ] || continue
  [ -d "$home/.config" ] || continue
  units="$home/.config/systemd/user"
  if [ -f "$units/async-agent.service" ] && grep -qF "$PROGRAM" "$units/async-agent.service"; then
    rm -f "$units/async-agent.service"
    echo "Removed $units/async-agent.service ($name)."
  fi
  wants="$units/default.target.wants/async-agent.service"
  if [ -L "$wants" ]; then
    target=$(readlink "$wants")
    if [ ! -e "$wants" ] || { [ "$target" = "$PACKAGED_UNIT" ] && grep -qF "$PROGRAM" "$PACKAGED_UNIT" 2>/dev/null; }; then
      rm -f "$wants"
    fi
  fi
  autostart="$home/.config/autostart/ua.pp.chds.async.desktop"
  if [ -f "$autostart" ] && grep -qF "$PROGRAM" "$autostart"; then
    rm -f "$autostart"
    echo "Removed $autostart ($name)."
  fi
done

for user in $(loginctl list-users --no-legend 2>/dev/null | awk '{print $2}'); do
  systemctl --user -M "$user@" daemon-reload >/dev/null 2>&1 || true
  systemctl --user -M "$user@" reset-failed async-agent.service >/dev/null 2>&1 || true
done
echo "ASync: each user's ~/.config/async (device identity and keys) was kept; delete it to remove them."
exit 0
