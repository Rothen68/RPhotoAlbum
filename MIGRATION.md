# Handoff Document — Windows → Raspberry Pi 4 (Debian) Migration

> Written on 2026-09-12, at the end of the previous development session (Windows machine), for a
> Claude Code session that will pick up this project on the new environment (Raspberry Pi 4,
> Linux/Debian), with no access to the conversation history that produced this document. Last
> commit at the time of writing: `d5c511e` on the `V3` branch.
>
> This document does not replace `ARCHITECTURE.md` (which remains the functional/technical
> reference for the project) — it complements it with the actual state of progress, pitfalls
> already encountered, and anything specific to the Windows machine the project was developed on
> until now.

## 1. Purpose and project context

RPhotoAlbum is a personal photo/video album management web application, built for **single-user**
use (the project's author and their family):

- All media (photos/videos) and album metadata are stored on **pCloud** (the user's personal
  pCloud account) — the application stores no business data that doesn't already exist on pCloud.
- The application itself (frontend + backend) runs on a private server, on the user's home
  network, reachable remotely only through an existing VPN — **never publicly exposed on the
  Internet**. The Raspberry Pi 4 targeted by this migration is that private server.
- Main use case: browse the Gallery (all media indexed from pCloud source folders), create
  themed albums (wedding, vacations...) by adding media and Markdown text blocks to them, in
  chronological order.
- Strong UX constraint: **mobile-first** — actual usage happens overwhelmingly from a phone (the
  application is installed as a PWA on the home screen).
- Strong architectural constraint: the local SQLite cache (metadata, thumbnails) must remain
  **fully reconstructible from pCloud** — it is only a performance index, never a source of truth.

See `ARCHITECTURE.md` at the repo root for the full detail (data model, functional flows, security
choices) — it has been kept up to date across versions and remains broadly reliable, with a few
nuances given in section 2 below.

## 2. Current state (actual progress, not an optimistic status)

The project is on its **3rd major iteration** (V1 → V2 → V3, one git branch per iteration, `V3`
being the current working branch and the one used for deployment — see section 4 for the detailed
history). Overall, the application is **functional and used daily** by the user on their current
server (accessed via `http://<LAN-IP>:3140` and `https://<LAN-IP>:3143`).

### What works (verified in real usage, not just tested)

- Single-user application authentication (login/password) + pCloud OAuth.
- Periodic indexing of pCloud source folders into the local SQLite cache, with EXIF extraction
  (date taken, GPS) and reverse geolocation (Nominatim) running in the background.
- Gallery: virtualized grid, date grouping, filters (type, size, location, name search), multi-
  select, global rejection, adding to one or more albums.
- Albums: creation, reorderable list via drag-and-drop, deletion (with confirmation).
- Album Detail: chronological flow of media/text blocks, Markdown editing, drag-and-drop
  reordering, merging/splitting rows, inline text insertion.
- Server-side thumbnail disk cache (avoids going back to pCloud on every display).
- Installable PWA on mobile (confirmed on Android by the user; iOS not explicitly tested).
- Offline album viewing (thumbnails only) — manual "offline mode" toggle in the Configuration
  menu, with automatic fallback on network failure. A substantial feature, **validated by the
  user under real conditions** ("Perfect it's working!"), but see the important caveat below.
- Self-signed HTTPS on the reverse proxy (required for offline viewing, which needs a secure
  browser context) — running alongside the existing HTTP port, not redirected.
- Login attempt rate limiting and basic HTTP security headers on the reverse proxy — added right
  at the end of the previous session, not yet proven over time.
- pCloud storage quota warning in Configuration — successfully tested against the real connected
  pCloud account just before the end of the previous session.

### Important caveat on issue #29 (offline)

GitHub issue **#29 "Offline album sync (PWA)" is still open on GitHub**, even though the feature
it describes was fully implemented and validated by the user during the previous session (many
`(#29)` commits on `V3`, see section 4). It was simply never closed on GitHub. Verify/re-close it
after migration if everything still works correctly on the new environment (the mechanism depends
on the browser's Cache Storage API, which is *per device*, not per server — a server change
shouldn't break anything client-side, but it deserves a real check).

### In progress / unfinished

- **Issue #7 (open, V3 milestone)**: search/filter by tags on photos (requires a local automatic
  tagging tool based on pCloud thumbnails, with a CPU/RAM frugality constraint — worth re-reading
  carefully now that the target is a Raspberry Pi, whose resources are more limited than a
  development workstation). **Not started at all.**
- A batch of 5 fixes (security/architecture/UX) was added right at the end of the previous
  session, in response to a deliberate review before closing the V3 milestone (see section 4,
  commits `0aa58c8`..`d5c511e`): HTTP security headers, login rate limiting, 3 new frontend tests
  (fragile areas), aligning touch targets to 44×44px, pCloud quota warning. Everything was tested
  (build + test suites + visual/manual verification in the embedded browser), but **not yet
  proven under prolonged real-world use**, unlike the rest of the application.

### Not broken, but still fragile / to keep an eye on

- CDK virtualization (Gallery and Album Detail, shared `PrecomputedVirtualScrollStrategy`) has
  already caused several bugs in the past (see section 3) and remains the trickiest area of the
  frontend code to touch.
- Network connectivity detection (offline mode) relies on a manual `setTimeout` pattern, judged
  necessary after concluding through real testing that the classic RxJS `timeout()` operator was
  not reliable behind the service worker — see section 3, a point not to inadvertently "simplify"
  by reintroducing `timeout()`.
- No end-to-end (E2E) integration tests exist. Current tests are unit tests only (xUnit on the
  backend, vitest on the frontend) — see section 5.

### What is broken (known)

- Nothing identified as actively broken at the time of writing. `/api/health` (supposed to be
  public per `ARCHITECTURE.md` §10) appears to actually return 401 like the rest of the API —
  observed once in passing at the very end of the previous session, never investigated or
  confirmed as a real bug (could also just be a route that isn't implemented, in which case the
  401 comes from the authorization fallback rather than an actual access control on an existing
  route). Worth checking if external monitoring (e.g. Uptime Kuma) is ever set up to hit this
  endpoint.

## 3. Accumulated knowledge / known pitfalls

This section gathers the non-obvious technical decisions and bugs already encountered — this is
the most important part for not rediscovering the same problems.

### Documentation/comment language switched to English right before this migration

On 2026-09-12, right after this document was first written, the whole project switched its
documentation and code-comment language from French to English going forward (see `CLAUDE.md` at
the repo root, which states this convention explicitly for any future session). `ARCHITECTURE.md`
and roughly 85 source files (`.cs`/`.ts`/`.html`/`.scss`) were translated in that same pass. The
application's own user-facing UI (labels, buttons, error messages shown to the user) was
deliberately **left in French** — that's a separate, permanent choice, not an oversight. If a
`git log`/`git blame` on an older commit shows French comments, that's simply history from before
this switch, not a regression — don't feel compelled to "fix" it retroactively unless you're
already touching that code for another reason.

### Internal Docker network: DNS resolution and IPv6

`reverse-proxy/nginx.conf` forces `resolver 127.0.0.11 valid=10s ipv6=off;`. On a Docker host with
IPv6 enabled on the internal network, Docker's DNS resolver can return an IPv6 address for
`backend`/`frontend` in addition to the IPv4 one — an address unreachable on that network.
Symptom observed under real conditions: intermittent `connect() failed (111: Connection
refused)`, with a **local 403 from nginx rather than a 502** (misleading — looks like an
authorization problem when it's actually a network resolution problem). Never remove `ipv6=off`
without explicitly re-testing on a host where IPv6 is active on the internal Docker network.
**Migration note**: a Raspberry Pi running Debian may have a different network/Docker
configuration than the one this bug was identified on (Windows + Docker Desktop) — re-test if
intermittent 403s appear after migration.

### `add_header` in nginx: never in `locations.conf`

The HTTP security headers (`X-Content-Type-Options`, `X-Frame-Options`, `Referrer-Policy`) are
declared in the `server` blocks of `nginx.conf`, **not** in `locations.conf` (shared by both
`server` blocks via `include`). Nginx has a non-obvious rule: an `add_header` inside a `location`
block disables inheritance of `add_header`s from the parent `server` block. Since
`locations.conf` currently declares no `add_header`, this inheritance works — but a future
`add_header` mistakenly added to `locations.conf` would silently break the headers defined at the
`server` level.

### Self-signed HTTPS: additive, no automatic redirect

The HTTP port (80, mapped to 3140) and the HTTPS port (443, mapped to `HTTPS_PORT`, 3143 by
default) work **in parallel**, with no HTTP→HTTPS redirect. Deliberate decision: nginx has no way,
from inside the container, to know which host port Docker maps onto its own 443 — a reliable
redirect isn't possible at this level without hardcoding it (fragile if `HTTPS_PORT` changes).
HTTPS exists only because the browser's Cache Storage API (offline viewing, #29) requires a
secure context — it is not a general security hardening measure.

### `docker compose up -d` does NOT recreate a service whose only change is a mounted file

The `reverse-proxy` service uses a stock nginx image (never rebuilt), with
`nginx.conf`/`locations.conf`/the certificates mounted as volumes. **Editing these files is not
enough**: `docker compose up -d` only recreates a container if its image or its declaration in
`docker-compose.yml` changes, never if only the *content* of a mounted file changed on the host
disk. You must explicitly run `docker compose restart reverse-proxy` after any nginx
configuration change (`deploy/deploy.sh` already does this systematically at the end of the
script — but a manual restart of just `reverse-proxy` during development must reproduce this same
step). This pitfall cost several failed deployments before being identified (an updated
`proxy_read_timeout` committed to the repo but left with no effect after several consecutive
deployments).

### `UseForwardedHeaders` and `Secure` cookies

The ASP.NET Core backend only ever receives plain HTTP internally (Docker network) — without the
`UseForwardedHeaders` middleware (which reads the `X-Forwarded-Proto` header forwarded by nginx),
`Request.IsHttps` would always be `false` there, even when the actual client is on HTTPS. Concrete
consequence: the session cookie and the pCloud OAuth cookie would never be marked `Secure`. The
`backend` service has no published port in `docker-compose.yml` (only reachable through the
reverse proxy) — that's what makes it safe to clear `KnownIPNetworks`/`KnownProxies` (trusting any
internal source IP). If a backend port were ever published directly, this blanket trust would
become a real risk worth revisiting.

### `HttpClient` to pCloud: an explicit timeout is mandatory

The typed `HttpClient` for pCloud (`IPCloudClient`/`PCloudClient`) has an explicit 170s timeout.
Without it, `HttpClient`'s default timeout (100s) silently applied to *every* pCloud request,
regardless of the `CancellationToken` passed at the call site — masked for a long time because
nginx's `proxy_read_timeout` (60s by default at the time) cut the connection first anyway. The
intended and currently in-place ordering: the internal application-level delay (shortest, fails
cleanly first) < the `HttpClient` timeout (170s) < nginx's `proxy_read_timeout` (180s, final
safety net). Don't change any one of these three delays without re-checking the other two.

### Thumbnail disk cache: two real bugs already fixed

Found through real testing during the initial implementation (#26): a concurrency bug (two
simultaneous requests for the same thumbnail could step on each other) and a badly calibrated
freshness delay (20s initially, raised to 90s — too short, invalidated thumbnails that were still
valid under load). See the comments in `MediaThumbnailCacheService`/
`MediaCacheEvictionBackgroundService` for detail — don't lower this delay again without
understanding why it was raised.

### Angular 22 without zone.js (zoneless) — a trap for tests and debugging

The frontend **does not use zone.js** (no `zone.js` dependency in `package.json`, no explicit
`provideZoneChangeDetection`/`provideZonelessChangeDetection` in `app.config.ts` — this is
Angular's new default behavior for a recently scaffolded project). Two concrete consequences
already encountered:

- **`fakeAsync`/`tick`** from `@angular/core/testing` **do not work** (they depend on zone.js's
  `fakeAsync` zone, absent here). Use vitest's native simulated timers
  (`vi.useFakeTimers()` / `vi.advanceTimersByTimeAsync()`) instead — see
  `frontend/src/app/core/auth/auth.service.spec.ts` for a concrete working example.
- DOM updates after an event (e.g. a synthetic click during debugging) **are not guaranteed to be
  synchronous** — an `element.click()` immediately followed by reading the DOM in the same
  synchronous block may read a state that hasn't refreshed yet. Always wait a tick
  (`await new Promise(r => setTimeout(r, ...))`) before checking the effect of a simulated click
  outside the app itself (debug scripts, exploratory testing via a browser).

Despite this, the code still contains many explicit `NgZone.run(...)` calls (inherited from the
project's history, a defensive pattern) — they are harmless in zoneless mode (effectively a
no-op) but not strictly necessary; don't be surprised by their presence, it isn't a leftover bug.

### CDK virtualization: the most fragile area of the frontend

`PrecomputedVirtualScrollStrategy` (`frontend/src/app/shared/virtual-scroll/`) is a virtualization
strategy with precomputed row heights, shared by Gallery and Album Detail. Two lessons literally
etched into the code by bugs already encountered:

- `ResizeObserver.contentRect` **excludes padding** (content-box only) — use
  `element.offsetHeight` (border-box) to measure a real height to reserve in a virtualized row
  (see `MeasureHeightDirective`).
- `.block`/`.block-content` (generic containers used by media/text blocks) **do not reliably
  propagate `height: 100%`** through the DOM chain, even inside a CSS grid — observed concretely
  (an image kept its natural size and overlapped the next row). The height must be set directly
  in JS on the concrete element (`[style.height.px]`), never through CSS cascading from an
  ancestor.

A **draggable date scrubber bar** was attempted in V2 (issue #8) and removed before the V2 release
after three unsuccessful rounds of fixes (Angular zone desync, a non-reactive `@Input()` read
inside a `computed()`, guessed-in-pixels track geometry) — replaced in V3 by a **read-only
floating date badge** (not draggable, purely reactive to scroll position), a deliberately simpler
architecture that avoids these three classes of bugs. If a scrubbing/drag navigation feature is
ever requested again, re-read the history of issue #8 before diving back in — the pitfall is not
a minor one.

### Browser automation environment: `document.visibilityState` stuck at "hidden"

Specific to the browser automation tools used during the development session (not an application
bug): the two browser tools available during development under Claude Code exposed tabs with
`document.visibilityState` permanently `"hidden"` — which prevents `ResizeObserver` from firing
during automated testing (Chrome suspends this notification in the background). A tab actually
navigated to the foreground (`foreground: true` / active tab) does NOT have this problem. Keep
this in mind if a future session needs to test `ResizeObserver`/`IntersectionObserver`-dependent
code again via browser automation: favor a foreground tab, or verify directly on a real device if
in doubt.

### Markdown rendering: `breaks: true` is mandatory

`marked` (the Markdown rendering library, single call site:
`frontend/src/app/shared/markdown.pipe.ts`) defaults to strict CommonMark behavior, where a single
line break within a paragraph collapses to a space (not a `<br>`) — counter-intuitive here, since
the text area is a free-form note typed by the user, not academic Markdown. `{ breaks: true }`
fixes this behavior. **Never remove this option** — it already caused a real regression (text
merged onto a single line) before being identified and fixed.

### `bypassSecurityTrustHtml` with no additional sanitization: a single-user assumption

Markdown rendering uses `DomSanitizer.bypassSecurityTrustHtml` with no additional HTML
sanitization — explicitly accepted because the application is single-user (the content comes from
the user themselves, not a third party). **If the application ever evolves toward multi-user use
or content sharing with third parties, this assumption no longer holds and strict sanitization
will need to be reintroduced.** Don't forget this if that direction is ever taken.

### Angular CSS budgets: `gallery.component.scss` already close to the warning threshold

The per-component style size budget (`angular.json`, `anyComponentStyle`) is 4 KB for a warning,
8 KB for an error. `gallery.component.scss` already slightly exceeds the warning threshold
(~4.7 KB) without breaking the build — not blocking, but a future substantial CSS addition to
this specific file is worth watching before it reaches the error threshold (8 KB).

## 4. Summarized history

The project was developed across three major iterations, each on its own git branch (`V1`, `V2`,
`V3` — all pushed to `origin`). `master` corresponds to the initial commit (architecture
document) and was not followed afterward — **`V3` is the actual working branch and the one to
use first after migration.**

### V1 — foundations (commits `c2c9795` to `daeb452`)

Initial scaffold (.NET backend, Angular frontend, Docker infra), single-user application
authentication, pCloud OAuth 2.0 integration, pCloud folder selection and periodic background
indexing, a Docker deployment script for a remote server (originally intended for TrueNAS
SCALE/Portainer), first bug fixes discovered through real testing on the pCloud folder picker.

### V2 — Gallery, Albums, virtualization, basic PWA (commits `26fe53b` to `e044b1c`)

The largest iteration by volume. Highlights:
- Full implementation of Gallery / Albums / Album Detail following a detailed UI/UX spec (dark
  "Nocturne" theme).
- Migration of drag-and-drop to Angular CDK, full-screen viewer, long-press, multi-select,
  merging/grouping photos into rows.
- CDK virtualization with precomputed heights (`PrecomputedVirtualScrollStrategy`), first for the
  Gallery, later reused for Album Detail.
- Markdown rendering and editing for text blocks.
- EXIF extraction (date, GPS) and Nominatim reverse geolocation running in the background after
  indexing.
- Server-side thumbnail disk cache (greatly reduces dependency on pCloud for repeated display).
- An attempt at a draggable date scrubber bar, removed before the end of V2 after several
  unsuccessful fixes (see section 3) — deferred to V3 in a different form.
- Numerous bug fixes reported through real usage (location filters, mobile layout broken by
  filters, text readability, freeze on large albums, disposed SQLite object, parallelization of
  deletions, in-memory pCloud token caching, SQLite WAL mode).

### V3 — automated tests, installable PWA, offline, HTTPS, date badge, closeout (commits `2ac4e88` to `d5c511e`)

- First automated backend tests (xUnit) — the suite went from zero tests to a reusable base
  (pCloud fakes, real in-memory SQLite for constraint testing, DI scope isolation tests).
- Reduced initial bundle via per-route lazy loading, pCloud token caching, extracting the date
  taken from videos, richer information in the full-screen viewer, wide-screen responsive design.
- Reorganizing albums into sections (drag-and-drop), deletion gated behind Organize mode.
- Thumbnail cache: usage displayed in Configuration (#27), optional automatic re-indexing per
  source folder (#28).
- **Installable PWA** (#14), then, right after, **offline album viewing** (#29, a big chunk:
  secure-context detection, self-signed HTTPS, network resilience, a manual offline-mode toggle
  after several rounds of fixes to automatic detection deemed unreliable, parallelizing thumbnail
  downloads).
- Fix for text block height computation in the virtualized view (#30), which also revealed and
  fixed a Markdown rendering regression (merged line breaks).
- **Read-only floating date badge** in the Gallery (#8), replacing the old draggable scrubber bar
  abandoned in V2, with a deliberately simpler architecture (one-way data flow, no state to
  resynchronize).
- **Milestone closeout**: a deliberate UI/UX, architecture, and security review before considering
  V3 done, resulting in 5 targeted fixes (HTTP security headers, login rate limiting, tests on
  fragile areas, 44×44px touch targets, pCloud quota warning) — see section 2 for the detail of
  what was verified or not.

## 5. Dependencies and environment

### Runtimes / languages

| Component | Target version | Where it's declared |
|---|---|---|
| .NET SDK/Runtime | **10.0** | `backend/src/RPhotoAlbum.Api/RPhotoAlbum.Api.csproj` (`TargetFramework net10.0`), Dockerfiles (`mcr.microsoft.com/dotnet/sdk:10.0` / `aspnet:10.0`) |
| Node.js | **24.x** | `frontend/Dockerfile` (`node:24-alpine`) — no `engines` field in `package.json` |
| Angular | **^22.1.x** | `frontend/package.json` (zoneless framework, see section 3) |
| TypeScript | **~6.0.2** | `frontend/package.json` |

### Tools required on the development/deployment machine

- Docker Engine + the `docker compose` (v2) plugin — `deploy/deploy.sh` also accepts the legacy
  `docker-compose` binary as a fallback.
- `git`.
- `openssl` (used by `reverse-proxy/generate-cert.sh` to generate the self-signed TLS certificate
  — natively present on most Debian distributions, but worth confirming).
- For local development **outside Docker** (recommended for fast iteration, which is how the
  previous session worked — rebuilding Docker only for final visual checks against the real
  stack): a native .NET 10 SDK and native Node.js 24 installed on the machine.
- `gh` (GitHub CLI) — used during development to read/close GitHub issues directly, not strictly
  required but very convenient if issue tracking continues to be done this way.

### Expected environment variables (`.env`, never committed — see `.env.example`)

| Variable | Role |
|---|---|
| `PCLOUD_CLIENT_ID` / `PCLOUD_CLIENT_SECRET` | pCloud OAuth application credentials |
| `PCLOUD_REDIRECT_URI` | OAuth callback URL (must match the app registered with pCloud) |
| `APP_BASE_URL` | Application base URL (used by the backend) |
| `APP_AUTH_SECRET` | Application secret (session cookie protection) |
| `APP_ADMIN_USERNAME` / `APP_ADMIN_PASSWORD_HASH` | The single account's credentials — the hash is generated via `dotnet run -- hash-password <password>` in `backend/src/RPhotoAlbum.Api` (see `Program.cs`, a special CLI mode) |
| `INDEXING_INTERVAL_MINUTES` | Frequency of periodic source folder re-indexing (default 15) |
| `LOG_LEVEL` | Serilog log level (default `Information`) |
| `TLS_SAN_IP` | Server's LAN IP, included in the self-signed HTTPS certificate |
| `HTTPS_PORT` | Host port for HTTPS access (default 3143; 3140 remains the HTTP port) |
| `MEDIA_CACHE_MAX_SIZE_MB` | Max size of the thumbnail disk cache (default 1024, see `docker-compose.yml`) |

See section 7 for detail on how to reconfigure each secret on the new environment.

### External services used

- **pCloud** (OAuth 2.0 + storage API) — the core of the project, see `ARCHITECTURE.md` §5.
- **Nominatim** (OpenStreetMap, free reverse geolocation) — called by `GeoLookupService` with a
  mandatory custom `User-Agent` (Nominatim usage policy, see the comment in `Program.cs`). No API
  key required, but subject to a fair-use policy (rate limited) — do not parallelize calls
  aggressively.
- **GitHub** (`Rothen68/RPhotoAlbum`) — repository hosting and issue/milestone tracking.
- Standard public Docker image registries (`mcr.microsoft.com`, `docker.io` for `node`, `nginx`,
  `amir20/dozzle`) — no authentication required, but they do require outbound Internet access
  from the Raspberry Pi at image build/pull time.

## 6. Windows specifics to adapt for Linux

Good general news: the project was designed from the start for a Docker deployment on a remote
Linux server (see `ARCHITECTURE.md` §2, §16) — the Windows machine only ever served as a
*development* machine, never as a deployment target. There is therefore **no structural Windows
dependency** in the application code itself. The following points are the only items actually
tied to the previous development machine's Windows environment:

- **Scripts**: `deploy/deploy.sh` and `reverse-proxy/generate-cert.sh` are already bash scripts
  (no project-owned `.ps1`/`.bat` files — the only `.ps1` files in the repo are artifacts
  generated by npm under `frontend/node_modules/.bin/`, unrelated to the project itself and
  regenerated on every `npm ci` anyway, see section 9).
- **Line endings (CRLF/LF)**: the previous Windows development machine has `core.autocrlf=true`
  enabled (confirmed), and the repo has no `.gitattributes`. This combination means files are
  **stored as LF in the git objects** (explicitly verified on `deploy/deploy.sh`: no `\r` in the
  blob) despite CRLF showing up in the local Windows working copy — a `git clone` on the Pi
  should therefore pull files that are already LF, with no conversion needed. Still worth
  double-checking after cloning (`file deploy/deploy.sh` should report "ASCII text", not "with
  CRLF line terminators") before relying on it.
- **Executable bit lost on a script**: `reverse-proxy/generate-cert.sh` is tracked by git as mode
  `100644` (not executable), unlike `deploy/deploy.sh` which is `100755`. This isn't a consequence
  of Windows (which doesn't track any Unix execute bit anyway) — the script simply never needed to
  be directly executable since `deploy.sh` invokes it via `bash reverse-proxy/generate-cert.sh`
  rather than `./reverse-proxy/generate-cert.sh`. On Linux, if this script ever needs to be run
  directly (`./generate-cert.sh`), it will need `chmod +x reverse-proxy/generate-cert.sh` first
  (and that mode change should be committed if it should persist for everyone).
- **Windows-only tools**: no build tool or dependency requires Windows. The only Windows-related
  npm packages present in `frontend/node_modules/` (e.g. `@rolldown/binding-win32-x64-msvc`,
  `lightningcss-win32-x64-msvc`, `@parcel/watcher-win32-x64`, `@lmdb/lmdb-win32-x64`,
  `@napi-rs/nice-win32-x64-msvc`) are optional native binaries automatically selected by npm based
  on the OS at install time — **never copy `node_modules/` to the Pi** (see section 9), a fresh
  `npm ci` on the Pi will automatically install the correct `linux-arm64` equivalents.
- **Machine-specific ports/paths**: no hardcoded Windows path found anywhere in the project's
  source code (an explicit search was run) — only the `127.0.0.1` addresses already documented in
  section 3, and the actual LAN IP configured in `.env` (specific to each deployment, not to the
  development machine), appear in the nginx configuration.
- **File permissions**: beyond the `generate-cert.sh` case already mentioned, nothing else
  identified. The generated TLS certificate (`reverse-proxy/certs/server.key`) is explicitly
  `chmod 600` by the script itself — behavior already designed for Linux, nothing to adapt.
- **CPU architecture (arm64 vs x86_64)**: not strictly a Windows issue, but it becomes genuinely
  relevant on a Raspberry Pi and has never been tested until now (the development machine was
  x86_64) — see section 9 for the associated risks in detail.

## 7. Secrets and credentials

No real secret value is included in this document, nor in the git repo (`.env` is gitignored,
only `.env.example` — with no values — is versioned). On the previous Windows machine, a real
`.env` file exists at the repo root (14 lines, values filled in) — it **must be manually copied
to the Pi over a secure channel** (e.g. direct `scp` between the two machines, never through
chat/messaging/the git repo) rather than rebuilt from scratch, if the intent is to keep using the
same pCloud account/credentials without further manual intervention.

| Secret | Where it's stored | How to reconfigure it |
|---|---|---|
| `PCLOUD_CLIENT_ID` / `PCLOUD_CLIENT_SECRET` | `.env` (repo root) | From the pCloud developer console (OAuth application already registered) — if the server's IP/domain changes, `PCLOUD_REDIRECT_URI` must stay consistent with what's registered on the pCloud side |
| `PCLOUD_REDIRECT_URI` | `.env` | Must point to `<APP_BASE_URL>/api/auth/pcloud/callback` on the new server |
| `APP_AUTH_SECRET` | `.env` | Can be regenerated (e.g. `openssl rand -hex 32`) — regenerating this secret invalidates existing active sessions, with no other consequence |
| `APP_ADMIN_USERNAME` / `APP_ADMIN_PASSWORD_HASH` | `.env` | The hash is generated via `dotnet run -- hash-password <password>` from `backend/src/RPhotoAlbum.Api` (a special CLI mode of `Program.cs`, does not start the web server in this case) |
| `TLS_SAN_IP` | `.env` | The Raspberry Pi's actual LAN IP on the new network — **update this on the very first deployment on the Pi** if the IP differs from the old server, then delete `reverse-proxy/certs/` to force certificate regeneration (otherwise the old certificate, with the old IP as SAN, stays in place and the browser will refuse HTTPS access by IP) |
| pCloud access token (OAuth) | Stored server-side via `PCloudTokenStore` (persisted in the cache's SQLite database, not in `.env`) | Regenerates automatically on the first pCloud OAuth login from the Configuration screen after migration — no need to transfer it manually, but the pCloud connection will need to be redone once on the new server (the SQLite cache is not transferred, see section 9) |
| ASP.NET Core Data Protection key | Files under `/data/keys` in the backend container (`backend-data` volume) | Regenerates automatically on first startup if absent — not a secret to transfer, just an expected consequence of a fresh Docker volume on the Pi (invalidates existing session cookies, nothing more) |

## 8. TODO / next steps

In the order they had been envisioned before the migration:

1. **Verify that everything still works identically after migration** before adding anything new
   — in particular: the Docker build on arm64 architecture (never tested, see section 9), offline
   viewing (#29, depends on the browser's Cache Storage API — should be independent of the server
   but deserves a real check), and the 5 fixes from the end of the previous session (rate
   limiting, security headers, touch targets, pCloud quota warning) which don't yet have prolonged
   real-world use behind them.
2. **Close issue #29 on GitHub** once point 1 is confirmed — it is functionally done but never
   formally closed (see section 2).
3. **Issue #7** (search/filter by tags, V3 milestone, never started) — requires thinking from the
   outset about the CPU/RAM cost of the local automatic tagging tool, a constraint that becomes
   noticeably more concrete on a Raspberry Pi 4 than on a typical development workstation. Probably
   the right moment to run a first real load test on the Pi before committing to the
   implementation.
4. No other explicit TODO beyond these two issues was identified as planned at this stage — the
   V3 milestone was in the process of being closed out at the time of migration (UI/UX/
   architecture/security review completed, see sections 2 and 4).

## 9. Points to watch for the migration itself

- **CPU architecture never tested on arm64**: this is the single most significant risk of this
  migration. All the Docker images used (`mcr.microsoft.com/dotnet/sdk:10.0`,
  `mcr.microsoft.com/dotnet/aspnet:10.0`, `node:24-alpine`, `nginx:1.27-alpine`,
  `amir20/dozzle:latest`) publish `linux/arm64` variants, so a `docker compose build` should work
  as-is — but this has never actually been verified on this project. Budget time for a first
  build/deployment that could reveal surprises (a missing image for a transitive dependency,
  different behavior from a native package like SQLitePCLRaw).
- **Confirm a 64-bit OS (arm64), not 32-bit (armhf)**, before even starting — .NET 10 has only
  limited/deprecated support for 32-bit ARM. Raspberry Pi OS/Debian must be installed in the
  64-bit variant on the Pi 4.
- **Limited Pi 4 resources for the *build*** (RAM in particular, depending on the 2/4/8 GB model):
  the frontend build (Angular/esbuild/rolldown bundler) and the .NET publish step can be memory-
  hungry. If `docker compose build` fails or is abnormally slow/OOMs on the Pi, consider building
  the images on a more powerful machine (or via `docker buildx` targeting `linux/arm64` from the
  Windows machine/a CI) and then transferring/pushing them to a registry, rather than building
  directly on the Pi.
- **Do NOT copy `node_modules/`, `bin/`, `obj/`, `dist/`, `.angular/`** — all gitignored, all to be
  regenerated on the Pi (`npm ci` on the frontend side, `dotnet restore`/`build` on the backend
  side for native development outside Docker). Copying these directories from Windows would bring
  in `win32-x64` native binaries that are completely unusable on arm64 Linux (confirmed, see
  section 6).
- **Do NOT copy the cache's SQLite database or the thumbnail disk cache**
  (`*.db`/`*.db-shm`/`*.db-wal`, the `backend-data` Docker volume, the `/data/thumbnails`/
  `/data/keys` folders inside the container) — fully reconstructible from pCloud by design
  (`ARCHITECTURE.md` §3). Copying them would introduce a SQLite database frozen at a Windows point
  in time, with no benefit, and would prevent verifying that rebuilding from scratch works well on
  the new environment. After migration, the first startup will require a full re-index (the
  "Reindex" button in Configuration, or waiting for the next automatic cycle) — this is not a
  problem, it's the expected normal behavior.
- **The self-signed TLS certificate should not be copied as-is** if it contains the old LAN IP as
  SAN (`reverse-proxy/certs/`, gitignored) — regenerate it on the Pi via
  `reverse-proxy/generate-cert.sh` once `TLS_SAN_IP` is updated in the new `.env` (see section 7).
  Every device that previously trusted the old certificate will need to re-approve the new one.
- **`.env` must be transferred manually over a secure channel**, never through a means that would
  route it through an untrusted third party (see section 7) — it's the only file containing real
  secret values on the current machine.
- **Verify scripts are executable after cloning on the Pi**: `deploy/deploy.sh` should be `+x`
  (bit preserved by git, `100755`), but `reverse-proxy/generate-cert.sh` is not (see section 6) —
  no consequence for `deploy.sh` itself, which invokes it via `bash ...`, but worth keeping in
  mind if it's ever run directly.
- **`gh` (GitHub CLI) and its authentication** are not transferred automatically — if the new
  Claude Code session on the Pi needs to read/close GitHub issues the way this was done during
  development, `gh auth login` (or the applicable equivalent in that new environment) will need
  to be redone.
- **The remote-access VPN** (mentioned in `ARCHITECTURE.md` §2, outside the scope of this repo) is
  not documented here in detail — its configuration (likely tied to the old server) will need to
  be reconstructed separately to point to the new Raspberry Pi, a topic entirely outside this
  project's code and therefore outside what this document can cover.
