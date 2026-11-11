# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.
`AGENTS.md` is the authoritative design document — read it before changing architecture.

## Commands

### Backend (.NET 10 — run from repo root)

```bash
dotnet build ASync.slnx
dotnet test src/ASync.Tests/ASync.Tests.csproj
```

### Frontend (run from `src/async-ui/`)

```bash
npm install
npx tsc --noEmit -p tsconfig.app.json  # type-check (plain `tsc --noEmit` checks nothing: tsconfig.json only holds references)
npx eslint .
npm run build    # TypeScript check + Vite build → src/ASync.Api/wwwroot/
```

### Database migrations (from repo root)

```bash
dotnet ef migrations add <MigrationName> --project src/ASync.Infrastructure --startup-project src/ASync.Api
```

### Running

```bash
docker compose up -d --build                  # full stack: server on http://localhost:5100 + PostgreSQL
docker compose up postgres -d                 # database only
dotnet run scripts/smoke-devices.cs -- http://localhost:5100 admin admin-password   # end-to-end device checks
```

## Architecture (short)

| Project | Purpose |
|---|---|
| `src/ASync.Contracts` | DTOs + enums shared by server and all clients. No dependencies. |
| `src/ASync.Core` | Domain models, repository interfaces, pure logic (`Security/Codes`, `Security/DeviceKeys`) |
| `src/ASync.Infrastructure` | EF Core `AppDbContext` (PostgreSQL), repositories, migrations, Data Protection key store |
| `src/ASync.Api` | Controllers, `Auth/` (JWT, OIDC, secrets command), `Services/DeviceProvisioningService`, hosted services |
| `src/async-ui` | React 19 + TypeScript + Vite SPA, served from the API's `wwwroot` |
| `src/ASync.Tests` | xUnit with hand-written fakes (`Fakes.cs`) |

Key patterns:
- **People vs devices:** people's endpoints use `[Authorize(Roles = Roles.Users)]`; device endpoints use
  `Roles.Device`. A device token must never reach account management.
- **Devices authenticate by signing challenges** with an ECDSA P-256 key. There is no device password or
  refresh token. See AGENTS.md "Device provisioning".
- **Codes and secrets are stored hashed.** Use `Codes.HashCode` for codes people type and
  `Codes.HashSecret` for machine secrets.
- **Controllers** follow abook's style: constructor-injected `_fields`, errors returned as
  `{ message }`, DTOs from `ASync.Contracts`.
- **Program.cs** registers everything by hand, grouped under `// ── Section ──` comments.

## Mandatory workflow rules

1. **After every code change**, check for compilation errors and warnings:
   - Backend: `dotnet build ASync.slnx`.
   - Frontend: `npx tsc --noEmit -p tsconfig.app.json` and `npx eslint .` in `src/async-ui`.
   - Fix all errors. Fix warnings whenever that is safe without changing behaviour.
2. **After every committed change**, run the version check-bump flow:
   1. Check whether the current `VERSION` has been pushed to origin
      (`git log origin/main..HEAD -- VERSION`). If it has, bump the patch number and add a new
      `## v{version} — {date}` heading to `RELEASE_NOTES.md`. Otherwise reuse the current version.
   2. Make sure the heading for the current version carries today's date.
   3. Append a bullet for the change under that heading.
3. **After every architectural or schema change**, update `AGENTS.md` (the design doc) and `README.md`
   (the user docs).
4. **Never create migration files by hand.** Always use `dotnet ef migrations add`.
5. **Never run the server outside Docker.** Don't use `dotnet run --project src/ASync.Api` or
   `npm run dev` to start it. Use `docker compose up -d`. The smoke script is a client, so it is fine to run.
