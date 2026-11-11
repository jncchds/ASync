# Release notes

## v0.1.0 — 2026-11-11

- feat: **Project scaffold**: .NET 10 solution with central package versions (Contracts, Core, Infrastructure,
  Api, Tests), a React 19 + Vite UI served by the server, a Docker image and compose file, and a CI workflow
  that builds multi-arch images. Migration `InitialCreate`.
- feat(auth): **Accounts and sign-in**, as in abook: JWT access tokens with rotating refresh cookies, the
  first account becomes admin, and admins manage users. **Optional OIDC single sign-on**, with accounts
  created automatically on first sign-in.
- feat(devices): **Device provisioning**:
  - QR and enrollment codes: one-time, or reusable for scripted CLI installs.
  - A device-code flow with an approval page for headless devices.
  - Challenge–response sign-in with per-device ECDSA P-256 keys.
  - Revocation that takes effect immediately.
  - The device list shows name, platform, version and last seen.
- feat: `scripts/smoke-devices.cs` runs an end-to-end check of sign-in and every device flow against a
  running server.
