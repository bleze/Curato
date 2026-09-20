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
  startAt: string
  endAt: string
  items: MediaItemDto[]
}

export interface TripGroupDto {
  id: number
  title: string
  namingMode: 'Adopted' | 'CuratoManaged'
  folderPath: string | null
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
  autoOrganizedEventCount: number
  adoptedEventCount: number
  tripGroupCount: number
}

async function asJson<T>(response: Response): Promise<T> {
  if (!response.ok) {
    const text = await response.text()
    throw new Error(text || `${response.status} ${response.statusText}`)
  }
  return response.json() as Promise<T>
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

export function thumbnailUrl(libraryPath: string, relativePath: string): string {
  const params = new URLSearchParams({ libraryPath, relativePath })
  return `/api/media/thumbnail?${params.toString()}`
}
