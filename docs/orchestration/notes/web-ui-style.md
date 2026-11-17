---
name: web-ui-style
description: "ASync's web UI must use the nopds/abook family look (sidebar shell, cards, pills, Inter, nopds token set) with ASync's own plum/fuchsia palette"
metadata:
  node_type: memory
  type: feedback
  originSessionId: 29680b06-953c-48b8-8fc2-d0c70b38aa72
  modified: 2026-11-12T02:58:58.527Z
---

The user's cross-project rule: every web project reuses the nopds/abook visual style, but with its own unique palette. The rule lives in `/home/chds/.claude/projects/-mnt-git-ANalyze/memory/web-visual-style.md`.

The style to copy:
- the collapsible sidebar app shell: toggle, app name, version pill, emoji nav, sign-out and theme toggle at the bottom (see `/mnt/git/abook/src/abook-ui/src/components/Sidebar.tsx`);
- flat cards and pills;
- the Inter font;
- the CSS-variable token structure from `/mnt/git/nopds/src/Nopds.Web/ClientApp/src/index.css`.

On 2026-11-12 the user pointed out that the first ASync UI (M0–M2) had missed this: it copied abook's indigo and used a top bar. ASync's palette is **plum/fuchsia**: dark theme deep plum surfaces with accent `#c026d3` (hover `#d946ef`); light theme lilac-white with accent `#a21caf`.

Palettes the sibling projects already use, so they must not be reused:
- abook indigo, abeat sky blue;
- achat, ANalyze and GenTrification teal;
- acopy orange, ashopper green, nopds walnut/brass, plexproxy gold.

**Why:** the user wants their apps to look like one family while each stays recognisable.
**How to apply:** any ASync UI work keeps the sidebar shell and the palette tokens. New pages use the shared tokens and patterns, never new colours or a new layout.
