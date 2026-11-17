## Agent service and control channel (M3)

The sync agent runs in the background as a service of the OS, and a local control channel lets the CLI and the
desktop app (M3-2) see and steer it. No GUI yet.

**Where it lives.** The host is a mode of `async-cli` (`async-cli service run`), with the logic in the library
`ASync.Agent`, not a separate executable:
- one program to install and update: the service runs exactly the CLI that installed it
  (`ServiceInstaller.CurrentProgram`: the apphost, or `dotnet async-cli.dll`), so the CLI and the agent are never
  different versions;
- the desktop app (M3-2) can reference `ASync.Agent` too, to run the same `AgentRuntime` or install the service;
  `ASync.Agent.Control` is the only part it needs to talk to one.

**Processes.** `AgentRuntime` is the agent process around `SyncAgent`, used by `async-cli run` (foreground, console
logs) and `async-cli service run`:
- `Start()` takes the config directory's `AgentLock` and opens the control channel. A channel that cannot be opened is
  logged and the agent runs without it; the lock still keeps the CLI from syncing next to it.
- `RunAsync()` signs the device in and runs `SyncAgent`. As a service (`WaitForIdentity`) it does not exit without an
  identity or after a revocation: it reports `NotEnrolled`/`Revoked`, polls `identity.json` every 5 seconds and starts
  by itself after `async-cli enroll` (or `forget` + `enroll`). A supervisor would only restart it into the same state.
  The foreground `run` keeps its old behaviour (enroll from the environment, exit 1 on revocation).

**Host** (`AgentHost.RunServiceAsync`, Generic Host with `DisableDefaults`):
- **systemd:** `AddSystemd()`, `Type=notify`. The runtime starts (lock, socket) in the hosted service's `StartAsync`,
  so READY=1 comes only after that, and a second agent fails before READY. Logs via `AddSystemdConsole` (journald
  priorities, no timestamps of its own).
- **Windows Service:** `AddWindowsService()` (`ASyncAgent`), logs to the Event Log (source "ASync Agent").
- **Everything else** (launchd, the Windows logon task, a terminal): a rolling file (`--log-file`, 10 MB × 5, written
  by a background thread; a full queue drops lines rather than block sync) or the console.
- Level Information (Microsoft.* and System.Net.Http at Warning); `--verbose` or `ASYNC_LOG_LEVEL` changes it.
  Shutdown timeout 45 s (systemd `TimeoutStopSec=60`). `AgentRuntime.DisposeAsync` and `ControlServer.DisposeAsync` are
  idempotent: the host disposes the singleton runtime, and `RunServiceAsync` disposes it again for the paths where the
  host never started (before this, every stop ended in an `ObjectDisposedException` and exit 1).

**Install** (`async-cli service install|uninstall|status [--system] [--run-as user] [--no-start] [--log-file]`,
`ServiceInstaller`; the unit, plist and task XML are pure functions with tests):

| | per user (default) | `--system` |
|---|---|---|
| Linux | `~/.config/systemd/user/async-agent.service`, `WantedBy=default.target`, `systemctl --user enable` + `restart`; hints at `loginctl enable-linger` | `/etc/systemd/system/async-agent.service`, `User=`, `RuntimeDirectory=async-agent` (0700) with `ASYNC_CONTROL_SOCKET=/run/async-agent/agent.sock`, after `network-online.target` |
| macOS | LaunchAgent `~/Library/LaunchAgents/ua.pp.chds.async.agent.plist`, `launchctl bootstrap gui/<uid>`, `KeepAlive` on failure, log in `~/Library/Logs/ASync` | LaunchDaemon with `UserName` |
| Windows | Task Scheduler task "ASync Agent" at logon, as the user (`InteractiveToken`, least privilege), through `conhost --headless` (no window), restarted on failure | Windows Service `ASyncAgent` (`sc.exe create`, LocalSystem, delayed auto-start, restart actions) |

Per user on Windows is a logon task, not a service: a service running as a user needs that user's password stored
with it, and the agent needs the user (DPAPI's CurrentUser keys, the user's own paths and mapped drives). `install`
restarts an agent that already runs, so re-installing after an update picks up the new binary.

**Single instance** (`AgentLock`): `agent.lock` in the config directory, held open with `FileShare.None` while the
agent runs. .NET turns that into `flock(LOCK_EX)` on Linux/macOS and a sharing lock on Windows; the OS drops it with
the process, so a crash leaves nothing stale. A second `run` or service fails with `AgentAlreadyRunningException`
(`service run` exits 2), `forget` refuses while it is held, and the routed commands below refuse to work in-process
next to an agent whose channel does not answer. One agent per config directory, not per user: the end-to-end test
runs two agents of one user.

**Control channel** (`ASync.Agent.Control`):
- **Transport:** a Unix domain socket on Linux and macOS, `$XDG_RUNTIME_DIR/async/agent-<hash>.sock` (macOS: `$TMPDIR`;
  without either, e.g. in a container: `/tmp/async-<uid>/`, created 0700 and refused when it cannot be tightened,
  i.e. someone else owns it); the socket is 0600 in a 0700 directory. On Windows the named pipe `ASync.Agent.<hash>`,
  created with `PipeOptions.CurrentUserOnly` (the pipe's DACL allows only the current user) and `FirstPipeInstance`;
  the client uses `CurrentUserOnly` too, which checks that the server runs as the same user, so no other account can
  plant an impostor pipe. The system service's pipe allows SYSTEM and Administrators. `<hash>` is SHA-256 of the user
  name and the config directory's full path (trailing separators trimmed). `ASYNC_CONTROL_SOCKET` overrides the
  address; the agent writes the address it uses to `agent.endpoint` in the config directory and clients read that
  first, so they find an agent started with another environment (the system unit).
- **Protocol** (version 1): one JSON object per line each way, camelCase, string enums, nulls left out, at most 1 MiB
  a line, `System.Text.Json` source generation (`ControlJsonContext`; trimming- and AOT-safe for the app). Requests
  `{v, id, op, folderId?, path?, secret?, allowMassDelete?}` get one response `{v, id, ok, error?, message?, hello?,
  status?, results?}` each, in order. Operations:
  - `hello`: protocol and agent version, pid, mode, config directory (sent by `ControlClient` on connect);
  - `status`: state (NotEnrolled, Connecting, Connected, Reconnecting, Offline, Revoked, Stopping), device, problem, and
    per folder its settings, activity (Idle, Syncing, BackingUp, Maintenance, Paused), whether an E2E folder is
    locked here, progress `{files, totalFiles, bytes, totalBytes, currentFile}`, last result and next run;
  - `sync` (a folder or all): waits for runs that start after the request and returns their results;
  - `pause`, `resume`, `set-path`: through the server's device settings, then a reload;
  - `unlock`: the secret is never logged; starts the backup that waited for it;
  - `subscribe`: the connection then carries only events: a full `status` first, then `folder` changes, `run-finished`
    with the result, progress twice a second while it moves, `status` on connection or folder-list changes. A
    subscriber that does not keep up loses its oldest events (bounded channel of 256) instead of slowing the agent.
- **Versioning:** within version 1 only optional fields and operations are added; clients ignore unknown fields and
  check `hello.protocol` before using newer operations. A request with a higher `v` is refused naming the agent's
  version, so an old agent tells a new app to update it. Errors come back as `{ok: false, error}`; the connection
  stays usable.
- **Authentication is the OS's access control, nothing more.** Only the user's own processes (and root or
  administrators, who can read the identity file anyway) can open the socket or pipe. Everything the channel can do,
  such a process can already do directly: read `identity.json` and the folder keys, sign in as the device, change the
  files being synced. A token or password on top would sit in a file readable by the same processes and protect
  nothing more. The channel never returns secrets (device key, folder keys, tokens), listens on no network address,
  and `unlock` only takes a secret in.

**CLI routing** (`ASync.Cli/Routed.cs`): `status`, `sync`, `pause`, `resume`, `set-path` and `unlock` first try the
agent of the config directory. If one answers, the command goes through it (same output and exit codes; `status` adds
each folder's activity, progress, last result and next run; `sync --progress` follows the event stream). If none runs,
the command works in-process as before. If the lock is held but the channel does not answer (another user, an agent
without a channel), it fails rather than sync next to the agent. `ASYNC_IN_PROCESS=1` skips the routing and that check
(for debugging). The other commands (`enroll`, `folders`, `snapshots`, `restore`, `prune`, `check`, `init-encryption`)
stay in-process: they don't sync plain folders, and the repository lock protects Backup folders.

**Tests:** `src/ASync.Tests/Agent`: the protocol over a real socket (or pipe) with a fake handler, endpoints and their
modes, a real `AgentRuntime` on its channel, the lock and a second agent, CLI routing and output, the unit, plist and
task rendering and quoting, the rolling log, secret formats and migration, progress. `scripts/smoke-agent-service.sh`
installs the real user unit on Linux against a throw-away stack (project `async-agent-smoke`, port 5191) and checks:
active after READY, status through the service, the first sync on start, a routed `sync`, a refused second `run`,
pause and resume, a watcher sync, journald lines, `systemctl --user restart` with a clean stop and a new pid, then
`service uninstall` leaving nothing behind (unit, process, socket, endpoint file) and the CLI in-process again. It
refuses to run when the user already has an `async-agent.service`, and removes its unit even when it fails half-way.

