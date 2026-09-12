# CLAUDE.md

Guidance for Claude Code sessions working in this repository.

## Documentation language

As of 2026-09-12, all documentation and code comments in this repository are written in
**English** — this includes `ARCHITECTURE.md`, `MIGRATION.md`, this file, and every `//`/`/* */`/
`///`/`<!-- -->` comment in the source code. This is a deliberate, permanent convention going
forward, not a one-off cleanup: write new comments in English, and if you touch an old comment
that's somehow still in French, translate it while you're there.

**The application's own user interface stays in French.** This is a single-user app for a
French-speaking household — all UI labels, button text, `aria-label`/`title` attributes, and
user-facing error/status messages (frontend and backend alike) are intentionally in French and
must stay that way. Never translate UI-facing strings to English; only comments and standalone
documentation files are affected by the English convention above. See `git log` around
2026-09-12 (commit translating `ARCHITECTURE.md` and ~85 source files) if you need to see exactly
where this line was drawn in practice.

## Key documents

- `ARCHITECTURE.md` — the functional/technical reference: data model, pCloud integration,
  security requirements, API surface, functional flows. Kept up to date across versions.
- `MIGRATION.md` — a handoff snapshot written during the Windows → Raspberry Pi 4 migration
  (2026-09-12): actual project state, accumulated pitfalls, environment/secrets, and Windows→Linux
  migration specifics. Treat it as a point-in-time snapshot, not a living document — re-verify
  anything time-sensitive against the current code/GitHub issues rather than assuming it's still
  accurate.

## Working conventions

- Backend: .NET 10 / ASP.NET Core, xUnit tests under `backend/tests/`. Build with
  `dotnet build`/`dotnet test` from `backend/`.
- Frontend: Angular 22 (zoneless — no zone.js, see `MIGRATION.md` §3 for testing implications),
  vitest tests colocated as `*.spec.ts`. Build/test with `npm run build`/`npm test` from
  `frontend/`.
- Full stack runs via Docker Compose (`docker-compose.yml`) behind an nginx reverse proxy;
  `deploy/deploy.sh` is the deployment script for a remote server.
- GitHub issues (`Rothen68/RPhotoAlbum`) track features/bugs by milestone (V1/V2/V3). Check
  `MIGRATION.md` §2 and §8 for the state of open issues as of the last handoff.
