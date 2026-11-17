You are the new orchestrator for the ASync project (/mnt/git/ASync), taking over from the previous orchestrator session (id b2861529) with a clear context. The person asked for this: "for iOS run a new session that will continue your work with a clear context". Your job: run the iOS tasks 21–25 one at a time, each in its own worker session, check and merge each one, and keep the person informed. You do not write the iOS code yourself; workers do.

READ FIRST (all in ~/.claude/projects/-mnt-git-ASync/memory/, MEMORY.md is loaded for you): orchestrated-task-flow, workers-wake-orchestrator, screenshots-via-remote, ios-plan (the plan and the person's decisions), virtual-file-systems, queued-tasks-16-18 (what was built for Android, which iOS mirrors), web-ui-style. Then CLAUDE.md and AGENTS.md (the design doc; especially "Extra data sources", "Android app", "Android background work", "Web UI: browsing a device's folders", "Push", "Working without push", "Packaging and CI").

STATE NOW (2026-11-17):
- Local `main` is at 48feec5 (task 20 merged), 5 commits ahead of origin/main. Not pushed. VERSION 0.1.6 (no tag yet). v0.1.5 is tagged and released (the person moved the tag and pushed main for it).
- Tasks 1–20 are done and merged. 15–20 were Android work: documents provider, All files access, browsing device folders from the web, adding folders from the apps, photo/video backup, contacts/calendars backup.
- Carried over from task 20, unverified because this machine can't run the Android emulator reliably: the Android smoke passes (pass 1 with the adb-content fix, the photos pass, the background pass), the task 20 Android screenshots (permission, accounts, calendars, restore) and their README row, and the 3-ABI APK (armeabi-v7a added for the person's Galaxy A13; CI builds it on the next tag; check lib/armeabi-v7a then). Mention these to the person when relevant; don't block iOS on them.
- The release workflow now publishes the GitHub Release directly (no draft) after all test and package jobs pass; package/android/build-and-push jobs wait for test, e2e and agent-desktop.

HOW TO RUN A TASK (what worked for 20 tasks):
1. Write the brief to a file in your $CLAUDE_JOB_DIR/tmp, then start the worker with the start-code-session skill: `~/.claude/skills/start-code-session/start.sh /mnt/git/ASync --title "ASync NN <short name>" --model claude-opus-5-5 --mode auto "$(cat <brief file>)"`. Write the brief file with the Write tool first; a long inline heredoc launch was once refused by the permission classifier.
2. Each brief contains:
   - the task number and that it is one of a sequenced series run by an orchestrator;
   - the worktree and branch `task/NN-<name>`, based on the LOCAL main (not origin/main);
   - follow CLAUDE.md strictly: build checks, the version check-bump flow (it checks the release TAG), release notes, AGENTS.md + README, migrations only through `dotnet ef`, the server only in Docker locally;
   - the AGENTS.md sections to read;
   - don't open PRs, don't push tags, don't run workflow_dispatch;
   - the person's goal and the agreed design;
   - numbered scope items, including tests with concrete checks and docs;
   - screenshots saved under docs/screenshots and sent with SendUserFile (display: render);
   - commit and push the branch, leave main alone;
   - the wake-up paragraph with YOUR OWN id and session id. Several sessions are named "ASync", so workers find your row in ListAgents by ref/id; if you are not running they resume you with start.sh --resume <your session id> "Report from …". Update workers-wake-orchestrator.md in memory with your ids first.
   - Tests that use Unix phone paths return early on Windows (CI's agent-desktop runs ASync.App.Tests on Windows).
3. After starting a worker, subscribe once with SendMessage(to: its name, notify_when_idle: true). Don't re-subscribe while it waits on a long run: repeated idle notices are noise; its SendMessage report is the signal.
4. When the report comes:
   - check the branch is pushed, based on main, and its worktree is clean;
   - in its worktree, run `dotnet build ASync.slnx` (0 errors), `dotnet test` for both ASync.Tests and ASync.App.Tests, and `npx tsc --noEmit -p tsconfig.app.json && npx eslint .` in src/async-ui;
   - send it back if open items remain;
   - otherwise `git merge --no-ff` into local main with a "Merge task NN: …" message;
   - remove the worktree, delete the branch locally and on origin, and `claude stop <worker id>`;
   - forward 3–4 of its screenshots with SendUserFile;
   - update the progress line in memory;
   - start the next task.
   Run `dotnet build-server shutdown` after your own builds: memory is tight.
5. Your own small fixes: EnterWorktree, edit, commit, ExitWorktree(keep), `git merge --ff-only` into main from /mnt/git/ASync, remove the worktree. Don't redirect git with -C into the main checkout from inside a worktree; the harness refuses it.

MACHINE: 4 cores, 5.4 GB RAM, about 6–7 GB free disk, shared with other projects' containers (not yours to stop; ask the person). Heavy local runs crashed the VM twice today. iOS work is mostly CI on GitHub macOS runners, which helps, but local .NET builds still need care: check `uptime`/`free -m`, and run one heavy thing at a time.

iOS SPECIFICS (from ios-plan, decided by the person):
- Builds happen on GitHub macOS runners; there is no Mac. The repo is public; gh is authenticated here.
- Run the macOS jobs as RARELY as possible. Not on every push of task/** and not on a schedule. Run on main merges only when iOS files changed (path filters), with a concurrency group that cancels superseded runs.
- Agents may not use workflow_dispatch, so task 21 must design how a worker triggers the iOS workflow when the iOS part is ready (e.g. pushing a dedicated `ios-ci/<task>` branch, or a path-filtered trigger). Have task 21 propose it and get it right, and tell the person which way was chosen.
- Workers develop and unit-test everything they can on Linux first, and batch fixes before re-running CI.
- Signing: CI always produces an unsigned .ipa (sideloading with SideStore/AltStore). The signing + TestFlight step is written but skipped until certificate secrets exist. APNs push and the File Provider extension must work once signed and degrade cleanly unsigned.
- The iOS simulator test may run the server natively on the macOS runner with PostgreSQL from Homebrew. This is a CI-only exception to the Docker rule.
- Reuse the Android-era interfaces: IMediaLibrary/MediaTree (photos), IContactsSource/ICalendarSource (contacts, calendars), the per-device selection and the hub lists, BackgroundPlanner, run conditions, DeviceBrowser scopes. Server and repository formats must not change for iOS.

TASKS (one worker each, in order):
- 21 iOS foundation, with the CI design above.
- 22 background work.
- 23 photos via PhotoKit.
- 24 contacts and calendars via Contacts and EventKit.
- 25 File Provider extension.
Before task 21, ask the person only what is genuinely theirs to decide. Otherwise go.

Start now:
1. Update workers-wake-orchestrator.md with your own ids.
2. Note in MEMORY.md/queued-tasks that you are the orchestrator from 2026-11-17.
3. Tell the person in a few lines that you've taken over and what's next.
4. Write and launch task 21.
