#!/bin/sh
# After installing or upgrading the package (deb: postinst configure [old-version]; rpm: %post 1|2+).
# scripts/package.sh replaces the placeholder below with the packaged program (/opt/async-desktop/async-desktop or
# /usr/bin/async-cli).
PROGRAM="@PROGRAM@"

# Menus and icons (the desktop package).
command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database -q /usr/share/applications 2>/dev/null
command -v gtk-update-icon-cache >/dev/null 2>&1 && gtk-update-icon-cache -q -t -f /usr/share/icons/hicolor 2>/dev/null

# Every running agent that runs this program restarts on the new version; the user managers re-read the unit files.
# Nothing is enabled or started for anyone who did not set the agent up.
for user in $(loginctl list-users --no-legend 2>/dev/null | awk '{print $2}'); do
  systemctl --user -M "$user@" daemon-reload >/dev/null 2>&1 || continue
  if systemctl --user -M "$user@" show -p ExecStart async-agent.service 2>/dev/null | grep -qF "$PROGRAM"; then
    systemctl --user -M "$user@" try-restart async-agent.service >/dev/null 2>&1 || true
  fi
done
exit 0
