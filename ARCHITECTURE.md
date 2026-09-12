# Detailed Architecture Document — pCloud-backed Photo/Video Album Application

> **Revision 2 — 2026-08-14**
> This document replaces the initial version following these decisions: **Angular** frontend
> (instead of React), **.NET Core** backend (instead of Node.js), addition of a **local SQLite
> cache** (rebuildable performance index), **compression disabled** for this version (raw copy
> only), **single-user application authentication** (login/password), removal of the compression
> `worker` container.
>
> **Revision 3 — 2026-08-15**
> UI/UX refined based on a detailed spec (Gallery/Albums/Album Detail, dark "Nocturne" theme).
> Substantial changes compared to revision 2:
> - **Global Gallery**: the media grid is no longer filtered by album or by added/available
>   status — it shows all indexed (non-rejected) media. Adding to one or more albums is done via
>   multi-select followed by an album choice (bottom sheet), rather than a per-album add/reject
>   flow.
> - **Rejection is now global** (no longer per album, §6.3 of revision 2): a rejected media item
>   disappears permanently from the Gallery, regardless of album. Triggered from the Gallery's
>   selection mode ("Reject" button next to "Add to Album").
> - **A media item can belong to several albums**: each membership is an independent `AlbumItem`
>   (with its own position), see §6.2.
> - Copy-on-add (§9.6) **kept as-is**: adding a media item to an album still duplicates the file
>   into the album's pCloud folder, so it doesn't depend on the source folders' permanence.

## 1. Purpose

This web application lets you create, view, and edit photo/video albums enriched with Markdown
text, ordered chronologically, and entirely stored on pCloud. The application itself is hosted on
a private Docker server, on the user's home network (accessed via an existing VPN), while all
media and album JSON files are stored on pCloud. [docs.pcloud](https://docs.pcloud.com/)

## 2. Hosting context

The deployment targets a private server, located on the user's home network and administered by
them. Remote access goes through an already-existing VPN — the application is not publicly
exposed on the Internet. The application is delivered as Docker containers separating the
frontend, the backend API, and the HTTPS reverse proxy.

Target architecture:
- a **reverse-proxy** container (Traefik or Nginx) for TLS, routing, and security headers;
- a **backend** container (ASP.NET Core Web API) for business logic, pCloud integration, and
  authentication;
- a **frontend** container (statically built Angular app, served by Nginx) for the user
  interface.

No media processing container (`worker`) is planned in this version: compression is on standby
(see §13).

An optional **logs** container ([Dozzle](https://dozzle.dev/)) exposes a real-time Docker log
viewer (dedicated port, outside the reverse proxy) for operational diagnostics. It reads the
Docker socket read-only and has no access to any application data; its protection relies on the
same network perimeter (VPN/LAN) as the rest of the deployment.

## 3. Guiding principles

Architecture choices must respect the following principles:
- pCloud as the source of truth for albums and media;
- a **local SQLite cache** is allowed as a performance index (metadata, pagination, thumbnails),
  provided it is fully rebuildable from pCloud — it stores no business data that wouldn't already
  exist on pCloud;
- pCloud secrets and tokens kept server-side only;
- application access protected by a single-user application login/password pair, in addition to
  network restriction via VPN;
- mobile-first interface usable on smartphone, tablet, and PC;
- clean separation between source media, media duplicated into an album, and editorial metadata.

## 4. Logical overview

The logical architecture is made up of four domains.

### 4.1 Web interface (Angular)

The frontend provides:
- the login screen (login/password);
- configuration of pCloud source folders;
- selection of the albums' parent folder;
- creating, viewing, and editing albums;
- a chronological timeline view;
- inserting Markdown text blocks;
- bulk actions from the Gallery: multi-select, global rejection, adding to one or more albums;
- paginated navigation through the grid of available media, with thumbnails.

### 4.2 Application API (.NET Core)

The backend centralizes:
- application authentication (login/password) and the user session;
- authentication toward pCloud (OAuth 2.0);
- reading source folders and updating the local cache;
- metadata normalization;
- creating album subfolders;
- duplicating (raw copy) media;
- reading/writing album JSON files;
- computing the global rejection status of media and their album memberships;
- paginating media lists.

### 4.3 pCloud storage

pCloud hosts:
- the external source folders entered by the user;
- a parent folder reserved for albums;
- one subfolder per album;
- the album JSON files;
- media copied into albums (raw copy, no transformation).

### 4.4 Local cache (SQLite)

The local cache handles:
- indexing source media (pCloud id, name, date, hash, status) to paginate and sort without
  re-scanning pCloud on every request;
- lightweight indexing of albums (id, name, last-updated date) for the album list;
- a full rebuild possible at any time via a pCloud scan, in case the cache is lost or
  inconsistent.

## 5. pCloud integration

pCloud exposes an HTTP/JSON API and requires using the correct endpoint depending on where the
user's data is located. The documentation states that OAuth 2.0 authorization code flow is the
recommended mode when an application has a server, and that the `locationid` and `hostname`
parameters returned during authorization are used to determine the correct API host, notably
Europe or United States. [docs.pcloud](https://docs.pcloud.com/)

### 5.1 pCloud authentication

The recommended flow is as follows:
1. The user, already logged into the application (application login), triggers the pCloud
   connection in the UI.
2. The backend redirects to the pCloud authorization screen.
3. pCloud returns a `code`, a `locationid`, and a `hostname`.
4. The backend exchanges the `code` for an `access_token` via `oauth2_token`.
5. The backend stores this token securely server-side, never in the frontend.
   [docs.pcloud](https://docs.pcloud.com/methods/oauth_2.0/authorize.html)

### 5.2 Application authentication

In addition to pCloud OAuth, access to the application itself is protected by a single-user
login/password:
1. The Angular frontend presents a login screen.
2. The backend validates the credentials against a single configured account (username +
   password hash stored as a Docker environment variable/secret).
3. A session (secure cookie or JWT) is issued and required for all `/api/*` routes except
   `/api/auth/login` and `/api/health`.
This layer is on top of the network access restriction (VPN) already in place, and does not aim
for multi-user or role-based management.

### 5.3 Regional handling

The backend must remember the `hostname` or derive the correct API endpoint to avoid errors
related to data location. The pCloud documentation specifies that calls must target
`api.pcloud.com` or `eapi.pcloud.com` depending on the user's datacenter.
[docs.pcloud](https://docs.pcloud.com/)

### 5.4 Thumbnails

For images with the `thumb` flag, pCloud provides `getthumblink`, which returns a thumbnail link
at a requested size. The dimensions must respect specific constraints, and thumbnails are
generated on first call and then cached on pCloud's side.
[docs.pcloud](https://docs.pcloud.com/methods/thumbnails/getthumblink.html)

Architectural consequence:
- the (paginated) editing grid requests pCloud thumbnails for each displayed page;
- the backend may proxy these URLs to simplify security and caching;
- the local SQLite cache may store the obtained thumbnail URL and its associated source hash, to
  avoid redundant `getthumblink` calls;
- the file hash must be watched to invalidate a thumbnail that has become stale, per pCloud
  documentation. [docs.pcloud](https://docs.pcloud.com/methods/thumbnails/getthumblink.html)

## 6. Data model

### 6.1 Source of truth vs cache

- **Source of truth**: the `album.json` files on pCloud (see §6.2).
- **Performance cache**: local SQLite database, purely derived, rebuildable at any time. It
  indexes:
  - media found in source folders (pCloud id, name, hash, date, dimensions, thumbnail link);
  - a lightweight summary of each album (id, name, slug, last-updated date) for fast display of
    the album list without downloading each JSON file.

### 6.2 Main entities

- **Connection configuration**: pCloud access information and folder choices.
- **Application account**: the single user's username and password hash.
- **Album**: global metadata (name, pCloud folder) and its ordered list of blocks (`items`).
- **AlbumItem (block)**: a timeline element of an album, of type `media` (reference to a media
  item + its copy in the album folder) or `text` (Markdown content). The same media item can
  appear in several albums; each membership is a distinct `AlbumItem` with its own position — see
  revision 3 at the top of this document.
- **Indexed media (cache)**: a local cache entry for a file detected in a source folder (pCloud
  id, hash, dates, type). Also carries **rejection**, now **global** (see §6.4) and no longer per
  album.

### 6.3 Recommended album schema

```json
{
  "id": "alb_20260703_ab12cd",
  "slug": "brittany-vacation-2026",
  "name": "Brittany Vacation 2026",
  "createdAt": "2026-07-03T12:00:00Z",
  "updatedAt": "2026-07-03T12:00:00Z",
  "albumFolder": {
    "folderId": 2001,
    "path": "/RPhotoAlbum/alb_20260703_ab12cd"
  },
  "items": [
    {
      "id": "itm_001",
      "type": "media",
      "mediaType": "image",
      "date": "2026-06-14T08:21:00Z",
      "source": {
        "fileId": 3001,
        "path": "/Sources/DCIM/IMG_1001.JPG",
        "hash": "1234567890",
        "name": "IMG_1001.JPG"
      },
      "albumCopy": {
        "fileId": 4001,
        "path": "/RPhotoAlbum/alb_20260703_ab12cd/IMG_1001.JPG",
        "variant": "original"
      },
      "technical": {
        "thumb": true,
        "width": 4032,
        "height": 3024,
        "size": 2844412,
        "rotate": 0
      }
    },
    {
      "id": "txt_002",
      "type": "text",
      "date": "2026-06-14T12:00:00Z",
      "markdown": "## Arrival\nLovely weather, calm sea."
    }
  ]
}
```

All elements of `items` are, by definition, "added" to this album — there is no more per-item
`status` field, nor a `rejected` list in the album JSON (rejection is now global, see §6.4). The
order of the `items` array is the display order, editable via Reorder mode (§11.7).

### 6.4 States of a media item

An indexed media item can be:
- **available**: visible in the Gallery, selectable to be added to one or more albums;
- **rejected**: permanently excluded from the Gallery (a global indicator, stored on the
  corresponding cache entry — not in an `album.json`). Triggered from the Gallery's selection
  mode (§11.3).

A media item can simultaneously be "available" (visible in the Gallery) and already present in
one or more albums — the two are not mutually exclusive, unlike the previous revision of this
document.

## 7. Dating and chronological sorting rules

pCloud returns various metadata such as `created`, `modified`, `width`, `height`, `duration`,
`rotate`, `thumb`, and `category`, useful for classifying and presenting media. These fields
remain pCloud storage/processing metadata, however, and don't systematically replace an EXIF date
or an editorial date chosen by the user. [docs.pcloud](https://docs.pcloud.com/)

The rule for computing the sort date must be, in order:
1. date manually corrected in the album;
2. date extracted from the media's native metadata by the backend, if available;
3. pCloud `created` date;
4. pCloud `modified` date;
5. date the item was added to the album.

The user flow then displays items from most recent to oldest, per the stated requirement.

## 8. Physical structure on pCloud

The recommended target structure is as follows:

```text
/RPhotoAlbum
  /albums
    /alb_20260703_ab12cd
      album.json
      IMG_1001.JPG
      VID_2033.mp4
    /alb_20260704_ef34gh
      album.json
      ...
```

Each album has its own subfolder in order to isolate:
- the business JSON file;
- the media items actually kept, as raw copies (no compressed derivatives in this version).

This separation prevents an album from changing if the source folders are later modified or
deleted.

## 9. Application services

The (.NET Core) backend can be split into clearly defined services.

### 9.1 Application authentication service

Responsibilities:
- validate the single account's credentials;
- issue and verify the session (secure cookie or JWT);
- protect API routes.

### 9.2 Configuration service

Responsibilities:
- store the minimal application configuration;
- validate source folder and parent folder identifiers;
- test pCloud access rights.

### 9.3 pCloud service

Responsibilities:
- encapsulate API calls (typed C# HTTP client);
- handle OAuth 2.0;
- resolve the correct API host;
- list folders, files, and metadata;
- retrieve thumbnails and download links;
- create folders and upload JSON files.

### 9.4 Indexing / cache service (SQLite via EF Core)

Responsibilities:
- scan configured source folders and populate the local cache;
- filter images and videos;
- normalize metadata;
- expose a sorted, **paginated** merged list;
- refresh or rebuild the cache on demand.

### 9.5 Album service

Responsibilities:
- create, list, and delete albums;
- read and write `album.json` (`items` blocks, order);
- bulk add/remove media ("Add to Album" flow, §11.4);
- insert, edit, and delete Markdown text blocks;
- apply the new block order (Reorder, §11.7);
- for a set of selected media, determine which albums already fully contain all of them (for the
  "included" state of the bottom sheet, §11.4).

### 9.6 Media ingestion service

Responsibilities:
- copy the source media into the album folder (raw copy, no compression) when added;
- link the source and the copy in the album's JSON;
- delete the album copy (and the corresponding block) when removed, never touching the source
  file in the source folder.

### 9.7 Markdown rendering service

Responsibilities:
- convert Markdown into sanitized HTML;
- prevent unwanted HTML injection;
- keep text blocks consistent between viewing and editing.

## 10. Proposed internal API

The application API may expose the following routes.

| Method | Route | Usage |
|---|---|---|
| POST | `/api/auth/login` | Application login (login/password) |
| POST | `/api/auth/logout` | Application logout |
| GET | `/api/health` | Technical service health check |
| GET | `/api/config` | Read the application configuration |
| PUT | `/api/config` | Save source folder IDs and the parent folder |
| GET | `/api/auth/pcloud/start` | Start pCloud OAuth |
| GET | `/api/auth/pcloud/callback` | pCloud OAuth callback |
| GET | `/api/pcloud/status` | pCloud connection status (connected/hostname) |
| POST | `/api/pcloud/disconnect` | Disconnect the pCloud account |
| GET | `/api/pcloud/folders/:folderId` | Browse pCloud folders (folder picker) |
| GET | `/api/media/source?page=&pageSize=` | Paginated list of available media (non-rejected, via cache) |
| POST | `/api/media/reindex` | Rebuild the local cache from pCloud |
| POST | `/api/media/reject` | Global rejection of one or more media items (hidden from the Gallery) |
| POST | `/api/albums` | Create an album (name only) |
| GET | `/api/albums` | List albums (cover, item count) |
| GET | `/api/albums/:id` | Detailed read of an album (ordered blocks) |
| DELETE | `/api/albums/:id` | Delete an album |
| POST | `/api/albums/membership` | For a set of media items, indicate which albums already fully contain all of them ("Add to Album" bottom sheet) |
| POST | `/api/albums/:id/media/add` | Bulk add media to the album (raw copy to the album folder) |
| POST | `/api/albums/:id/media/remove` | Bulk remove media from the album (deletes the album copy, not the source) |
| POST | `/api/albums/:id/text` | Insert a Markdown text block at a given position |
| PUT | `/api/albums/:id/items/:itemId` | Edit a text block |
| DELETE | `/api/albums/:id/items/:itemId` | Remove a block (media or text) from the album |
| PUT | `/api/albums/:id/order` | New block order (Reorder) |

All `/api/*` routes, except `/api/auth/login` and `/api/health`, require a valid application
session.

## 11. Detailed functional flows

### 11.1 Login

1. The user opens the application (via VPN).
2. They enter their username and password.
3. The backend validates and issues a session.
4. The frontend redirects to the album list.

### 11.2 Initial configuration

1. The user connects their pCloud account (OAuth).
2. The user enters the source folder IDs.
3. The user enters the albums' parent folder.
4. The application validates folder access and triggers a first indexing pass (SQLite cache).
5. The configuration is saved on the backend.

### 11.3 Creating an album

1. From the Albums screen, the user taps "+": a dialog opens with a single field (name),
   auto-focused.
2. "Create" stays disabled while the name is empty; Enter or "Create" creates an empty album and
   closes the dialog.
3. An album can also be created on the fly from the "Add to Album" bottom sheet (§11.4),
   pre-filled with the media currently selected.

### 11.4 Selecting, rejecting, and adding to one or more albums (Gallery)

1. The Gallery shows all non-rejected indexed media in a grid (adjustable columns, 1 to 4).
2. The user enables selection mode ("Select" button) and checks one or more media items.
3. An action bar appears at the bottom: selected count, a "Reject" button, and a primary "Add to
   Album" button (disabled while nothing is selected).
4. "Reject" marks the selected media items as rejected (globally) and immediately removes them
   from the grid.
5. "Add to Album" opens a bottom sheet listing "New album" then each existing album, with its
   inclusion state (included if all selected media items are already in it). Tapping an album
   toggles the inclusion of **all** selected media items in that album: adds the ones missing, or
   removes all of them if already all present (tapping again = safe undo).
6. Each addition copies the file (raw copy) into the album's pCloud folder and inserts a `media`
   block into `album.json`; each removal deletes the block and its associated copy, without
   touching the source file.
7. Changes apply immediately, with no separate save step. "Done" closes the sheet; "Cancel" or
   the end of the flow exits selection mode and clears the selection.

### 11.5 Inserting text into an album

1. In Album Detail, the user taps the "+" shown between two blocks (or before the first one).
2. An italic inline text field opens at that exact position, with focus.
3. Losing focus with non-empty text creates a `text` block at that position; an empty field
   creates nothing.
4. Tapping an existing text block (outside Reorder mode) reopens its inline editing; fully
   clearing it on focus loss deletes the block.

### 11.6 Reordering and removing blocks

1. "Reorder" switches the album into reorganization mode: each block gains a drag handle, up/down
   buttons, and a delete button (×).
2. Drag-and-drop moves a block to the target position; the up/down buttons offer a touch-friendly
   alternative.
3. The (×) button removes a block (media or text) from the album — the associated pCloud copy is
   deleted, the source media item never is.
4. "Done" exits reorganization mode.

## 12. Synchronization rules

System consistency relies on simple rules.
- A `media` block in an album must always have a copy in that album's folder.
- A rejected media item (global indicator on the cache) must no longer appear in the Gallery,
  regardless of album — but remains unchanged in albums it had already been added to before its
  rejection.
- A source media item deleted after being added to an album remains visible in that album via the
  album copy, as long as it still exists.
- A source media item deleted before ever being added must no longer appear in the Gallery on the
  next indexing pass.
- The local SQLite cache can become inconsistent with pCloud (source moved/deleted outside the
  application); a manual rebuild (`POST /api/media/reindex`) must always restore a consistent
  state. Global rejection, although carried by a cache entry, is a user choice and not
  reconstructible data — see §6.4.
- Regenerating the Gallery and Album Detail must be idempotent from the local cache and the
  `album.json` files.

## 13. Compression and optimization — out of scope for v1 (standby)

Compression is **disabled for this version**: all media (images and videos) is copied raw from
the source folder to the album folder, with no resizing or re-encoding.

This decision simplifies the ingestion pipeline (§9.6) and removes the need for a dedicated
`worker` container (§16) as well as media-processing dependencies (Sharp/FFmpeg) for this version.

Future direction, not to be implemented now: reintroduce an optional compression step (resized
images, re-encoded videos) if storage volume or mobile display smoothness justifies it. pCloud
thumbnails (`getthumblink`, §5.4) are sufficient for the editing grid in the meantime.

## 14. Security

The minimum requirements are as follows:
- pCloud authentication via server-side-only code flow;
- single-user application authentication (hashed login/password, e.g. ASP.NET Core
  `PasswordHasher`) in addition to network access restriction via VPN;
- application secret and tokens in Docker environment variables;
- HTTPS recommended even on the local network;
- logging with no token or password leakage;
- strict validation of folder IDs and Markdown input. [docs.pcloud](https://docs.pcloud.com/)

pCloud tokens can be used via a parameter in API calls — `auth` for a token from
password-based authentication, `access_token` for a token from the OAuth 2.0 flow (using `auth`
with an OAuth token fails silently with pCloud's generic *"Log in failed"* error, result 2000). In
both cases, this requires strong vigilance over logs, network traces, and application errors to
never expose this parameter client-side or in diagnostic files.
[docs.pcloud](https://docs.pcloud.com/methods/intro/authentication.html)

## 15. Observability and operations

The system must provide:
- an `/api/health` endpoint;
- structured logs (e.g. Serilog);
- indexing/cache logs;
- a clear error level for pCloud failures;
- per-request correlation logging.

Simple metrics are enough for a first pass:
- source folder scan/indexing time;
- time to add a media item;
- pCloud upload failure rate;
- local cache size (number of entries).

## 16. Target Docker architecture

A recommended Docker composition:
- `reverse-proxy`: Traefik or Nginx, TLS termination, routing `/` to the frontend and `/api` to
  the backend;
- `frontend`: statically built Angular application, served by Nginx;
- `backend`: ASP.NET Core API, also hosting the cache's SQLite database (dedicated local volume).

Minimum environment variables:
- `PCLOUD_CLIENT_ID`
- `PCLOUD_CLIENT_SECRET`
- `PCLOUD_REDIRECT_URI`
- `APP_BASE_URL`
- `APP_AUTH_SECRET`
- `APP_ADMIN_USERNAME`
- `APP_ADMIN_PASSWORD_HASH`
- `LOG_LEVEL`

No persistent business-data volume is required (all business data is externalized to pCloud). A
local volume is kept for:
- the cache's SQLite database (purely rebuildable);
- technical logs.

## 17. Recommended stack

### Frontend

- Angular (latest stable version), TypeScript
- Angular Router, Angular Forms (reactive)
- HTTP client (`HttpClient`) with interceptors for session handling and error management
- lightweight UI library (e.g. Angular Material) or custom components
- sanitized Markdown rendering (e.g. `ngx-markdown` with sanitation)

### Backend

- .NET Core (ASP.NET Core Web API), C#
- Entity Framework Core + SQLite for the local cache
- dedicated typed HTTP client for pCloud (`HttpClientFactory`)
- minimal ASP.NET Core Identity, or a lightweight custom implementation, for single-user login
- Serilog for structured logging
- xUnit for tests

### Deployment

- Docker Compose for the first version
- HTTPS reverse proxy
- simple CI for build and deployment

## 18. Functional screen mockups

Screens derived from the detailed UI/UX spec (revision 3, dark "Nocturne" theme):
- Application login (login/password)
- pCloud configuration (OAuth, albums folder, source folders)
- **Gallery** — main tab, grid of all non-rejected media, column-count control (1-4), selection
  mode with "Reject" / "Add to Album" actions
- **Add to Album** — bottom sheet triggered from selection: on-the-fly album creation + per-album
  inclusion toggle
- **Albums** — second main tab, card list (cover, name, item count), creation via a "+" dialog
- **Album Detail** — vertical flow of media/text blocks, inline text insertion, Reorder mode
  (drag-and-drop + up/down buttons + block deletion)

A two-entry tab bar (Gallery / Albums) always visible, except in Album Detail (full-screen child
view) and during the Gallery's selection mode (which replaces the header and adds a bottom action
bar, but does not hide the tab bar).

## 19. UX requirements

The application's design must follow responsive webapp conventions: a single clear primary action
per screen, mobile-first design, compact text sizes, touch targets of at least 44×44 px, and
navigation that adapts appropriately between mobile and desktop.

Concrete implications, as specified by the detailed spec:
- dense, sober interface: subtle, fast animations (~150-180ms), no showy effects;
- Gallery grid: square tiles, 6px spacing, 8px rounded corners; video thumbnails with a play icon
  and duration overlay;
- selection mode: selected tiles slightly shrunk (0.94×) with an accent-colored outline and check
  badge;
- destructive actions: deleting an album is irreversible and requires confirmation (the only case
  in the application); removing a block from an album is trivially reversible (the source media
  item is never touched) and therefore requires no confirmation;
- clear visual separation between available media (Gallery) and rejected media (hidden).

## 20. Technical risks

The main risks are:
- ambiguity between the pCloud date and the actual date the photo/video was taken;
- network latency when scanning/indexing large source folders (mitigated by the SQLite cache and
  pagination, §9.4);
- the local cache drifting out of sync with pCloud's actual state if files are modified outside
  the application (mitigated by manual rebuilding, §12);
- unchecked growth of pCloud storage volume in the absence of compression (§13);
- pCloud API host errors if Europe/US location handling is mishandled.
  [docs.pcloud](https://docs.pcloud.com/)
