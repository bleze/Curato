export interface MediaItemDto {
  id: number
  relativePath: string
  kind: 'Photo' | 'Video'
  takenAt: string
  /** Where takenAt actually came from — lets the UI flag a date that's just the file's filesystem timestamp rather than a real Exif/video tag. */
  takenAtSource: 'ExifDateTaken' | 'ExifModifyDate' | 'VideoMetadata' | 'FileSystem' | 'Manual'
  /** Set when this is the still frame of an Apple Live Photo — the paired .MOV's relative path. */
  liveVideoRelativePath: string | null
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
  events: EventDto[]
}

export interface LibraryDto {
  trips: TripGroupDto[]
  standaloneEvents: EventDto[]
}

export interface ScanResult {
  mediaItemCount: number
  newAutoOrganizedEventCount: number
  newAdoptedEventCount: number
  newTripGroupCount: number
  itemsAddedToExistingEvents: number
  /** Leftover auto-formed (time-gap-only) Trips from before that heuristic was disabled, ungrouped this scan. */
  removedAutoTripGroupCount: number
  /** Leftover fictitious "2019"-style bare-year Trips from before that heuristic was fixed, ungrouped this scan. */
  removedBareYearTripGroupCount: number
  /** Previously-indexed items whose file was deleted on disk outside Curato, cleaned up this scan. */
  removedMissingFileCount: number
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

export function setupAccount(username: string, password: string): Promise<void> {
  return fetch('/api/auth/setup', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ username, password }),
  }).then(expectOk)
}

export function login(username: string, password: string): Promise<void> {
  return fetch('/api/auth/login', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ username, password }),
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

export function scanLibrary(path: string): Promise<ScanApiResponse> {
  return fetch('/api/scan', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ path }),
  }).then(asJson<ScanApiResponse>)
}

export function getLibrary(path: string): Promise<LibraryDto> {
  return fetch(`/api/library?path=${encodeURIComponent(path)}`).then(asJson<LibraryDto>)
}

export interface OrganizeResult {
  eventsOrganized: number
  filesMoved: number
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

export function thumbnailUrl(libraryPath: string, relativePath: string): string {
  const params = new URLSearchParams({ libraryPath, relativePath })
  return `/api/media/thumbnail?${params.toString()}`
}

export function fullImageUrl(libraryPath: string, relativePath: string): string {
  const params = new URLSearchParams({ libraryPath, relativePath })
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

export function recycleBinThumbnailUrl(libraryPath: string, deletedItemId: number): string {
  const params = new URLSearchParams({ path: libraryPath, deletedItemId: String(deletedItemId) })
  return `/api/recyclebin/thumbnail?${params.toString()}`
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

export function uploadFiles(path: string, files: FileList | File[]): Promise<UploadResult> {
  const form = new FormData()
  form.set('path', path)
  for (const file of files) {
    form.append('files', file)
  }
  return fetch('/api/upload', { method: 'POST', body: form }).then(asJson<UploadResult>)
}
