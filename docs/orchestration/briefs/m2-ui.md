Task for this session: restyle the ASync web UI into the user's app family look, with ASync's own palette, and fix the layout bugs. A coordinator session merges the result.

First read CLAUDE.md (mandatory rules), AGENTS.md, and the memo ~/.claude/projects/-mnt-git-ASync/memory/web-ui-style.md (the user's cross-project UI rule). Work in a git worktree branched from origin/main, on branch `ui-family-style`.

Reference implementations (read, don't modify):
- /mnt/git/abook/src/abook-ui: Sidebar.tsx (collapsible sidebar: toggle + app name + version pill at the top, emoji nav in the middle, sign-out + light/dark/system theme toggle at the bottom), layouts, index.css, hooks/useTheme.ts; cards, pills, and how it behaves on phones.
- /mnt/git/nopds/src/Nopds.Web/ClientApp/src/index.css: the CSS-variable token structure to copy (semantic tokens for light/dark). ASync stays plain CSS (no Tailwind/MUI); map the same token names onto plain CSS.

Do:
1. Replace ASync's top bar with the sidebar shell: Folders, Devices, Storage, Users (admin) with emoji icons, the version pill (__APP_VERSION__), the user's name, sign-out, and a light/dark/system theme toggle (remembered in localStorage, wrapped in try/catch). On phones the sidebar collapses to a drawer/top toggle like abook.
2. Use ASync's palette, which must not repeat a sibling's:
   - dark theme: deep plum surfaces, accent fuchsia #c026d3, hover #d946ef;
   - light theme: lilac-white surfaces, accent #a21caf;
   - success, warning and danger tuned to fit;
   - Inter font;
   - every colour through tokens, with no hard-coded colours left in components.
3. Restyle all pages to the family patterns (flat cards, pills for mode/encryption/shared/status badges, consistent buttons/forms): Login, Folders (+ FolderForm, FolderDeviceForm, SnapshotBrowser), Devices (+ AddDevicePanel, approve page), Storage (+ form), Users.
4. Fix the layout bugs seen in screenshots:
   (a) on the Folders page long local paths / long error messages push the device table beyond its card and the page wider than the viewport (the header/banner then stop short of the page width) — tables must stay inside their cards (wrap/ellipsis with title, or scroll inside the card);
   (b) at phone width (390px) the page is ~3× wider than the screen — make every page fit 390px with no horizontal page scroll (stack table rows as cards on narrow screens or scroll inside the card);
   (c) snapshot ids shown as `Id[..8]` are only the time prefix of a Guid v7 (identical for snapshots made within ~a minute): in src/ASync.Client.Core/SyncAgent.cs messages and anywhere in the UI, show a distinguishing part (e.g. the last 8 hex chars), and make `async-cli restore --snapshot` accept a prefix OR suffix.
5. Verify with screenshots: run the stack (`ASYNC_PORT=5196 docker compose -p async-ui up -d --build`), seed demo data (you may adapt ~/.claude/jobs/29680b06/tmp/shots/seed.py and the Playwright script ~/.claude/jobs/29680b06/tmp/shots/pw/shots.mjs — playwright-core is installed there and drives /usr/bin/google-chrome), capture desktop dark+light and 390px phone screenshots of every page, look at them, and iterate until they look right. Save the final screenshots under docs/screenshots/ and reference a few from README.md.
6. tsc + eslint clean, dotnet build without warnings, tests pass. Update AGENTS.md (a short "Web UI style" section: shell, tokens, palette) and RELEASE_NOTES.md (follow the version check-bump rule in CLAUDE.md).

Finish: commit, push the branch, open a pull request to main with before/after notes. Do NOT merge it. End with a short report.
