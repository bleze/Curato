export interface MediaItemDto {
  id: number
  relativePath: string
  kind: 'Photo' | 'Video'
  takenAt: string
  /** Where takenAt actually came from — lets the UI flag a date that's just the file's filesystem timestamp rather than a real Exif/video tag. */
  takenAtSource: 'ExifDateTaken' | 'ExifModifyDate' | 'VideoMetadata' | 'FileSystem' | 'Manual'
  /** Set when this is the still frame of an Apple Live Photo — the paired .MOV's relative path. */
  liveVideoRelativePath: string | null
  /** Reverse-geocoded from this item's own GPS Exif, if it has any — null for most video and any photo with no GPS tag. */
  location: string | null
  /** At most one at a time — see TagRepository.SetMediaItemFavoriteCategory. Null when no favorite category (or Picasa star) is set. */
  favoriteCategoryId: number | null
  /** True when this item's date sits far outside the rest of its own Event's dates — the "camera clock wasn't set" symptom (EventDateOutlierDetector). Only ever set within an Event context; absent (not just false) for a bare item list like Albums/Tags, which has no Event range to compare against. */
  isDateOutlier?: boolean
}

export interface EventDto {
  id: number
  title: string
  namingMode: 'Adopted' | 'CuratoManaged'
  folderPath: string | null
  /** What Organize would move this to, if it hasn't happened yet (dry-run preview). */
  proposedFolderPath: string | null
  startAt: string
  endAt: string
  /** Reverse-geocoded from the average GPS position of whichever members have coordinates — null if none do. */
  location: string | null
  /** Non-null once this Event has been grouped into a Trip (auto or manual) — lets the UI disable re-grouping it. */
  tripGroupId: number | null
  /** True for a folder matching the standard ".Before" pre-digital-scans naming convention, or for having at least one EventDateOutlierDetector-flagged member. Just a nudge (a badge), never anything auto-changed. */
  needsDateReview: boolean
  /** Best-effort date parsed straight out of the folder's own name (e.g. "2023-06-29 Title") — see FolderTitleParser.Parse. Null when the name doesn't start with one. Preferred over library.earliestReliableTakenAt for the "Fix dates…" prefill, since it's specific to this folder rather than a library-wide guess. */
  folderDate: string | null
  items: MediaItemDto[]
}

export interface TripGroupDto {
  id: number
  title: string
  namingMode: 'Adopted' | 'CuratoManaged'
  folderPath: string | null
  proposedFolderPath: string | null
  startDate: string
  endDate: string
  location: string | null
  needsDateReview: boolean
  folderDate: string | null
  events: EventDto[]
}

export interface LibraryDto {
  trips: TripGroupDto[]
  standaloneEvents: EventDto[]
  /** Earliest TakenAt among reliably-sourced (Exif/video-metadata) items anywhere in the library — used to prefill "Fix dates…" for a folder needing date review. Null if the library has none. */
  earliestReliableTakenAt: string | null
}

export interface ScanResult {
  mediaItemCount: number
  newAutoOrganizedEventCount: number
  newAdoptedEventCount: number
  newTripGroupCount: number
  itemsAddedToExistingEvents: number
  /** Leftover fictitious "2019"-style bare-year Trips from before that heuristic was fixed, ungrouped this scan. */
  removedBareYearTripGroupCount: number
  /** Events left with zero items (e.g. from before RecycleBin deletes started cleaning them up), removed this scan. */
  removedEmptyEventCount: number
  /** Previously-indexed items whose file was deleted on disk outside Curato, cleaned up this scan. */
  removedMissingFileCount: number
  /** Videos indexed before the wrong-epoch encoder bug fix existed (a movie-header date decades too early), self-healed this scan. */
  repairedVideoTakenAtCount: number
}

export interface AuthStatus {
  needsSetup: boolean
  isAuthenticated: boolean
  username: string | null
}

async function asJson<T>(response: Response): Promise<T> {
  if (!response.ok) {
    const text = await response.text()
    throw new Error(text || `${response.status} ${response.statusText}`)
  }
  return response.json() as Promise<T>
}

async function expectOk(response: Response): Promise<void> {
  if (!response.ok) {
    const text = await response.text()
    throw new Error(text || `${response.status} ${response.statusText}`)
  }
}

export function getAuthStatus(): Promise<AuthStatus> {
  return fetch('/api/auth/status').then(asJson<AuthStatus>)
}

export function setupAccount(username: string, password: string, rememberMe: boolean): Promise<void> {
  return fetch('/api/auth/setup', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ username, password, rememberMe }),
  }).then(expectOk)
}

export function login(username: string, password: string, rememberMe: boolean): Promise<void> {
  return fetch('/api/auth/login', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ username, password, rememberMe }),
  }).then(expectOk)
}

export function logout(): Promise<void> {
  return fetch('/api/auth/logout', { method: 'POST' }).then(expectOk)
}

export function getLibraryFolder(): Promise<string | null> {
  return fetch('/api/settings/library-folder')
    .then(asJson<{ path: string | null }>)
    .then((r) => r.path)
}

export function setLibraryFolder(path: string): Promise<void> {
  return fetch('/api/settings/library-folder', {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ path }),
  }).then(expectOk)
}

export interface BrowseFolderEntry {
  name: string
  path: string
}

export interface BrowseFoldersResponse {
  path: string | null
  parentPath: string | null
  folders: BrowseFolderEntry[]
}

export function browseFolders(path?: string): Promise<BrowseFoldersResponse> {
  const query = path ? `?path=${encodeURIComponent(path)}` : ''
  return fetch(`/api/settings/browse-folders${query}`).then(asJson<BrowseFoldersResponse>)
}

/** A custom label for "home" (e.g. "Kirke Hyllinge") shown instead of a real reverse-geocode for anything nearby — see GpsTripGate.HomeOverrideLabel. */
export function getHomeLocation(): Promise<string | null> {
  return fetch('/api/settings/home-location')
    .then(asJson<{ label: string | null }>)
    .then((r) => r.label)
}

/** Pass null/empty to clear the override and go back to plain reverse-geocoding everywhere. */
export function setHomeLocation(label: string | null): Promise<void> {
  return fetch('/api/settings/home-location', {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ label }),
  }).then(expectOk)
}

export interface iCloudSyncStatus {
  libraryFolderConfigured: boolean
  connected: boolean
  appleUsername: string | null
  intervalMinutes: number | null
  enabled: boolean | null
  lookbackDays: number | null
  /** A fixed calendar date (ISO "yyyy-MM-dd"), taking precedence over lookbackDays when set. */
  sinceDate: string | null
  recentItemCap: number | null
  lastSyncAt: string | null
  lastSyncStatus: string | null
  lastSyncMessage: string | null
  lastSyncItemCount: number | null
  /** The most recent line icloudpd printed, live, while a sync is running — null once it's done. */
  currentActivity: string | null
}

export interface iCloudSyncNowResult {
  success: boolean
  errorMessage: string | null
  itemsDownloaded: number
  newEventCount: number
}

export function getICloudSyncStatus(): Promise<iCloudSyncStatus> {
  return fetch('/api/icloud-sync/status').then(asJson<iCloudSyncStatus>)
}

export function iCloudLogin(username: string, password: string): Promise<{ requiresTwoFactor: boolean; error: string | null }> {
  return fetch('/api/icloud-sync/login', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ username, password }),
  }).then(asJson<{ requiresTwoFactor: boolean; error: string | null }>)
}

export function iCloudVerifyTwoFactor(username: string, code: string): Promise<void> {
  return fetch('/api/icloud-sync/verify-2fa', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ username, code }),
  }).then(expectOk)
}

/** Backs out of a pending login (e.g. leaving the 2FA screen) without completing it. */
export function iCloudCancelLogin(): Promise<void> {
  return fetch('/api/icloud-sync/cancel-login', { method: 'POST' }).then(expectOk)
}

export function iCloudSyncNow(): Promise<iCloudSyncNowResult> {
  return fetch('/api/icloud-sync/sync-now', { method: 'POST' }).then(asJson<iCloudSyncNowResult>)
}

export function iCloudUpdateSettings(
  intervalMinutes: number,
  enabled: boolean,
  lookbackDays: number,
  sinceDate: string | null,
  recentItemCap: number,
): Promise<void> {
  return fetch('/api/icloud-sync/settings', {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ intervalMinutes, enabled, lookbackDays, sinceDate, recentItemCap }),
  }).then(expectOk)
}

export function iCloudDisconnect(): Promise<void> {
  return fetch('/api/icloud-sync/disconnect', { method: 'POST' }).then(expectOk)
}

export interface ScanApiResponse {
  scan: ScanResult
  picasaImport: PicasaImportResult
}

// `signal` lets Cancel abort the client's wait for a response — the scan
// itself has no server-side cancellation support (ScanService.Scan is a
// single synchronous walk with nothing to check a token against), so an
// aborted scan keeps running to completion on the server; Cancel only stops
// this tab from waiting on it. Safe either way since re-scanning is
// idempotent (ScanService's own docs).
export function scanLibrary(path: string, signal?: AbortSignal): Promise<ScanApiResponse> {
  return fetch('/api/scan', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ path }),
    signal,
  }).then(asJson<ScanApiResponse>)
}

export function getLibrary(path: string): Promise<LibraryDto> {
  return fetch(`/api/library?path=${encodeURIComponent(path)}`).then(asJson<LibraryDto>)
}

/** Polled while a scan is in flight — see ScanController.GetProgress for why this needs its own endpoint rather than the scan request itself reporting progress. */
export function getScanProgress(path: string): Promise<{ filesFoundSoFar: number }> {
  return fetch(`/api/scan/progress?path=${encodeURIComponent(path)}`).then(asJson<{ filesFoundSoFar: number }>)
}

export interface OrganizeResult {
  eventsOrganized: number
  filesMoved: number
  /** Skipped rather than crashing the whole Organize run — something changed the file outside Curato since the last Scan. Re-scanning reconciles it (routes it through the Recycle Bin if it's genuinely gone). */
  filesSkippedMissing: number
}

export function organizeLibrary(path: string): Promise<OrganizeResult> {
  return fetch('/api/organize', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ path }),
  }).then(asJson<OrganizeResult>)
}

/** Renames an Event's title. Once it's been Organized, this also renames its real folder on disk to match. */
export function renameEvent(path: string, eventId: number, title: string): Promise<void> {
  return fetch('/api/events/rename', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ path, eventId, title }),
  }).then(expectOk)
}

/** Renames a TripGroup's title. Once it's been Organized, this also renames its real folder (and every nested Event's folder under it) on disk to match. */
export function renameTripGroup(path: string, tripGroupId: number, title: string): Promise<void> {
  return fetch('/api/trips/rename', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ path, tripGroupId, title }),
  }).then(expectOk)
}

/**
 * Moves a MediaItem into a different Event (or, with targetEventId null, into
 * a brand new standalone Event of its own) — the Lightbox's "Move to Event"
 * fix for a wrong grouping. Refused (409) if either Event has already been
 * organized into a real on-disk folder.
 */
export function moveMediaItemToEvent(path: string, mediaItemId: number, targetEventId: number | null): Promise<void> {
  return fetch('/api/media-items/move-to-event', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ path, mediaItemId, targetEventId }),
  }).then(expectOk)
}

/** Manually groups a range of Events into a new Trip. Refused (409) if any Event already belongs to one. */
export function createTripGroup(path: string, eventIds: number[]): Promise<TripGroupDto> {
  return fetch('/api/trips', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ path, eventIds }),
  }).then(asJson<TripGroupDto>)
}

/** Undoes a Trip grouping (manual or auto-formed) — its Events become standalone again. */
export function ungroupTripGroup(path: string, tripGroupId: number): Promise<void> {
  return fetch(`/api/trips/${tripGroupId}/ungroup`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ path }),
  }).then(expectOk)
}

/** Ejects one Event from its Trip back to standalone — the whole Trip stays (see ungroupTripGroup for removing all of it at once). */
export function removeEventFromTrip(path: string, eventId: number): Promise<void> {
  return fetch(`/api/events/${eventId}/remove-from-trip`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ path }),
  }).then(expectOk)
}

/** Merges every item out of sourceEventId into targetEventId and deletes the now-empty source. Refused (409) if either Event is already organized into a real folder. */
export function mergeEventInto(path: string, sourceEventId: number, targetEventId: number): Promise<void> {
  return fetch('/api/events/merge', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ path, sourceEventId, targetEventId }),
  }).then(expectOk)
}

// An edit (e.g. Lightbox rotate) changes what a MediaItem's thumbnail/full
// image render to without changing its URL at all, so the browser happily
// keeps showing its old cached response everywhere that URL is used —
// confirmed live as a rotated photo's grid thumbnail still showing the old
// orientation even after the Lightbox's own (separately cache-busted) view
// of it was already correct. A single shared version number per item,
// bumped once by bumpImageVersion and read by every thumbnailUrl/
// fullImageUrl call for that item from then on, fixes every view of it at
// once without each caller needing to track or pass anything itself.
const imageVersions = new Map<number, number>()

export function bumpImageVersion(mediaItemId: number): void {
  imageVersions.set(mediaItemId, (imageVersions.get(mediaItemId) ?? 0) + 1)
}

function appendCacheBust(params: URLSearchParams, mediaItemId?: number): void {
  const version = mediaItemId !== undefined ? imageVersions.get(mediaItemId) : undefined
  if (version) params.set('v', String(version))
}

export function thumbnailUrl(libraryPath: string, relativePath: string, mediaItemId?: number): string {
  const params = new URLSearchParams({ libraryPath, relativePath })
  appendCacheBust(params, mediaItemId)
  return `/api/media/thumbnail?${params.toString()}`
}

export function fullImageUrl(libraryPath: string, relativePath: string, mediaItemId?: number): string {
  const params = new URLSearchParams({ libraryPath, relativePath })
  appendCacheBust(params, mediaItemId)
  return `/api/media/full?${params.toString()}`
}

export function rawFileUrl(libraryPath: string, relativePath: string): string {
  const params = new URLSearchParams({ libraryPath, relativePath })
  return `/api/media/raw?${params.toString()}`
}

export interface DeletedItemDto {
  id: number
  originalRelativePath: string
  kind: 'Photo' | 'Video'
  deletedAt: string
  purgeAt: string
  source: string
}

export function deleteMediaItem(path: string, mediaItemId: number): Promise<DeletedItemDto> {
  return fetch('/api/recyclebin/delete', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ path, mediaItemId }),
  }).then(asJson<DeletedItemDto>)
}

export function getRecycleBin(path: string): Promise<DeletedItemDto[]> {
  return fetch(`/api/recyclebin?path=${encodeURIComponent(path)}`).then(asJson<DeletedItemDto[]>)
}

/** Manual last-resort date fix (TakenAtSource.Manual) for a MediaItem whose Exif is missing or wrong. */
export function setTakenAt(path: string, mediaItemId: number, takenAt: string): Promise<void> {
  return fetch('/api/media-items/taken-at', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ path, mediaItemId, takenAt }),
  }).then(expectOk)
}

export interface ResetTakenAtResult {
  takenAt: string
  takenAtSource: MediaItemDto['takenAtSource']
}

/**
 * Recovers a MediaItem's true date after a bad manual edit by re-reading
 * the real Exif/video metadata straight off the file — see
 * LibraryController.ResetTakenAt for why this needs no separate undo
 * history to maintain.
 */
export function resetTakenAt(path: string, mediaItemId: number): Promise<ResetTakenAtResult> {
  return fetch('/api/media-items/reset-taken-at', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ path, mediaItemId }),
  }).then(asJson<ResetTakenAtResult>)
}

/**
 * Bulk sibling of setTakenAt for a whole folder of undated/mis-dated scans
 * at once (e.g. a legacy "pre-1998" album) — every MediaItem in every given
 * Event gets set to the exact same takenAt. See
 * LibraryRepository.SetTakenAtForEvents for why this doesn't try to
 * preserve/re-derive relative ordering.
 */
export function fixEventDates(path: string, eventIds: number[], takenAt: string): Promise<void> {
  return fetch('/api/events/fix-dates', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ path, eventIds, takenAt }),
  }).then(expectOk)
}

/** Non-destructive (PLAN.md §10) — a database-only Edit row, the original file is never touched. */
export function rotateMediaItem(path: string, mediaItemId: number, deltaDegrees: number): Promise<void> {
  return fetch('/api/media-items/rotate', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ path, mediaItemId, deltaDegrees }),
  }).then(expectOk)
}

export function restoreDeletedItem(path: string, deletedItemId: number): Promise<void> {
  return fetch('/api/recyclebin/restore', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ path, deletedItemId }),
  }).then(expectOk)
}

export function purgeDeletedItem(path: string, deletedItemId: number): Promise<void> {
  return fetch('/api/recyclebin/purge', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ path, deletedItemId }),
  }).then(expectOk)
}

export function recycleBinThumbnailUrl(libraryPath: string, deletedItemId: number, size?: number): string {
  const params = new URLSearchParams({ path: libraryPath, deletedItemId: String(deletedItemId) })
  if (size !== undefined) params.set('size', String(size))
  return `/api/recyclebin/thumbnail?${params.toString()}`
}

export function recycleBinRawUrl(libraryPath: string, deletedItemId: number): string {
  const params = new URLSearchParams({ path: libraryPath, deletedItemId: String(deletedItemId) })
  return `/api/recyclebin/raw?${params.toString()}`
}

export interface UploadResult {
  filesSaved: number
  filesSkipped: number
  scan: ScanResult
  picasaImport: PicasaImportResult
}

export interface PicasaImportResult {
  iniFilesFound: number
  contactsFileFound: boolean
  albumsFileFound: boolean
  photosMatched: number
  photosWithNewData: number
  facesImported: number
  peopleMatched: number
  editsImported: number
  tagsApplied: number
}

export interface DuplicateGroupDto {
  items: MediaItemDto[]
  suggestedKeepId: number
}

export function scanDuplicates(path: string): Promise<DuplicateGroupDto[]> {
  return fetch('/api/duplicates/scan', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ path }),
  }).then(asJson<DuplicateGroupDto[]>)
}

export function resolveDuplicateGroup(
  path: string,
  keepMediaItemId: number,
  removeMediaItemIds: number[],
): Promise<{ itemsRemoved: number }> {
  return fetch('/api/duplicates/resolve', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ path, keepMediaItemId, removeMediaItemIds }),
  }).then(asJson<{ itemsRemoved: number }>)
}

export function importPicasaData(path: string): Promise<PicasaImportResult> {
  return fetch('/api/picasa-import', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ path }),
  }).then(asJson<PicasaImportResult>)
}

export interface TagDto {
  id: number
  name: string
  isFavoriteShortcut: boolean
  itemCount: number
  coverRelativePath: string | null
}

export function getTags(path: string): Promise<TagDto[]> {
  return fetch(`/api/tags?path=${encodeURIComponent(path)}`).then(asJson<TagDto[]>)
}

export function getTagItems(path: string, tagId: number): Promise<MediaItemDto[]> {
  const params = new URLSearchParams({ path })
  return fetch(`/api/tags/${tagId}/items?${params.toString()}`).then(asJson<MediaItemDto[]>)
}

export interface FavoriteCategoryDto {
  id: number
  name: string
}

/** All favorite categories, including empty ones — for Settings and the star-button picker. */
export function getFavoriteCategories(path: string): Promise<FavoriteCategoryDto[]> {
  return fetch(`/api/tags/favorites?path=${encodeURIComponent(path)}`).then(asJson<FavoriteCategoryDto[]>)
}

/** Refused (409) if a tag/album by that name already exists. */
export function createFavoriteCategory(path: string, name: string): Promise<FavoriteCategoryDto> {
  return fetch('/api/tags/favorites', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ path, name }),
  }).then(asJson<FavoriteCategoryDto>)
}

export function deleteFavoriteCategory(path: string, tagId: number): Promise<void> {
  const params = new URLSearchParams({ path })
  return fetch(`/api/tags/favorites/${tagId}?${params.toString()}`, { method: 'DELETE' }).then(expectOk)
}

/** The star button on a grid tile. Pass tagId null to clear — at most one favorite category per photo. */
export function setMediaItemFavoriteCategory(path: string, mediaItemId: number, tagId: number | null): Promise<void> {
  return fetch('/api/tags/favorite-category', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ path, mediaItemId, tagId }),
  }).then(expectOk)
}

export function uploadFiles(path: string, files: FileList | File[]): Promise<UploadResult> {
  const form = new FormData()
  form.set('path', path)
  for (const file of files) {
    form.append('files', file)
  }
  return fetch('/api/upload', { method: 'POST', body: form }).then(asJson<UploadResult>)
}
