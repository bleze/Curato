# Curato — Project Plan

A self-hosted photo/video server: it organizes a library into dated, titled folders
(with smart event/trip grouping) and serves a fast, Picasa-like web UI to browse and
view it, from any client on the network — a browser on Windows, a native app on an
Android TV. The server itself runs wherever makes sense (a Synology NAS, a Windows PC,
eventually the TV device itself) since the whole engine is built as portable .NET, not
tied to one OS. Local, on-device face recognition is a planned but deferred capability —
the data model and pipeline are built to support it from day one even though the
feature ships later.

**Architecture in one paragraph**: one server (`Curato.Api`, ASP.NET Core) owns the
library — the actual files, the SQLite database, all business logic — and exposes it
over HTTP. Every client (a browser, a native Android TV app) is just a UI calling that
API; none of them touch the filesystem directly, except for uploading brand-new files
(a camera, a phone export, an iCloud download) that aren't in the library yet. This
replaces the earlier "native Windows desktop app with a portable per-folder database"
shape considered earlier in this plan's history — most of the *design* below (grouping,
organizing, tagging, editing, recycle bin, backup) carries over unchanged, since it was
already written as plain .NET business logic independent of any UI; what changes is
*where* it runs and *how* you reach it.

## 1. Tech stack & topology

- **.NET 10** (LTS; built/verified with VS2026, SDK 10.0.401 on this machine) throughout
  — chosen specifically because it's genuinely cross-platform (Windows, Linux, and —
  see §16 — Android), so the *same* server code can run on a Synology NAS, a spare
  Windows PC, or eventually be embedded directly in a TV app, without a rewrite per
  platform.
- **`Curato.Api`** (ASP.NET Core + Kestrel): the server. Hosts the REST API, runs all
  business logic below, and serves the built web frontend's static files directly (one
  deployable artifact, no separate frontend server/CORS setup to manage).
- **`Curato.Web`**: the browser frontend — a TypeScript SPA, **React + TanStack Virtual**
  for the media grid's virtualization. Talks to `Curato.Api` over HTTP/JSON exactly
  like any other client would.
- **SQLite** (via `Microsoft.Data.Sqlite`) — one central database now, not one per
  folder (see §2). Perfectly sufficient at personal/family library scale with a single
  server process serializing writes; revisit only if real multi-writer contention shows
  up later.
- **MetadataExtractor** (NuGet) for EXIF/photo metadata (DateTaken, GPS, camera) — reads
  EXIF out of RAW files too, so RAW photos get correct dates/grouping even before
  decoding a preview image.
- **FFmpeg** for poster-frame thumbnails (not built yet) — packaged per-platform (a Linux
  static build in the Docker image, a Windows build for the Windows-hosted server).
  [Video *creation time* turned out not to need FFmpeg at all: MetadataExtractor (already
  a dependency for photo EXIF) also reads the QuickTime/MP4-family "movie header" atom
  that .mov/.mp4/.m4v/.3gp store it in — one fewer packaging dependency for that specific
  need. This was a real, silent gap for a while: `MediaFileScanner` always fell back to
  the file's filesystem date for every video, `TakenAtSource.VideoMetadata` existed in the
  enum but nothing ever produced it. AVI/Matroska/WebM/WMV still have no reader in
  MetadataExtractor and fall back to filesystem date as before.]
- **HEIC support** (Apple's format, confirmed in scope): a cross-platform HEIF decode
  path (e.g. `libheif` bindings) rather than Windows' WIC extension, since the server no
  longer assumes Windows.
- **RAW support** (DSLR files, confirmed in scope, **preview-based, not full RAW
  development**): every common RAW format (CR2/CR3, NEF, ARW, DNG, ...) embeds a
  full-size JPEG preview — Curato reads EXIF plus that embedded preview for
  thumbnails/viewing, rather than implementing real RAW demosaicing, which is a
  professional-editing feature outside a Picasa-like organizer's scope.
- **ONNX Runtime** (phase 5, later) for local face detection + embedding models — runs
  server-side, no cloud calls, but see §14 for the real hardware-tradeoff this
  introduces now that "the server" might be a NAS with no GPU.

### Solution layout

```
Curato.sln
  Curato.Core/        # domain models, clustering engine, naming engine (no UI, no DB, no OS-specific code)
  Curato.Data/         # SQLite schema, repositories, migrations
  Curato.Media/        # EXIF/video metadata readers, thumbnail generation, hashing
  Curato.Organizer/    # file move/copy engine, undo log, conflict resolution, backup
  Curato.Vision/        # (phase 5, stub now) ONNX face detection/embedding + clustering
  Curato.Api/          # ASP.NET Core host: REST API + serves Curato.Web's built output
  Curato.Web/          # TypeScript SPA frontend (separate build, not a .NET project)
  Curato.Tests/
```

`Curato.Core`/`Data`/`Media`/`Organizer`/`Vision` have zero OS-specific dependencies by
design — that's what makes running the same `Curato.Api` on Windows, Linux, and
(eventually) Android a realistic goal instead of three separate implementations.

### Where the server runs

- **Synology / Linux (primary target, and the only one being built right now)**: a
  Docker image, multi-arch (`linux/amd64` + `linux/arm64` to cover both Synology CPU
  families), run via DSM's Container Manager — the same deployment pattern as
  Plex/Immich/PhotoPrism on a NAS. This is where implementation starts.
- **Windows — paused, not part of current implementation.** The design stays recorded
  (the same `Curato.Api` would run as a **system-tray app** on a PC, for anyone who'd
  rather dedicate a Windows machine than a NAS, at the cost of only running while
  someone's logged in — unlike the always-on NAS/Docker case), but it's deliberately
  not being built yet since it isn't the deployment actually being used. Nothing in
  `Curato.Api`/`Core`/`Data`/`Media`/`Organizer` is Windows-specific either way, so
  picking this back up later is additive, not a redesign.
- **Android TV, as a stretch goal (§16)**: eventually embedding `Curato.Api` in-process
  inside the Android TV client itself, so a device like an Nvidia Shield could be a
  fully standalone box — its own storage, its own server, its own UI, no NAS or PC
  required. Flagged explicitly as higher-risk/unproven (Android's background-service
  lifecycle limits, native library packaging for SQLite/FFmpeg on Android) — see §16 for
- **Android TV, as a stretch goal (§16)**: eventually embedding `Curato.Api` in-process
  inside the Android TV client itself, so a device like an Nvidia Shield could be a
  fully standalone box — its own storage, its own server, its own UI, no NAS or PC
  required. Flagged explicitly as higher-risk/unproven (Android's background-service
  lifecycle limits, native library packaging for SQLite/FFmpeg on Android) — see §16 for
  why this ships after, not instead of, the Windows/Linux server.

## 2. Data model (SQLite)

### Library storage: one server, one database, portable by design

The server owns one central `library.db`, not one per folder — but the same
reinstall-proofing principle from earlier design still applies, just phrased for a
server: **redeploying the server (new NAS, rebuilt Docker container, migrated to a
different machine) should recover everything from nothing more than re-mounting the
same photo volume(s).**

- `library.db` and the Recycle Bin's trash (§5) live inside a `.curato/` folder placed
  **on the same volume as the photos** (e.g. `/volume1/Photos/.curato/` on a Synology
  NAS), not in a container-local path that would vanish when the container is
  recreated. Anything that is irreplaceable *user data* — the database, pending
  deletions — travels with the photos; anything regenerable (thumbnail cache,
  downloaded AI model weights, hardware-probe results) lives in a separate data volume
  that's fine to lose and rebuild.
- **Every path stored in the database is relative to its configured library root**,
  never absolute. `MediaItem.FilePath`, `Event`/`TripGroup.FolderPath`, `DeletedItem`
  trash paths — all relative, so remounting a volume at a different container path, or
  migrating to new NAS hardware, never breaks a single reference.
- The server can be configured with **multiple library roots** (e.g. two different NAS
  shares, or a NAS share plus a locally-attached drive) — they browse seamlessly as one
  merged Library/Events/Trips/Tags view across every configured root, not a
  per-root switcher the user has to think about. Each item still knows which physical
  root it lives in, needed for Organize/Backup/Recycle Bin to act on the right place —
  that's just not something browsing normally surfaces.
- A **"merge libraries"** action (move everything from one root into another,
  re-parenting its Events/Trips/Tags in the database) is a natural later feature — e.g.
  consolidating two drives into one — but isn't needed for the seamless multi-root
  browsing above to work, so it's not committed to a phase yet.

```
MediaItem
  Id, FilePath, Kind (Photo/Video),
  FileSizeBytes, QuickHash, ContentHash (nullable, computed lazily),
  TakenAt (UTC, best-effort from EXIF/video/file time), TakenAtSource,
  Width, Height, DurationMs (video), ImportedAt,
  LivePhotoVideoItemId (nullable, set on a Photo row — see below)

[Phase 1/2 addition: Apple Live Photos (iCloud/Photos export) land as a
JPG/HEIC + same-named `.MOV` pair, sitting side by side in the source
folder — without handling, that's two separate gallery tiles for what's
one logical photo. LivePhotoPairer (Curato.Core) matches same-folder,
same-filename-stem, close-timestamp Photo+Video pairs after every scan and
sets LivePhotoVideoItemId on the photo's row; nothing on disk is renamed or
merged, both files stay independently tracked/organized/recoverable. The
web gallery (§11) excludes a paired video from its own tile and shows a
"LIVE" badge + play toggle on the photo's tile instead. Deleting a Live
Photo's still frame cascades to recycle its paired video too (§5), so one
click doesn't leave an orphaned sidecar file behind. The matched sidecar
extension is a deliberately narrow, named whitelist (Apple's `.MOV`, plus
`.mp4` for Samsung/Google Motion Photos exported/backed up as a split pair)
rather than "any video with the same name," to avoid wrongly pairing two
genuinely unrelated files that happen to share a filename — a real but
smaller risk for `.mp4` specifically, since it's a much more generic video
format than `.MOV`. Samsung/Google Motion Photos' more common *single-file*
form (one JPEG with the clip embedded as trailing data + an XMP marker) has
no "double files" problem to solve at all; playing that embedded clip back
would be a separate, unbuilt feature — extracting the trailing MP4 via its
XMP byte offset, not pairing two MediaItems.]

Event                      -- a "moment": one logical shoot, may cross midnight
  Id, StartAt, EndAt, Title, TitleIsAuto,
  FolderPath, TripGroupId (nullable),
  NamingMode (Adopted/CuratoManaged)   -- Adopted: Curato never renames/moves this folder

EventMediaItem
  EventId, MediaItemId, SortOrder

TripGroup                    -- a "vacation": a run of nearby Events
  Id, StartDate, EndDate, Title, TitleIsAuto, FolderPath, NamingMode

Tag                           -- a Tag *is* an Album: freeform, user-named, cross-folder
  Id, Name, IsFavoriteShortcut (the one tag the F-key toggles), SortOrder

MediaItemTag                  -- many-to-many: any item can carry any number of tags
  TagId, MediaItemId, AddedAt, ManualSortOrder (nullable — per-album ordering)

Edit                          -- non-destructive; never touches the original file's bytes
  Id, MediaItemId, OperationType (Crop/Rotate/Brightness/Contrast/Saturation/
    RedEye/BlackAndWhite/Sepia/FillLight/Sharpen/...),
  Parameters (JSON, op-specific), PipelineOrder, Source (Picasa/Local)

DeletedItem                   -- the in-app Recycle Bin (§5) — every deletion lands here
  Id, MediaItemId, OriginalFilePath, TrashFilePath, ThumbnailPath,
  DeletedAt, PurgeAt, Source (DuplicateFinder/Manual/...)
  [Phase 1 built shape: the MediaItem row is deleted outright at delete-time
  rather than kept alongside its DeletedItem row, so restoring just puts the
  file back and lets the next scan re-discover it rather than reconstructing
  its old Event/Trip membership. No stored ThumbnailPath either — the
  Recycle Bin UI's thumbnail endpoint generates one on demand straight from
  the trashed file, so there's never a second on-disk copy to keep in sync.]

BackupDestination             -- Auto Backup (§6)
  Id, Path, Label, Enabled, LastSuccessAt, LastError

BackupRecord                  -- what's already been copied where, to avoid re-hashing
  MediaItemId, DestinationId, BackedUpAt, VerifiedContentHash

Person                       -- phase 5
  Id, Name, RepresentativeFaceId   -- display avatar only; matching uses every
                                    -- confirmed Face for this Person (§14), not just this one

Face                         -- phase 5
  Id, MediaItemId, PersonId (nullable = unassigned),
  BoundingBox, Embedding (BLOB, float[], nullable), Confidence, Source (Picasa/Local),
  Ignored (bool) -- "not someone to track" (§14) — distinct from unreviewed/unassigned
```

`Event` and `TripGroup` both store a `FolderPath`. For `CuratoManaged` rows, the
physical folder name is derived from a template + title, and titles are user-editable
after the fact — the DB row is the source of truth; the folder is a rendering of it. A
"reconcile" pass matches folders back to DB rows by `ContentHash` if someone renames
folders manually outside Curato. For `Adopted` rows, it's the reverse: `FolderPath` is
whatever the user already had, is never rewritten by Curato, and a title edit updates
only the DB's display title (see §3, Step 0).

## 3. Grouping engine (the core hard problem)

Per your requirements: grouping is **not** simple calendar-day bucketing. An evening
party that runs past midnight must stay one folder; conversely a single calendar day
with a quiet morning and a distinct evening event should become *two* folders. So the
atomic unit is a **Event**, produced by time-gap clustering over each `MediaItem`'s
`TakenAt`, not by date.

### Step 0 — Existing folders are always the boundary; no mode to pick

**An existing folder boundary is a decision the user already made, and it's obeyed —
automatically, without asking.** Two photos taken seconds apart can belong in different
folders because the user judged them unrelated (different subjects, different reasons
for taking them) even though a timestamp-gap algorithm would happily cluster them
together. That judgment — made once, maybe decades ago — has to survive import
unchanged. So this isn't a mode the user picks per source; it's a structural rule
applied automatically, folder by folder, as the scanner walks the tree:

- **A folder that already contains files is treated as a single, atomic unit.** Its
  contents map 1:1 to an `Event` (or `TripGroup` → `Event` for an obvious two-level
  nesting), keeping the current name, files, and location. Curato's gap-based
  clustering (Steps 1–2 below) **never runs across a folder boundary and never splits
  a folder's contents apart** — it can't merge two folders together no matter how close
  in time their photos are, and it can't decide a folder should really be two Events.
  Concretely:
  - **No file moves, no renames, no reclustering.** Curato indexes the archive where it
    already lives; a configured library root can simply *be* the existing tree.
  - The folder name is parsed for a display title/date (`YYYY-MM-DD Title`,
    `Title (YYYY-MM-DD)`, etc.) purely for nicer display; if nothing matches, the
    literal folder name is kept, verbatim, as the title. This never touches the actual
    folder on disk.
  - These become `NamingMode = Adopted` events/trips in the data model (§2) — Curato
    will *never* auto-rename or move an Adopted folder's contents on its own.
  - Purely informational, never actioned automatically: if a folder's contained photos
    span a suspiciously wide date range, Curato may flag it ("this folder spans 3
    years — you may want to review it") as an FYI the user can ignore.
- **Loose files with no enclosing folder of their own** — sitting directly in whatever
  root is configured, with no subfolder around them (a camera SD card, a phone's `DCIM`
  dump, a staging "Imports" folder where everything just landed in one pile) — are the
  *only* input that goes through gap-based clustering + the folder-naming template
  (Steps 1–4), ending in the dry-run preview and a **copy** (never move/delete, per §4)
  into the library. These become `NamingMode = CuratoManaged` events/trips, which *do*
  follow the naming template and *do* rename their folder when their title is edited
  later.

No upfront question, no per-source choice to get wrong — the shape of what's already on
disk decides. The preview always shows the result either way, and the manual override
controls (Step 5) still let the user split, merge, or regroup anything the automatic
pass got wrong, exactly as before. An adopted 20-year archive stays exactly as it is;
new incoming photos from a phone or camera get auto-organized and land in the library
using Curato's naming convention going forward — without ever touching how the old
material is arranged.

### Restructure to Curato format (separate, optional, later)

Renaming an Adopted folder's *physical* name/nesting to match Curato's own template
(§3, Step 4) is deliberately a distinct action from import — never bundled into it,
never automatic. While Curato is in use, the on-disk name barely matters; it browses via
its own database regardless of what a folder is called. But a database is also a single
point of failure a folder tree isn't: if Curato is ever discontinued (the fate that
actually befell Picasa) or the files are opened years from now by something that's
never heard of Curato's database, a plain, human-readable
`2026/2026-07-10 to 2026-07-14 Summer Vacation` folder tree is what keeps the archive
legible on its own. So "Restructure to Curato format" is offered as an explicit,
opt-in action — per-folder or for a whole root at once — that renames/reshapes an
Adopted folder to the standard template, purely for that long-term portability; skipping
it entirely changes nothing about how the app itself works day to day.

### Step 1 — Event detection (gap-based clustering)
1. Sort all media by `TakenAt`.
2. Walk the sorted list; start a new `Event` whenever the gap to the previous item
   exceeds a configurable threshold (default **4 hours**, user-adjustable).
3. This naturally keeps a midnight-crossing party in one event (gaps between party
   photos stay small even as the clock rolls over), and naturally splits a single
   calendar day into two events when there's a multi-hour lull (e.g., morning outing →
   6-hour gap → evening event).
4. Minimum event size (e.g. 1 item) — singletons are fine, just get their own folder.

[Real bug found and fixed: this sort-by-TakenAt guarantee only held the *first*
time an Event was created. When a re-scan added newly-found files to an
*already-adopted* folder's existing Event (`LibraryRepository.
AddMediaItemsToEvent`), the new items were simply appended with an
ever-increasing `SortOrder`, regardless of their actual `TakenAt` — so a
file added in a later scan always landed at the end of that Event's
thumb-grid, even if it was chronologically the very first photo in the
folder. Fixed by re-sorting the *whole* membership (existing + newly-added)
by `TakenAt` and reassigning every row's `SortOrder` accordingly, rather
than only ever appending.]

### Step 2 — Trip/range detection (grouping Events into a TripGroup)
1. Walk consecutive `Event`s; group into a `TripGroup` when the gap between one
   event's end and the next event's start is below a larger threshold (default
   **36 hours**) **and** the resulting group spans at least N days (default 2) — a
   single busy day that produced 2 events should *not* become a "trip" of its own.
2. `TripGroup`s are proposals: shown to the user before organizing, with merge/split
   controls, but no manual date-range selection required for the common case.

[**Disabled in the built Phase 1/2 pipeline** — TripClusterer (this
algorithm) is implemented and unit-tested, but ScanService no longer calls
it to auto-create TripGroups. With zero location signal, a pure time gap
can't tell "an actual trip away from home" apart from an ordinary run of
days that happen to have photos on them — for anyone who takes photos close
to daily, nearly their whole library has <36h gaps between consecutive
days' Events, so it was merging unrelated weeks/months into sprawling,
meaningless "trips." Confirmed against a real library and fixed per
explicit product decision rather than just tuning the threshold, since any
purely time-based number has the same fundamental problem. Adopted
TripGroups (§3 Step 0 — real folder structure the user already made, e.g.
`2019 Summer/Day 1`, `2019 Summer/Day 2`) are unaffected; that's reading
existing organization, never a guess. A library scanned before this fix
self-heals on its next scan: `LibraryRepository.RemoveAutoFormedTripGroups`
ungroups any leftover CuratoManaged TripGroup back into standalone Events
(no file or MediaItem is touched). The real fix is Step 3's location-aware
naming logic below, generalized into location-aware *grouping* — tracked
against Phase 4 (Map view, §12) landing GPS EXIF extraction, not scheduled
before then.]

### Step 3 — Titling
- Auto-title an `Event` from its date/time-of-day (e.g. "Morning", "Evening") unless
  the user supplies one; auto-title a `TripGroup` as its date range until named.
- **Location-aware trip naming, when GPS data is available**: reverse-geocode a
  `TripGroup`'s photos (reusing Map view's reverse-geocoding, §12 — see that section's
  note on it being the one network-dependent feature) and use the **plurality
  location** — the place name shared by the most photos, not just the first or last —
  as the default title instead of a bare date range, e.g. "Mallorca" for a week's trip.
  Using the plurality rather than first/last matters exactly because of airport photos
  at each end: a handful of shots at a departure/arrival airport shouldn't dilute or
  override the name of the place where most of the week was actually spent. This is
  purely a *naming* enhancement — it never changes which photos belong to the trip
  (that's still Step 1–2's gap-based clustering, which already includes the bookending
  airport photos naturally, since they're time-contiguous with the rest of the trip) —
  and it degrades gracefully to the plain date-range title whenever geocoding is
  unavailable (offline, or GPS-less photos), so it never becomes a hard dependency for
  the otherwise fully offline Import & Organize flow.
- Titles are edited any time, before or after files are moved; editing re-renders the
  folder name from the template (see below) and the Organizer renames the folder on
  disk to match.

### Step 4 — Folder naming (configurable template)
Tokens: `{year}`, `{date}`, `{date:yyyy-MM-dd}`, `{dateRange}`, `{title}`, `{tripTitle}`.
Default templates nest everything under a year folder:
- Top-level year folder: `{year}` → `2026`
- Event (no trip): `{year}/{date} {title}` → `2026/2026-09-19 Birthday`
- Event (inside a trip): `{date} {title}`, nested inside the trip's own folder (no
  need to repeat the year a second time one level down).
- Trip: `{year}/{dateRange} {tripTitle}` → `2026/2026-07-10 to 2026-07-14 Summer
  Vacation`. A trip that crosses a year boundary (e.g. Dec 30 → Jan 2) uses the trip's
  *start* date's year — simple, deterministic, and rare enough not to need more.

Example resulting tree:
```
Library/
  2026/
    2026-07-10 to 2026-07-14 Summer Vacation/
      2026-07-10 Arrival/
      2026-07-11 Beach Day/
      2026-07-11 Evening Bonfire/      <- same calendar day, second event
      2026-07-12 Hiking/
    2026-09-19 Birthday/               <- standalone event, no trip
  2025/
    ...
```

Year nesting is just the *default* template — like every other naming choice here it's
configurable in `OrganizerSettings`, and it only ever applies to `CuratoManaged` folders
(§3, Step 0); `Adopted` archives keep whatever structure they already had, year folders
or not.

All thresholds (event gap, trip gap, min trip span, naming template) live in one
`OrganizerSettings` object, editable via the web UI.

### Step 5 — Manual override controls (auto-grouping is a proposal, not a verdict)

The clustering above gets the common case right automatically, but the user must be
able to correct it in a couple of clicks, on the Import & Organize preview *and* later
from the library (re-grouping already-organized folders):

- **Split an event in two**: pick a split point (a specific photo/time in the event's
  filmstrip) and everything from that point on becomes a new sibling `Event` — the
  inverse of "same day, split" auto-detection, for when the gap-threshold guessed wrong.
- **Merge events into one**: select two or more adjacent events and merge them into
  a single `Event` (used when, say, the bonfire and the beach day should actually be
  one folder) — re-derives `StartAt`/`EndAt` and re-renders the folder name.
- **Group events into a range/trip**: select several events (adjacent or not
  necessarily contiguous) and "Group into trip," name it, and they become a `TripGroup`
  — the inverse of relying on the 36-hour/2-day auto-trip heuristic; also covers
  ungrouping an event back out of a trip.
- **Shift timestamps by a fixed offset**: select a batch and nudge every `TakenAt` by
  +/- hours/minutes before (re-)clustering. Multi-device import (two people's cameras
  from the same trip) mostly needs no special handling at all — the gap-based
  clustering already works off the flat timestamp list regardless of which device a
  photo came from, so overlapping-timestamp imports from two cameras naturally land in
  the same Event/Trip together. The one real gap is a camera whose clock was never set
  correctly (off by hours, sometimes days) — this control fixes exactly that, so a
  clock-drifted device's photos cluster correctly instead of splitting off on their own.

All four are undoable and re-render folder names/paths immediately in the preview
(files don't actually move until "Organize" is confirmed) — see the Import & Organize
Preview mockup in `docs/DESIGN.md` for the split/merge affordances on each row (note:
that mockup predates this server/web pivot and shows a WinUI desktop shell — the
*content*/flow it depicts is still accurate, the visual chrome will differ in the
actual web UI).

## 4. File organizer

**Organize never deletes anything. That's a hard rule, not a default that can be
toggled off by accident.**

- Moving a file already inside the library (e.g. re-rendering a folder path after a
  rename/split/merge) is a same-volume rename — the file just relocates, so there's no
  copy/delete step and nothing to lose.
- Bringing files in from an external **source** — a browser upload from a camera/SD
  card/phone/iCloud export sitting on a client device (§4a) — is always a **copy**:
  received bytes are written, verified (hash match), and kept; nothing on the client
  device is ever touched or expected to be deleted by Curato.
- An **undo log** (a manifest of every move/copy in a batch) makes each Organize run
  reversible regardless.
- Conflict handling: duplicate filenames get suffixed; duplicate *content* is only ever
  flagged for the separate Duplicate Finder (§7) to deal with — Organize itself doesn't
  skip or dedupe anything unprompted.
- A "dry run" preview screen shows the proposed folder tree before anything moves —
  nothing happens without the user seeing the plan and confirming it first.
- Designed as a library (`Curato.Organizer`) called by the API, so it can also run
  headless/scheduled server-side (e.g., watching a designated "drop" folder on the
  server's own filesystem) independent of any client being connected.

### 4a. Getting new files in: upload, not local file access

Since the server owns the storage and clients don't have filesystem access, "importing
new photos" is a normal upload, same shape as any self-hosted photo tool (Google
Photos, Immich):
- **Browser upload (v1/MVP)**: drag-and-drop or a file/folder picker in the web UI
  sends new files to the server over HTTP; the server runs the usual EXIF-read →
  cluster → preview → copy-into-library pipeline (§3–4) exactly as if it had found them
  on disk itself.
- **A designated local "drop" folder on the server's own filesystem** also works for
  free with no upload step at all — e.g. an SMB share the NAS already exposes, or a
  folder a Windows-hosted server watches directly — since the server has ordinary local
  file access to whatever's already on its own machine.
- **A native Windows Importer Helper** (§17) is a later, optional convenience on top of
  plain browser upload — auto-detecting camera/SD-card insertion and uploading in the
  background — not required for the core flow to work.
- **Google Photos: via its official sync client's local folder, not a direct API
  integration.** Google locked its Library API's broad read-your-library scope down in
  April 2025 — apps can now only manage content *they* uploaded, or use a new Picker API
  that's a one-time manual-selection flow, not a background auto-import mechanism
  ([Google Photos API updates](https://developers.google.com/photos/support/updates),
  [Photos API policy](https://developers.google.com/photos/support/api-policy)). An
  unofficial/reverse-engineered client exists, but isn't worth the same trade-off iCloud
  made below, since **Google Drive for Desktop** already materializes cloud photos into
  an ordinary local folder the "designated local drop folder" mechanism above handles
  with zero new code — same trick as the OneDrive Backup Destination (§6), just pulling
  in instead of pushing out.
- **iCloud: direct integration — reversing this plan's original "official client only"
  decision.** Originally rejected for the same reason as Google above (Apple has never
  published a public API for third-party access to iCloud Photos; an unofficial client
  is "the same fragile-unversioned-API pattern already rejected for Synology," §6) in
  favor of relying on **iCloud for Windows** materializing photos into a local drop
  folder. That reasoning didn't survive contact with reality: iCloud's own web/Windows
  download path was confirmed live stripping Exif (`DateTimeOriginal`/`DateTimeDigitized`/
  GPS, down to a bare version tag) for a real fraction of files — the exact problem this
  feature exists to solve — and **iCloud for Windows doesn't exist for the primary
  Synology/Linux deployment target at all**, so the "official path" the original decision
  leaned on isn't even available where this app is actually meant to run.
  [Phase 2 built shape: direct integration via
  [icloudpd](https://github.com/icloud-photos-downloader/icloud_photos_downloader)
  (MIT-licensed) — invoked as an external process (`Curato.iCloud`), not ported into C#,
  since that's a mature, maintained reverse-engineering of Apple's own iCloud-web API
  surface rather than something worth redoing from scratch. Crucially, `icloudpd`'s
  `--set-exif-datetime` flag writes iCloud's own internal (correct) date back into the
  downloaded file's Exif — directly fixing the metadata-stripping problem, something the
  official client apparently doesn't do reliably.

  Getting the one-time Apple ID login/2FA handshake right took two failed attempts,
  both confirmed live against a real account before landing on the actual fix:
  1. A custom script driving the standalone `pyicloud` PyPI package directly (a clean
     `requires_2fa`/`validate_2fa_code`/`trust_session` API) seemed like the obvious
     choice — until live testing showed entering a correct 2FA code just produced
     another 2FA prompt, every time. Root cause: `icloudpd` doesn't actually depend on
     that package at all — it vendors its own separate, incompatible fork
     (`pyicloud_ipd`) with a different session/cookie format, so a session established
     via plain `pyicloud` was never recognized by `icloudpd`'s own sync run.
  2. Rewriting the same script against `pyicloud_ipd` directly was the next instinct,
     but that class isn't a stable public API (a `domain`/`password_provider`-callable/
     `response_observer`/`client_id` constructor clearly meant for internal use), and
     replicating icloudpd's own auth-flow behavior exactly would mean re-deriving
     everything `icloudpd` already does.

  The actual fix: stop building a separate script and drive the *real* `icloudpd` CLI
  directly for the handshake too, via its own `--auth-only` mode ("performs
  authentication, persists tokens/cookies, and exits without processing assets") — this
  guarantees byte-for-byte session compatibility with the sync step, since it's the
  literal same code path. `Curato.iCloud`'s `iCloudAuthClient` starts one `icloudpd
  --auth-only` process and reads its output incrementally (not line-buffered — its
  prompts don't end in a newline until answered) watching for a "...authentication
  code..." substring, since icloudpd's 2FA prompt uses plain `input()` (confirmed in its
  source), which respects redirected stdin normally. The *password* prompt, by contrast,
  uses `getpass.getpass()`, which tries to read directly from the console device and
  behaves unreliably for a console-less background child process — exactly the context
  Curato.Api spawns it in — so the password goes in via icloudpd's own `--password` flag
  instead (a command-line argument, technically visible to another OS user via a process
  listing for icloudpd's brief run — accepted as the pragmatic trade-off over an
  interactive prompt that risks not working at all on Windows). If 2FA is required, the
  process is held open (a single mutable field is proportionate — one iCloud account per
  server, so only ever one login in flight) until the code arrives over stdin
  (`VerifyTwoFactorAsync`) or the user backs out (`cancel-login`), closing/killing it
  either way. Only `icloudpd`'s own session/cookie files persist, sharing the same
  `--cookie-directory` the recurring sync reads — kept at the same trust boundary as
  `server.db` and the library itself, both already unencrypted on the same volume,
  rather than building a new encryption-at-rest subsystem for arguably-not-more-sensitive
  data.

  Downloaded files land in an `iCloud Sync/` staging subfolder inside the new shared
  "library folder" setting (below) and are fed through the *existing* scan pipeline
  exactly like a browser upload — zero new clustering/auto-titling logic, since loose new
  files already get auto-clustered and auto-titled. A new `LibraryOperationGate`
  (`Curato.Organizer`) serializes ScanService/OrganizeService calls per library root,
  since a periodic background sync is the first thing that can genuinely race a
  user-triggered scan/upload/organize — nothing defended against that before.

  Confirmed live why an unbounded scope matters: a real account's first sync reported
  9604 items with no cap at all (`RunFullBackfillAsync` originally passed no
  `--until-found` limit). `iCloudSync.LookbackDays` (default 365, editable in Settings,
  applies to every sync, not just the first) bounds this via icloudpd's own
  `--skip-created-before {N}d` flag rather than a Curato-side date filter, so the
  same year's worth of history downloads either way — first sync or incremental.]
- **A new shared "library folder" setting** (`Curato.Data`'s `LibraryFolderRepository`,
  surfaced in a new minimal Settings panel) — the destination iCloud sync (and any future
  external-source puller) copies into, since there's nothing to "organize in place" for
  content that doesn't exist locally yet. Deliberately additive, not a change to how
  scanning/organizing a folder you point Curato at directly already works — that stays
  exactly as described above. First real driver of the Settings surface this plan has
  gestured at since §4a/§5 (Picasa auto-import opt-out, recycle-bin retention), kept
  intentionally minimal rather than building a generic settings framework ahead of a
  second concrete need.

## 5. Recycle Bin (every deletion in the app goes through here)

A second hard rule, alongside "Organize never deletes": **nothing Curato ever removes
disappears immediately.** Not just Duplicate Finder — any deletion feature the app ever
grows routes through the same mechanism. This is deliberately Curato's *own* recycle
bin, not the OS's: it needs to show rich per-item preview/metadata in the web UI and
enforce a Curato-specific retention policy, neither of which an OS-level trash can do.

- **On delete**: the file moves from its library location into a Curato-managed trash
  folder inside the relevant library root (`<LibraryRoot>/.curato/RecycleBin/<id>/
  <original filename>` — see §2's storage note; a deleted-but-not-yet-purged photo is
  real user data, not a cache, so it lives with the library), and a `DeletedItem` row
  (§2) records the original path, when it was deleted, why (which feature deleted it),
  and a retained thumbnail — reusing the gallery's already-cached thumbnail (§11), so
  the preview still renders even though the working file moved.
- **Browsable, its own nav entry**: every pending-deletion item, shown with its preview
  image, deletion date, days remaining before permanent purge, and where it came from.
  Two actions per item: **Restore** (back to its original location, removed from the
  bin, back in the library) or **Delete Permanently** (skips the wait — the one place in
  the app with a stronger, explicit confirmation, since it's the one truly irreversible
  action).
- **Auto-purge after 30 days** (configurable in Settings), matching the convention
  Apple's iCloud Photos "Recently Deleted" set — a background job checked on server
  startup and periodically while running permanently removes anything past its
  `PurgeAt`.
- Every other deletion-capable feature in the plan (Duplicate Finder §7, and any future
  one) is written to always go through this, never straight to a permanent delete.

## 6. Auto Backup

Prompted directly by a real failure mode: a NAS syncing to OneDrive that "keeps
breaking" — silently, which is the actual problem. Redundancy that fails invisibly isn't
redundancy. Curato's backup is meant to replace that fragile pipeline with something
that's verified and visibly honest about its own health — and since the server now runs
continuously on the NAS (or a dedicated PC) rather than an app the user opens
occasionally, it's a more natural fit for scheduled backups than it would've been as a
desktop-only feature.

- **Backup Destinations are just filesystem paths** — a second NAS share, a
  OneDrive-synced local folder, an external drive, whatever the server's OS can mount
  as an ordinary path; multiple destinations can run side by side for redundancy.
- Runs automatically (after an Organize completes, and/or on a schedule) and copies
  new/changed `MediaItem`s to each destination, mirroring the library's folder
  structure, verified by hash after copy — the same copy-then-verify discipline as the
  Organizer (§4).
- **Additive only — mirrors forward, never mirrors deletions.** If something is removed
  or sent to the Recycle Bin (§5), its existing backup copies are left alone. This is
  deliberate: the backup is then an independent safety net against Curato's *own*
  mistakes too (say, the Recycle Bin's 30-day auto-purge turning out to be premature for
  one item) — not just a second copy riding the same lifecycle as the first.
- **Status is visible, never silent.** A Backup screen shows each destination's last
  successful run, how many items are pending, and — the important part — surfaces
  clearly when a destination has gone unreachable rather than failing quietly the way
  the current setup does.
- Runs inside the server process (`Curato.Organizer`, alongside the move/copy engine)
  on its own schedule — no separate always-on client needed, since the server itself is
  now the thing that's always on.

## 7. Duplicate finder

Goal: find exact duplicate photos/videos (same content, possibly re-imported from a
second phone backup, a cloud sync folder, and the camera's SD card) **without** reading
whole files — a full SHA-256 over a 4K video library would mean hours of disk I/O.

**Two-tier hashing**, computed during the same pass that already opens each file for
EXIF/video metadata (so it's close to free):

1. **QuickHash (cheap, always computed)** — a fast non-cryptographic hash (xxHash128 or
   BLAKE3) over `FileSizeBytes` + a few fixed-size sampled byte ranges (e.g. first 64 KB,
   last 64 KB, and a 64 KB slice from the middle — the whole file for anything smaller
   than ~200 KB). This is O(1) disk seeks per file regardless of file size, so it costs
   almost nothing even for multi-gigabyte videos.
2. **Group by `(FileSizeBytes, QuickHash)`** across the library. Two different files
   matching on size *and* three sampled regions is astronomically unlikely, so this
   grouping alone finds essentially all real duplicate candidates.
3. **ContentHash (expensive, computed rarely)** — only for files that already collide on
   step 2 (normally a tiny fraction of the library), compute a full-file hash to give a
   yes/no confirmation before anything is ever offered as "delete this duplicate." This
   keeps the guarantee (never wrongly flag two different files as duplicates) without
   paying the full-hash cost library-wide.

**A separate, manually-triggered feature — not part of Organize.** Duplicate detection
never runs automatically during import/organize; the user opens "Find Duplicates" (its
own nav entry) and runs it deliberately when they want to. Organize's own duplicate
handling (§4) is limited to *flagging*, never deleting.

**Review UI**: results are a list of duplicate groups, not a bare hash/filename report —
because a hash match the user can't visually verify is exactly how you'd accidentally
delete the wrong thing:
- Each group shows **actual thumbnails** of every file in it, side by side — so the user
  can visually confirm with their own eyes that the app matched the same shot and didn't
  mismatch two different photos, before anything is touched.
- Per group, a suggested "keep" is pre-selected (highest resolution / longest duration /
  original import folder), but the user picks — a simple checkbox/radio per item, one
  click to accept the suggestion, or override to keep a different copy (or more than
  one).
- Confirmed removals go to the **Recycle Bin (§5)** — never a permanent delete, and
  browsable there afterward with the same preview/metadata as anything else recycled.

*Out of scope for this feature, worth a separate later pass:* **near-duplicate**
detection (the same photo re-exported at a different resolution, a HEIC and its
converted JPEG, a screenshot of a screenshot) is a similarity problem, not an identity
one — it needs perceptual hashing (pHash/dHash) and a similarity threshold, not exact
byte matching. Reasonable to add in Phase 3 once exact-duplicate detection is solid.

[Phase 1 built shape: since a genuine (FileSizeBytes, QuickHash, ContentHash)
match means byte-identical files, resolution/duration can never actually
differ between members of a group — those tie-breakers only make sense for
Phase 3's near-duplicate detection. The "keep" suggestion here uses only the
one criterion that's meaningful for exact duplicates: whichever copy has the
earliest ImportedAt (§7's "original import folder", read literally). No
dedicated nav entry/page yet — it's a toggleable panel alongside the
Recycle Bin's, consistent with Phase 1's minimal web UI.]

## 8. Legacy Picasa import (opportunistic, one-time)

You already have `.picasa.ini` files scattered through your existing folder structure
from years of using Picasa — that's real, usable data, not just clutter to ignore:

- **`.picasa.ini`** (one per folder) stores, per photo: a `faces=` entry (one or more
  `rect64(x1,y1,x2,y2),contactId` pairs — a **face box plus a person reference** for
  photos Picasa already detected and the user already tagged), a `star=yes` flag, a
  `caption=` string, and — the part worth preserving pixel-for-pixel — **edit
  instructions**: `rotate=rotate(N)` (90°-increment rotation), `crop=rect64(...)` (crop
  rectangle, same normalized coordinate scheme as face boxes), and a `filters=` pipeline
  string recording the ordered list of non-destructive adjustments Picasa applied
  (brightness/contrast/fill-light/saturation/red-eye/B&W/sepia/etc. with their
  parameters). Google never published an official spec for this format — it's
  reverse-engineered from community documentation — so the parser targets the commonly
  seen keys and **skips anything it can't confidently parse rather than guessing**;
  losing an obscure filter's exact parameter beats silently corrupting a crop.
- **`contacts.xml`** (once per machine, historically under
  `%LocalAppData%\Google\Picasa2\contacts\`) maps each `contactId` to the **person's
  name** — this is what turns Picasa's face rectangles into actual named people.
- **`Picasa2Albums.ini`** records Picasa's own (cross-folder) album groupings and names,
  which map directly onto Curato's Tag/Album system (§9) — a Picasa album *is* a Tag.

Plan: when the import scanner finds `.picasa.ini` files under a configured library
root, offer an opt-in "Import Picasa data" step that:
1. Locates and parses `contacts.xml` once, building a `contactId → name` map.
2. Parses each `.picasa.ini`, converts `rect64(...)` (a coordinate normalized to a 64-unit
   grid) into pixel coordinates using the photo's real dimensions, and inserts `Person`
   (from named contacts) and `Face` rows (`Source = Picasa`, `Embedding = null` — Picasa
   never exposed a usable embedding, just the box + name).
3. Converts `rotate=`/`crop=`/`filters=` into `Edit` rows (`Source = Picasa`,
   `PipelineOrder` preserving Picasa's original sequence) — applied non-destructively at
   render time (§10), never by touching the original file, so there's no risk of getting
   a decades-old edit wrong and baking it in permanently.
4. Imports star ratings as the `Favorite` tag and Picasa albums as Curato tags/albums.

This means Phase 5's People feature launches **pre-populated** for anyone migrating off
Picasa: existing tagged faces show up immediately with names attached, and the local
ONNX pipeline only needs to run detection+embedding on (a) new imports going forward and
(b) faces Picasa never got to — rather than starting from zero.

[Phase 1 built shape: `filters=` (the brightness/contrast/fill-light/etc.
pipeline string) is deliberately not parsed at all — it's the least
documented key in an already-reverse-engineered format, and a wrong guess
there risks fabricating an edit that was never actually applied. Only
`rotate=`/`crop=`/`faces=`/`star=`/album membership are imported, all built
on the same `rect64`/`rotate(N)` primitives, which are well enough attested
to trust. `contacts.xml` and `Picasa2Albums.ini` are looked for anywhere
under the library root (their real, machine-local paths aren't reachable by
a self-hosted server) — if absent, faces still import with boxes intact and
`PersonId = null`. Runs automatically as part of every scan/upload (no
separate button to press) — idempotent and a no-op when no `.picasa.ini`
files exist, so there's no real cost to always checking. The standalone
`/api/picasa-import` action still exists underneath for a future settings
dialog to make this opt-out, but nothing in the UI calls it directly
anymore.]

## 9. Tags / Albums

A Tag *is* an Album — there's deliberately one mechanism, not two competing concepts.
Picasa's star was a single boolean; you wanted something richer ("kids", "nature",
whatever"), so:

- **Freeform, user-created tags**, applied to any `MediaItem` regardless of which
  Event/Trip/folder it physically lives in — a photo from a 2019 trip and one from
  last week can both carry "kids" and both show up when browsing that album.
- **Multiple tags per item.** Tagging UI: select one or more items (spanning multiple
  folders/events at once) → "Add to album…" with autocomplete against existing tags
  plus "create new."
- **Favorite is just a tag**, not a separate boolean column — specifically the one tag
  `IsFavoriteShortcut` marks, so a single keystroke (F) toggles it on a selection while
  it's still an ordinary, filterable, combinable tag under the hood. Consistent
  behavior instead of a special-cased favorites system living alongside a separate
  tagging system.
- **Browsing**: a nav entry lists every tag as a browsable album (photo count, cover
  thumbnail); opening one filters the grid to that tag's items, chronological by
  default with an optional manual drag-to-reorder per album (`MediaItemTag.
  ManualSortOrder`) — Picasa albums supported curated ordering, not just chronological.
- **Combines with everything else**: tag filters stack with Event/Trip/People/search
  filters rather than being a separate silo.
- **Picasa import**: `Picasa2Albums.ini` entries and star ratings (§8) land here
  directly — a Picasa album becomes a Tag with the same name and membership; no
  separate import path needed since it's the same concept end to end.

[Phase 2 built shape: browsing only, via an "Albums" panel — cover
thumbnail + item count per tag, opening one shows its members in the same
virtualized grid/Lightbox the main library view uses (a paired Live Photo
video is hidden the same way, §3). No tagging UI yet (no way to create a
tag or add/remove an item from one by hand) and no `ManualSortOrder`
drag-to-reorder — browsing what Picasa import already populated was the
immediate need; native tagging is later Phase 2/3 scope.]

## 10. Non-destructive editing

Basic edits (crop, rotate/straighten, brightness/contrast/saturation, fill light,
red-eye removal, black & white/sepia) — matching Picasa's own editing scope, and the
mechanism it used: **the original file's bytes are never modified.** An edit is just
`Edit` rows (§2) describing an ordered pipeline of operations, applied server-side at
render time (thumbnail generation, the image the fullscreen viewer requests) by the
image pipeline, never baked into the source file on disk. Reverting to the original is
always just "stop applying the pipeline" — nothing to undo at the file level, because
nothing at the file level ever changed.

- **Picasa-imported edits render immediately**, even before Curato has its own editing
  UI — parsed crop/rotate/filter pipelines (§8) go through the exact same render-time
  pipeline as edits made natively in Curato, so migrating off Picasa means seeing your
  old crops and rotations correctly from the moment the gallery viewer (§11) ships,
  independent of when native editing lands.
- **Exporting/downloading a copy** (for sharing, printing, uploading somewhere) is the
  one place the pipeline gets "baked in" — and even then, only into a **new** file
  streamed to the client; the original on the server is still never touched.
- Edit history is just the `Edit` pipeline for that item — no separate undo stack to
  maintain, and no risk of an editing bug ever corrupting the source photo/video.

### Red-eye removal: manual now, AI-assisted later

Manual (v1, this section): click on each red eye (or drag a small region over it) in
the viewer; that becomes a `RedEye` `Edit` row (§2, `Parameters` = the point/region(s)
corrected) — a correction layer applied at render time exactly like every other edit
here, never touching the source pixels, so it can be tweaked or removed later with the
original always intact. Picasa's own red-eye fixes import the same way (§8).

**AI-assisted detection, later**: once the local face-detection pipeline (§14) exists,
finding eyes within an already-detected face is a small, natural extension of work
that pipeline is already doing. Unlike the higher-stakes auto-suggestions elsewhere in
this plan (Duplicate Finder's review queue exists *because* its action is a deletion),
red-eye correction is just a non-destructive rendering layer — nothing is deleted,
nothing is even touched at the file level — so it doesn't need an upfront
approval/review queue to be safe:
- Detected red-eyes are **corrected automatically**, each becoming its own `RedEye`
  `Edit` row exactly as the manual flow above produces, applied at render time.
- Each auto-applied correction shows a **simple per-instance toggle** in the viewer (not
  a queue to work through) — off if the model misfired (called something red-eye that
  wasn't, or did a bad job of it), on otherwise. A group photo with several faces gets
  one toggle per detected eye/face, not one blanket toggle for the whole photo, since
  some corrections in the same shot might be good and others not.
- Toggling off just stops applying that one `Edit` row — instant, free to reconsider
  later, no queue, no approval step, because there's nothing at risk to approve.

Tracked as a §14 follow-on, not a separate model/phase of its own.

## 11. Web gallery/viewer UI (Picasa-like)

- Virtualized grid (e.g. React + TanStack Virtual, or the equivalent in whatever
  frontend framework is settled on) so scrolling stays smooth over tens of thousands of
  items — never request full-res images for off-screen tiles.
  [Also: the grid uses the full window width rather than a fixed max-width
  (photo galleries want the space); the scroll container is keyboard-
  focusable (tabIndex) since native Page Down/End/arrow-key scrolling only
  ever targets a focused element, not just whatever's under the mouse; a
  hovered tile scales up slightly (transform, no layout impact on
  neighbors) for a bit of tactile feedback. One easy-to-miss virtualization
  gotcha worth remembering if it resurfaces elsewhere: TanStack's
  measureElement reads an item's own border-box, which excludes margin —
  reserving gap-sized space between virtualized rows needs padding on the
  measured element, not margin, or the gap silently disappears once real
  measurements replace the initial estimate. A second, more stubborn
  gotcha in the same area: a native browser scrollbar's drag handle
  recomputes its mouse-to-scrollTop mapping from the *current* scrollHeight
  on every tick of an active drag, so even a 1px measureElement correction
  mid-drag is enough to make the handle visibly desync from the cursor —
  better estimates only shrink this, they don't eliminate it. The actual
  fix was to stop measuring at all wherever an estimate can be exact: since
  thumbnail-row height is already computed deterministically from the
  container's real measured width, `measureElement` isn't attached to
  those rows. Header rows were originally left on dynamic `measureElement`
  too, on the theory that a large library has "far too few of them for
  residual drift there to matter" — that held for a small/organized
  library but not for a ~2000-item unorganized one, which can have several
  hundred Event headers whose title text and "Will move to" hint line both
  wrap unpredictably at narrower column counts. Confirmed still reproducing
  after three separate fix attempts aimed at the thumbnail rows, which is
  what pointed at headers as the real remaining source: `measureElement`
  was removed from headers too, and their height estimates were made
  deliberately generous (safely cover a 2-line wrap of both the title and
  the hint) instead of tight, so `getTotalSize()` never needs correcting
  and is stable for the whole scroll session — at the cost of a bit of
  unused whitespace under a header whose real content is shorter than the
  now-generous estimate. A third, unrelated bug that looked similar from the
  outside (intermittently huge, pixelated thumbnails, "fixed" by a full
  page reload): the container-width measurement used to combine a
  synchronous `getBoundingClientRect` read with a `columnCount = 4`
  fallback while unmeasured. `getBoundingClientRect` returns the border-box,
  which — unlike `ResizeObserver`'s `contentRect` — does *not* exclude a
  scrollbar's width, so that initial read could be wrong by however wide
  the scrollbar is; if no further resize ever fired to correct it, the
  wrong value stuck for the component's whole lifetime (a real render race,
  not a deterministic calculation error, matching both the "sometimes" and
  the reload-fixes-it symptoms). Fixed by removing the guess entirely:
  width starts at `null` and the component renders an empty (but ref'd, so
  still measurable) scroll container until `ResizeObserver`'s own first
  callback — the one guaranteed to be correct — actually fires.]
- Server-side thumbnail pipeline: decode + downscale on the server, cache to disk
  (a regenerable cache, keyed by `ContentHash`, separate from the portable `.curato/`
  data per §2), served to any client over HTTP — so a thumbnail is generated once no
  matter how many different clients (a browser, the Android TV app) ask for it.
  [`ThumbnailCache` (Curato.Media) lives at `.curato/thumbnail-cache/` — deliberately
  *not* keyed by `ContentHash` as first planned, since that's lazily computed (§7) and
  forcing it on every first thumbnail request would defeat the point; keyed instead by a
  cheap composite (source file size+mtime, requested pixel size, and an Edit-set stamp)
  that invalidates on everything that actually changes rendered output, without hashing
  the whole file. No eviction policy yet — a replaced file's old entry just becomes
  orphaned dead weight. Also: SkiaSharp's plain decode ignores EXIF orientation entirely,
  handing back pixels exactly as the sensor stored them — for the very common case of a
  phone held in portrait (sensor data stored "sideways" plus a tag saying how to display
  it), that came out visibly rotated. `ExifOrientationCorrector` (Curato.Media) reads the
  tag via `SKCodec.EncodedOrigin` and applies the matching rotate/mirror before resizing,
  as the *last* pipeline step — after Edit application (§10), since Picasa-imported crop
  rectangles were computed against the file's raw, as-stored dimensions, and correcting
  orientation first would misalign them for a photo with both a Picasa crop and a rotated
  orientation tag. One more real bug this surfaced: `MediaFileScanner`'s recursive walk
  never excluded `.curato/` at all — the Recycle Bin's trashed originals and now the
  thumbnail cache both genuinely contain real `.jpg` files on disk, which without that
  exclusion would get indexed as new library photos on every re-scan.]
- Fullscreen/lightbox viewer: swipe/arrow-key/remote-control navigation, pinch-zoom or
  scroll-to-zoom for photos, native `<video>` playback for video with a generated
  poster frame as the grid thumbnail.
  [Phase 2 built shape: scroll-wheel and click-to-toggle zoom (1x-4x), with
  drag-to-pan once zoomed — pinch-zoom (touch) and a poster frame for
  videos aren't built yet. Zoom-toggle was first bound to double-click,
  which didn't match the `cursor: zoom-in` hint shown on hover (implying a
  single click); switched to a plain click, distinguished from a real pan
  drag by tracking whether the pointer moved more than a few pixels between
  mousedown and mouseup, so panning while already zoomed in doesn't also
  toggle zoom off on release. The caption shows TakenAt alongside the
  relative path and position, formatted in the viewer's own locale, and
  flags a `TakenAtSource.FileSystem` date (no Exif/video metadata date was
  found, so it's just the file's last-modified time) and a
  `TakenAtSource.ExifModifyDate` one (Exif's DateTimeOriginal/Digitized were
  both missing but IFD0's plain "last modified" tag survived — a weaker but
  still device-sourced signal) with a hover-hint each — added so "N items
  land on today" reports can be self-diagnosed as a real missing-metadata
  case versus a scanner bug, rather than guessed at. Confirmed live: an
  iPhone photo downloaded from iCloud whose Exif block had nothing but a
  bare version tag (0221) — DateTimeOriginal/Digitized/GPS all stripped by
  whatever sharing/transfer path it went through, which no automatic
  fallback can recover since the data is simply gone from the file. For
  that class of file, a manual override is the only remaining option: a
  pencil button next to the caption opens a `datetime-local` input, saving
  via `POST /api/media-items/taken-at` (`TakenAtSource.Manual`) and
  re-sorting the item's Event membership + StartAt/EndAt the same way a
  newly-scanned item is folded in (`LibraryRepository.ResortEventMembership`,
  shared by both paths now). Deliberately does not re-bucket the item into a
  different Event/day even if the correction crosses a day boundary — that
  needs the full clustering pass, out of scope for a single-field fix. This
  same stripped-Exif finding is also what drove §4a's direct iCloud
  integration — `icloudpd`'s `--set-exif-datetime` fixes it at the source
  for newly-synced files, so the manual editor here is the fallback for
  whatever still slips through (e.g. files that already made it in another
  way) rather than the primary fix.]
- Grouping-aware navigation: browse by Event/Trip, or an "all photos" timeline view.
- Fast-scroll date scrubber (like Photos/Picasa's right-edge date rail).
  [Phase 2 built shape: Trips and standalone Events are merged into one true
  chronological timeline client-side (they arrive from the API as two
  separate pre-sorted arrays) — fixed alongside this, since a scrubber only
  means anything over a genuinely time-ordered list, and trips themselves
  weren't even being sorted before now. The rail shows a tick per calendar
  year and jumps via the virtualizer's own scrollToIndex on click/drag;
  ticks are spaced evenly by count, not by each month's actual rendered
  height, which varies with how many photos it has.]
- **Light + dark theme, both first-class**, following the OS/browser preference
  (`prefers-color-scheme`) by default with an in-app override — the near-black
  fullscreen-viewer chrome stays dark in *both* themes, since a light lightbox frame
  around photos looks wrong regardless of the surrounding UI's theme.
- **Authentication** (new, since this is now a network-exposed service, not a desktop
  app inheriting OS login as its access control) — see §15.

## 12. Map view

Reverse-geocode each `MediaItem`'s GPS EXIF onto an interactive map so you can see
*where* a trip happened, not just when — clustering nearby pins as you zoom out, and
clicking a pin/cluster filters the grid to those photos. Two-way with Event/Trip
browsing: open a Trip and see its photos plotted along wherever they were taken;
open the map and jump into the Event a cluster belongs to. Photos without GPS data
can be placed manually by dragging them onto the map. (This is naturally web-friendly
technology, if anything easier now than as a native desktop control.)

**Worth flagging as a deliberate exception** to the local-only posture the rest of the
plan holds to (§14's face recognition, no cloud calls): map tiles have to come from
*somewhere* — there's no good fully-offline default tile set. Two consequences to
design in from the start rather than discover later:
1. The GPS coordinates themselves never leave the server except as anonymous map-tile
   image requests (e.g. to OpenStreetMap-based tiles), the same way any map-embedding
   web page works. Worth stating this distinction plainly in the UI/README next to the
   "fully local" face-recognition claim, so the one network-dependent feature is
   clearly scoped and not mistaken for a broader walk-back of the local-first promise.
2. Should degrade gracefully offline (cached tiles for previously-viewed areas, a plain
   "map needs internet" state otherwise) rather than erroring.
- Turning coordinates into a place name (`"Paris, France"`) for captions/titles needs
  either an online reverse-geocoding call or a bundled offline dataset — left as an open
  question (§19) to settle nearer Phase 4. The same reverse-geocoding this section needs
  for the map itself is what powers location-aware Trip auto-naming (§3, Step 3) — one
  capability, two uses, both landing together in Phase 4.

## 13. Slideshow

Fullscreen playback of whatever's currently in view — an Event, a Trip, a Tag/Album, or
a search result set — reusing the fullscreen viewer's rendering rather than a separate
code path. This is also the feature the Android TV client (§16) cares about most:
- Configurable transition (fade/slide/none) and per-slide duration, auto-advance with
  pause/resume, optional loop.
- Optional background music from a **user-selected local audio file** — Curato never
  bundles music (licensing), and never needs network access for this either.
- A video encountered mid-slideshow just plays inline (with its own audio) before
  auto-advancing.
- Keyboard/remote-control control (space/select to pause, arrows/D-pad to skip)
  consistent across the web UI and the TV client.

## 14. Local face recognition (phase 5 — architected now, built later)

Kept out of v1 scope, but the data model (`Person`/`Face` tables with an `Embedding`
BLOB) and a pluggable "enrichment pipeline" hook in `Curato.Media` exist from the start,
so adding this later doesn't require a schema migration on people's real libraries.

When built:
- **Detection**: a small ONNX face-detection model (e.g. YuNet/RetinaFace) run
  server-side per photo (and per sampled video frame) via ONNX Runtime — no cloud calls,
  but see the hardware note below. Skips photos that already have a `Source = Picasa`
  face box (see §8, Legacy Picasa import) unless the user asks for a re-scan.
- **Embedding**: an ArcFace-style embedding model producing a fixed-length vector per
  face, stored in `Face.Embedding` — including backfilling embeddings for
  Picasa-imported faces so they participate in auto-matching too.
- **Known vs. Unknown, and a confirm-don't-assume workflow** (matching how Picasa's own
  face tagging worked, and got better over time):
  - The People view is split into **Known** (named `Person`s) and **Unknown**
    (detected `Face` rows with no `PersonId` yet).
  - A new detection that's a close embedding match to an existing named Person is
    **suggested, never auto-assigned** — "Is this {Name}?" with a Yes/No, surfaced as a
    user-paced review inbox (work through a handful whenever you want, not a blocking
    prompt per photo — same spirit as Picasa's own batch-labeling flow).
  - **No** on a suggestion offers two corrections: pick a *different* existing known
    Person (the model matched the wrong one), or **create a new Person** from this
    face. Leaving it unconfirmed is always fine too — it just stays in Unknown for
    later.
  - Faces with no confident match to anyone land straight in Unknown, grouped by
    unsupervised clustering (DBSCAN/agglomerative) into "these look like the same
    unnamed person" proposals — the same mechanism as before, just now explicitly the
    *fallback* path for when there's no known-person suggestion to confirm.
  - **Gets stronger with every confirmation**: matching a new detection isn't done
    against one static reference face per person — it's matched against **every
    confirmed `Face` for that `Person`** (`Person.RepresentativeFaceId` is just which
    one shows as their avatar in the UI, not the sole matching reference). Each
    Yes/correction adds another confirmed embedding to that Person's reference set, so
    accuracy compounds over time exactly the way it did in Picasa — more confirmed
    photos of someone means more angles/lighting/ages represented, and fewer future
    misses or wrong suggestions for them.
  - Picasa-imported names (§8) start with zero confirmations needed — they're already
    named — but immediately participate in this same growing-reference-set matching
    for anything new.
  - **Ignore this person**: for strangers/bystanders that don't belong to anyone
    worth tracking — easy to catch in a public shot — reviewing an Unknown face offers
    "Ignore" alongside naming it, marking that `Face.Ignored = true` rather than
    forcing a name onto someone the user has no interest in identifying.
    - Ignoring pulls it out of the review queue for good, and out of the clustering
      pass, so it stops resurfacing.
    - Since clustering already groups visually-similar unlabeled faces, ignoring one
      face from a cluster offers to **ignore the whole cluster at once** — if the same
      passerby shows up in several shots from one event, that's one action, not one
      per photo.
    - **The same growing-reference-set matching applies to Ignored**, just for
      suppression instead of naming: a new detection is checked against every
      previously-ignored `Face` (nearest-neighbor, not one blurred average, since
      different ignored strangers are different people and shouldn't be blended into
      one identity) as well as against Known persons — a close match to something
      already ignored gets auto-marked `Ignored` too, quietly, instead of prompting
      again about the same recurring stranger.
    - **Reversible**: a de-emphasized "Ignored" section in the People view lets the
      user browse and un-ignore anything, in case a face turns out to be someone worth
      naming after all — nothing about "Ignore" is a one-way door.
- **UI**: the People album view above, drag-to-merge/split clusters in Unknown, and
  person filters combinable with Event/Trip filters.
- All processing is opt-in and fully local (to the server) — call this out explicitly
  in the UI/README since it's a meaningful trust commitment for a personal-photo app.
- **Follow-on**: AI-assisted red-eye detection (§10) reuses this same detection pipeline
  once it exists, rather than needing its own model.

### CPU-only viability, checked rather than assumed

Previously this ran on the user's own Windows PC, likely with a real GPU. Now it runs
wherever the server runs — typically a Synology NAS with a weak CPU and no GPU — so
it's worth actually checking whether that's viable rather than assuming it isn't:

- Lightweight ONNX face detectors built for exactly this (e.g. **YuNet**, ~76K
  parameters) run at **~1.6ms/frame at 320×320 on a fast desktop CPU**
  ([benchmark](https://www.researchgate.net/publication/370122920_YuNet_A_Tiny_Millisecond-level_Face_Detector)).
  Even a NAS-class Celeron running this 10-20x slower is still tens of milliseconds —
  **the detection network itself is not the bottleneck on any CPU built in the last
  decade.**
- The real cost is **decoding the source photo** (a 4000×3000+ phone photo, or a RAW
  file) before the network sees it — but that decode already happens for thumbnail
  generation (§11), so face detection adds one more cheap pass over an image already
  being decoded, not a new expensive step.
- Rough estimate for a weak NAS CPU, all-in (decode + detect + embed): **~200-500ms per
  photo**. For a 50,000-photo backlog, that's a few hours of background work — real, but
  entirely reasonable as a one-time job on a box that's on 24/7, not something that
  needs to finish instantly or blocks normal use.
- What this means for the design: the compact CPU-tier model stays the right *default*
  for NAS deployments (unchanged from before), but the **backlog scan needs to be a
  proper background job** — resumable, pausable, throttled so it doesn't starve the
  NAS's other duties, with visible progress — not an assumption that it completes
  quickly. Very low-end ARM NAS units (no AVX2, weak single-core performance) will be
  slower still; the hardware probe should flag that honestly rather than silently
  grinding for days. Running the server on something beefier (a Windows PC, or a NAS
  with a real GPU/OpenVINO-capable Intel iGPU) remains the honest answer for anyone who
  wants this fast — not solved further here, just no longer an assumption.

### Model delivery: download-on-demand, hardware-matched (not bundled)

The server ships with **no model weights at all** — ONNX face-detection/embedding
models (and any future local-AI models) are downloaded on demand once the server is
running, like an on-device model manager (à la Bionic/LM Studio/Ollama-style "pick a
model, download it" flows), rather than bloating every server image/update with weights
most deployments don't need yet.

1. **Hardware probe on first use of the People feature** — now needs a per-platform
   implementation rather than the Windows-specific DXGI/D3D12 approach originally
   sketched: on Linux, check for an NVIDIA GPU (CUDA availability) or fall back to CPU;
   on Windows, DirectML is available on any DX12 GPU; CPU instruction-set support
   (AVX2/AVX-512) matters for the quantized CPU-model tier everywhere. Realistically,
   CPU-only is the common case for a NAS-hosted server (see above).
2. **A small model catalog/manifest** (JSON, fetched from the same GitHub
   Releases/repo — versioned independently of the server) lists each available model
   variant with its size, required execution provider, minimum VRAM, and
   accuracy/speed tradeoff.
3. **Only list what the machine can actually run well** — filter the catalog against
   the hardware probe so the download picker never shows, say, a CUDA-only build to a
   machine with no NVIDIA GPU, or a model needing 6 GB VRAM to a card that doesn't have
   it; recommend one sensible default per capability tier.
4. Downloads are verified by checksum, cached in the server's regenerable data volume,
   and re-used across server updates — "checked, cached, verified, never silently
   overwritten," the same posture as everything else in the plan.
5. Model licensing matters for a distributed app: only ship a catalog entry for
   permissively-licensed pretrained weights (e.g. YuNet/RetinaFace/ArcFace-family
   models with licenses that allow redistribution of the *weights themselves*, since
   here Curato is redistributing a download link/mirror, not just code) — worth an
   explicit license check per catalog entry before it's added, not assumed.

## 15. Authentication & security (internet-facing is a supported deployment, not an edge case)

New section, since the server is now a network-exposed service rather than a desktop
app that inherited Windows' own login as its access control — and per your requirement,
"point it at the internet with an open port" needs to be genuinely safe, not just
technically possible. That's a materially higher bar than LAN-only, so it's treated as
a real deployment mode with its own requirements, not an afterthought:

- **Single admin account, set on first run** — a username/password created the first
  time the server starts, matching the personal/family scale this whole plan targets;
  multi-user accounts (separate logins/permissions per family member) would be a
  natural later extension if it's ever actually wanted, not designed further here since
  it hasn't been asked for.
- **Passwords hashed with a well-vetted adaptive algorithm** (ASP.NET Core Identity's
  default password hasher — PBKDF2 with a proper work factor, or Argon2 if preferred) —
  never a custom scheme, never a fast general-purpose hash like plain SHA-256.
- **Session-based auth** (ASP.NET Core's built-in cookie auth: `HttpOnly`,
  `SameAsRequest`-secure, `SameSite=Strict` cookies) for the web UI; the Android TV
  client (§16) authenticates the same way and holds a long-lived session/token, since
  re-entering a password with a remote control every time is a bad experience.
  **Not yet implemented**: sessions being individually revocable ("log out all
  devices," or real protection if a device/cookie is stolen) needs a server-side ticket
  store checked on every request (ASP.NET Core's `OnValidatePrincipal` hook) — what
  exists today is the simpler self-contained encrypted cookie, where logout only tells
  *that* browser to delete its own cookie rather than revoking the ticket server-side.
  Tracked as a gap, not silently assumed solved.
- **Login brute-force protection**: rate limiting on the login/setup endpoints
  (ASP.NET Core's built-in `Microsoft.AspNetCore.RateLimiting` middleware — no extra
  dependency, 5/minute). Escalating lockout beyond the rate limit isn't added yet.
  An internet-exposed login is a real target for credential stuffing in a way a
  LAN-only one never was; this is not optional once a port is open to the internet.
- **Implemented so far**: single-admin-account setup/login/logout (`AuthController`),
  password hashing via `PasswordHasher`, a global fallback authorization policy so
  every controller requires a session by default unless explicitly marked
  `[AllowAnonymous]` (only the auth endpoints and `/health` are). Server-wide config
  (currently just this one account) lives in its own `server.db`, separate from any
  library's `.curato/library.db` (§2), in the server's persistent data directory.
- **HTTPS is mandatory for internet exposure, not "the deployer's job, take it or leave
  it."** Recommended path: run behind a reverse proxy that already does TLS well
  (Synology DSM's own built-in reverse proxy + Let's Encrypt integration for the
  primary NAS-hosted case; Caddy/Traefik for a Windows- or Linux-hosted server) rather
  than Curato managing certificates itself — but this is a hard requirement for
  internet-facing setups, spelled out clearly in setup docs, not a "nice to have."
  Skipping HTTPS is only reasonable on a genuinely trusted, non-exposed home LAN.
  [Phase 2: `Program.cs` now wires up ASP.NET Core's Forwarded Headers
  middleware (`X-Forwarded-Proto`/`X-Forwarded-For`), a gap previously
  flagged but left until an actual reverse-proxy deployment was being set
  up — without it, `Request.IsHttps` would always read false behind a
  TLS-terminating proxy (since Curato.Api itself only ever receives plain
  HTTP from that hop), silently defeating the auth cookie's `SameAsRequest`
  Secure policy below. `KnownIPNetworks`/`KnownProxies` are cleared to trust
  the header from whatever sent it, rather than pinned to one proxy IP —
  safe only because Curato.Api's own port must never be reachable except
  through that trusted reverse proxy; never publish it directly.]
- **Every API endpoint requires a valid session**, not just the web UI's shell — the
  only unauthenticated routes are login itself and a minimal health check. This matters
  more once the server is internet-reachable: there's no LAN perimeter doing any of the
  access control for you anymore.
- **Path/file-handling hardening on upload and every filesystem-touching endpoint**:
  rigorous validation against path traversal (a crafted filename or path component
  trying to write/read outside the intended library root) and sensible file-type/size
  limits on uploads (§4a) — a classic vulnerability class for any file-serving app, and
  one that matters a lot more once the endpoint is reachable by anyone, not just people
  already on the home network.
- **Audit logging for login attempts and admin actions** (successful and failed),
  visible in the UI — the same "make failures visible, never silent" principle already
  applied to Auto Backup (§6): a self-hoster exposing a port to the internet should be
  able to notice if their box is being probed or attacked, not find out later.
- **A VPN/tunnel (WireGuard, Tailscale) is the safer alternative worth documenting
  alongside direct port-forwarding** — it means the login surface itself is never
  reachable by the open internet at all, only through an already-authenticated tunnel.
  Not a replacement for the hardening above (a compromised or lost device on the tunnel
  is still a real scenario), but worth presenting as the lower-risk option for anyone
  who doesn't specifically need a bare open port.
- **Keeping the server patched matters more now**: the Docker image update flow (§18,
  "pull the new tag") already exists for other reasons, but for an internet-facing
  deployment, staying current is a real security practice, not just a way to get new
  features — worth saying so explicitly in setup docs.

## 16. Android TV client

A native app (Nvidia Shield and similar Android TV devices are the concrete target),
since a browser tab is the wrong UX for remote-control/10-foot navigation — the same
reason Plex itself ships a native Android TV app instead of pointing the TV at its web
UI. Talks to a `Curato.Api` server exactly like the web frontend does (§11), just with a
TV-shaped UI: a media grid and slideshow (§13) are the primary use cases, browsing by
Event/Trip/Tag/People the same as on the web.

**Two deployment modes, in order of how proven they are:**

1. **Thin client to a separately-hosted server (near-term, low-risk)** — the TV app
   just talks to whatever `Curato.Api` instance is already running on the NAS or a
   Windows PC (§1). This alone already satisfies "browse and slideshow the curated
   library on the TV," and is exactly as proven as the Windows/Linux server itself,
   since the TV app is just another API client.
2. **Standalone mode: the server embedded in the TV app itself (stretch goal, real
   risk, validate before committing)** — the ambition is for the Shield to optionally
   *be* the server too (its own storage or an attached USB drive, no NAS or PC
   required), by running `Curato.Api` in-process inside the Android app. This is
   **genuinely higher-risk** and shouldn't be assumed to "just work" the way the
   Windows/Linux server does:
   - Android's background-execution limits (Doze mode, restricted always-on services)
     make a normal Android app hosting a persistent web server on a LAN port a less
     standard pattern than on a desktop OS or Linux container — Shield's specific
     Android TV variant is comparatively open (sideloading, Kodi, Plex client, etc. all
     work fine there), but this still needs a real spike/prototype to validate before
     it's treated as a committed feature, not assumed from precedent.
   - Native library packaging (SQLite, FFmpeg) needs Android-specific builds/ABIs,
     solvable but an extra packaging surface beyond the Windows/Linux targets.
   - Client-app technology choice for the TV app itself (native Kotlin + Jetpack
     Compose for TV, vs. .NET MAUI for code-sharing with the rest of the .NET codebase)
     is an open question — Compose for TV is the more mature, better-trodden path
     specifically for TV-shaped UI (D-pad focus handling, leanback-style navigation);
     .NET MAUI would let the *server* embed more naturally (same .NET assembly, no
     separate host) but is less proven for genuinely TV-optimized UI. Worth deciding
     with a small prototype of each rather than committing up front.

**Sequencing**: ship mode 1 first (thin client, Phase 6) — it alone delivers everything
asked for (browse + slideshow the curated library on the TV) without betting on the
riskier standalone mode. Treat mode 2 as a follow-on investigation once the core
server/web experience is solid, not a blocker for the TV app's first release.

## 17. Windows Importer Helper (optional convenience client)

Not a full gallery app — the web UI is that now — but a small, optional native Windows
background utility that makes getting new photos in more convenient than a manual
browser upload every time (§4a):
- Detects camera/SD-card/phone (MTP) insertion and uploads new files to the configured
  `Curato.Api` server automatically, in the background, with resume-on-reconnect for
  large batches (a full SD card of RAW+video files is a worse fit for a plain browser
  upload than a purpose-built background transfer).
- Lives in the system tray; not required for Curato to work at all — plain browser
  upload (§4a) remains the baseline, always-available path.
- Small enough in scope to still use Velopack (§18) for its own self-update, same as
  the Windows-hosted server package.

## 18. Release engineering: CI, packaging, auto-update

Modeled on the sibling project MediaCurator's pipeline (GitHub Actions → GitHub
Releases, tag-triggered publish), adapted for a server-first, mostly-cross-platform
app rather than a single Windows desktop installer:

- **CI** (`.github/workflows/build.yml`): one workflow.
  - `push` to `main`/`develop` and `pull_request` to `main` → `dotnet build` + run
    `Curato.Tests`, plus building `Curato.Web`'s frontend.
  - Push of a `v*` tag → a `release` job that:
    - Builds and pushes a **multi-arch Docker image** (`linux/amd64` + `linux/arm64`)
      containing `Curato.Api` + the built `Curato.Web` static files, tagged `vX.Y.Z`
      and `latest`, to GitHub Container Registry (`ghcr.io`) — this is the primary
      artifact, covering Synology/Linux deployment. No custom updater needed for this
      target: updating is "pull the new image tag," the same as any other self-hosted
      Docker app (optionally automated with something like Watchtower, which is the
      user's/deployer's choice, not something Curato builds).
    - **Paused for now**: packaging the Windows-hosted server and the Windows Importer
      Helper (§17) with Velopack, publishing them as GitHub Release assets via
      `softprops/action-gh-release@v2` — not part of CI until the Windows server is
      picked back up (§1). The Docker image above is the only release artifact for now.
  - Same guard MediaCurator uses to avoid a redundant build right before tagging: skip
    the `main`-branch build when the commit message starts with `Release `.
- **Versioning: manual, single source of truth**, same discipline as MediaCurator: bump
  a central version, commit as `Release X.Y.Z`, push, then
  `git tag vX.Y.Z && git push origin vX.Y.Z` to trigger the release job — the same tag
  drives the Docker image tag and the Windows packages' `<Version>`, so everything
  released together stays in lockstep. A `docs/RELEASING.md` runbook (mirroring
  MediaCurator's) documents the exact steps, written alongside the Phase 0 scaffold.
  One-time repo setting to remember (MediaCurator's runbook calls it out explicitly):
  **Settings → Actions → General → Workflow permissions → "Read and write
  permissions"**, or the release job's Release/package pushes fail on permissions.
- Added to **Phase 0** scope: the workflow file, Dockerfile, Velopack wiring for the two
  Windows packages, and `RELEASING.md` land together with the initial solution
  scaffold, since CI needs a buildable solution to do anything.

## 19. Open questions to settle before Phase 0 coding starts

- Map view (§12): which tile/reverse-geocoding provider, and what the offline fallback
  looks like — needs a decision nearer Phase 4, not blocking anything before it.
- Android TV client technology (§16): native Kotlin/Compose-for-TV vs. .NET MAUI —
  affects whether "embed the server in the TV app" (mode 2) is realistic at all;
  worth a small prototype of each before Phase 6, not before Phase 0.

## 20. Roadmap / phases

1. **Phase 0 — Scaffold**: `Curato.Api` (ASP.NET Core) + `Curato.Web` (SPA) projects,
   SQLite schema + migrations, settings model, auth + the internet-facing security
   baseline (§15 — hashing, rate limiting, session hardening, path-traversal/upload
   validation; this lands in Phase 0 rather than later since bolting security onto an
   already-built API is a worse plan than building the API session-gated from the
   start), Dockerfile + GitHub Actions CI (Docker/Synology target only — Windows
   packaging is paused, §1) + `RELEASING.md` (§18).
2. **Phase 1 — Ingest & organize engine**: metadata extraction, QuickHash/duplicate
   detection, structure-aware import (existing folders as atomic units vs. gap-based
   clustering for loose/flat input, §3 Step 0), Event/TripGroup clustering, naming
   templates, dry-run preview, copy engine with undo, browser-upload ingestion (§4a),
   and the Recycle Bin (§5). Includes the opt-in Legacy Picasa import step, since it
   plugs into the same import/scan pass.
3. **Phase 2 — Web gallery viewer**: virtualized grid, thumbnail cache, fullscreen
   photo/video viewer, browse-by-Event/Trip, Tags/Albums browsing and tagging (§9),
   and rendering imported non-destructive edits (§10) so Picasa crops/rotations show up
   correctly from day one.
4. **Phase 3 — Polish**: rename-in-place syncing folder↔DB, reconcile-on-drift,
   "Restructure to Curato format" (§3) for Adopted folders, search/filter,
   near-duplicate (perceptual-hash) detection, Auto Backup (§6), the Windows Importer
   Helper (§17), performance passes on very large libraries (100k+ items), native
   non-destructive editing UI (§10) for edits made in Curato itself.
5. **Phase 4 — Map & Slideshow**: geotagging/map view, location-aware Trip auto-naming
   (§3 Step 3, shares the map's reverse-geocoding), and the slideshow feature (§12–13).
6. **Phase 5 — Local face recognition**: hardware-matched model download manager,
   detection/embedding pipeline, clustering, People album, incremental re-scan on new
   imports — pre-seeded with anything already imported from Picasa.
7. **Phase 6 — Android TV client**: native thin client to an existing server (§16,
   mode 1) — browse + slideshow on the TV. The standalone "server embedded in the TV
   app" mode is a follow-on investigation, not part of this phase.
