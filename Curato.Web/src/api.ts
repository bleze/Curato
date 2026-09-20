export interface MediaItemDto {
  id: number
  relativePath: string
  kind: 'Photo' | 'Video'
  takenAt: string
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

export function scanLibrary(path: string): Promise<ScanResult> {
  return fetch('/api/scan', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ path }),
  }).then(asJson<ScanResult>)
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

export function thumbnailUrl(libraryPath: string, relativePath: string): string {
  const params = new URLSearchParams({ libraryPath, relativePath })
  return `/api/media/thumbnail?${params.toString()}`
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

export function uploadFiles(path: string, files: FileList | File[]): Promise<UploadResult> {
  const form = new FormData()
  form.set('path', path)
  for (const file of files) {
    form.append('files', file)
  }
  return fetch('/api/upload', { method: 'POST', body: form }).then(asJson<UploadResult>)
}
