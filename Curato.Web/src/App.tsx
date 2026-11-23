import { useEffect, useRef, useState } from 'react'
import './App.css'
import type { AuthStatus, FavoriteCategoryDto, iCloudSyncStatus, LibraryDto, PicasaImportResult } from './api'
import {
  addEventToTripGroup,
  createTripGroup,
  deleteEvents,
  deleteMediaItem,
  deleteMediaItems,
  fixEventDates,
  getAuthStatus,
  getFavoriteCategories,
  getICloudSyncStatus,
  getLibrary,
  getLibraryFolder,
  getScanProgress,
  iCloudCancelSync,
  iCloudSyncNow,
  iCloudVerifySyncTwoFactor,
  logout,
  mergeEventInto,
  moveMediaItemToEvent,
  moveMediaItemsToEvent,
  organizeLibrary,
  removeEventFromTrip,
  renameEvent,
  renameTripGroup,
  resetTakenAt,
  scanLibrary,
  setMediaItemFavoriteCategory,
  setTakenAt,
  ungroupTripGroup,
  uploadFiles,
} from './api'
import { AlbumsPanel } from './components/AlbumsPanel'
import { AuthGate } from './components/AuthGate'
import { DuplicateFinderPanel } from './components/DuplicateFinderPanel'
import { LibraryGrid } from './components/LibraryGrid'
import { RecycleBinPanel } from './components/RecycleBinPanel'
import { SettingsPanel } from './components/SettingsPanel'

const LAST_PATH_KEY = 'curato:lastLibraryPath'

function rememberPath(path: string) {
  try {
    localStorage.setItem(LAST_PATH_KEY, path)
  } catch {
    // Private browsing / blocked storage — losing this is a minor
    // convenience hit, not worth surfacing to the user.
  }
}

function recallPath(): string | null {
  try {
    return localStorage.getItem(LAST_PATH_KEY)
  } catch {
    return null
  }
}

// Picasa import (§8) runs automatically on every scan/upload — no button to
// press, no separate step to remember. This only has anything to say when
// .picasa.ini files were actually found.
function formatPicasaSummary(result: PicasaImportResult): string {
  if (result.iniFilesFound === 0) return ''
  return (
    ` Picasa data: ${result.photosWithNewData} photo(s) newly imported ` +
    `(${result.facesImported} face(s), ${result.editsImported} edit(s), ${result.tagsApplied} tag(s)).`
  )
}

function formatRemovedBareYearTripsNote(removedBareYearTripGroupCount: number): string {
  if (removedBareYearTripGroupCount === 0) return ''
  return ` Ungrouped ${removedBareYearTripGroupCount} fictitious year-folder "trip(s)" back into standalone events.`
}

function formatRemovedEmptyEventsNote(removedEmptyEventCount: number): string {
  if (removedEmptyEventCount === 0) return ''
  return ` Removed ${removedEmptyEventCount} empty event(s) left over from an earlier delete.`
}

function formatRemovedMissingFilesNote(removedMissingFileCount: number): string {
  if (removedMissingFileCount === 0) return ''
  return ` Removed ${removedMissingFileCount} item(s) whose file was deleted on disk.`
}

function formatRepairedVideoTakenAtNote(repairedVideoTakenAtCount: number): string {
  if (repairedVideoTakenAtCount === 0) return ''
  return ` Fixed ${repairedVideoTakenAtCount} video(s) with a decades-too-early date (a known encoder bug).`
}

// proposedFolderPath (set on an Event/TripGroup) is exactly the dry-run
// preview Organize would act on — the same field LibraryGrid already reads
// to show each "Will move to: ..." pill. Nothing carrying one means a real
// Organize run right now would move zero files, so the bar offering to run
// it has nothing to offer.
function hasPendingOrganize(library: LibraryDto): boolean {
  return (
    library.standaloneEvents.some((event) => event.proposedFolderPath !== null) ||
    library.trips.some(
      (trip) => trip.proposedFolderPath !== null || trip.events.some((event) => event.proposedFolderPath !== null),
    )
  )
}

function App() {
  const [authStatus, setAuthStatus] = useState<AuthStatus | null>(null)
  const [path, setPath] = useState('')
  const [library, setLibrary] = useState<LibraryDto | null>(null)
  const [loadedPath, setLoadedPath] = useState<string | null>(null)
  const [favoriteCategories, setFavoriteCategories] = useState<FavoriteCategoryDto[]>([])
  const [status, setStatus] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [scanning, setScanning] = useState(false)
  // Live count of files found so far, polled from the server while a scan
  // is in flight (see scanPath below and ScanController.GetProgress) — the
  // scan itself is one big blocking request with no way to report partway
  // through, so this is a second, independent request running alongside it.
  const [scannedCount, setScannedCount] = useState(0)
  const scanAbortRef = useRef<AbortController | null>(null)
  const [autoLoading, setAutoLoading] = useState(false)
  // Library/Albums/Recycle Bin/Persons are mutually-exclusive views of the
  // same main content area (a toolbar toggle, not stacked dismissable
  // panels) — Find Duplicates and Settings stay real overlays below since
  // they're one-off tools/actions, not browsable views of the library's
  // contents. Persons has no browsing feature behind it yet (no API, no
  // grid) — it's a placeholder marking where that lands once it's built.
  const [activeView, setActiveView] = useState<'library' | 'albums' | 'recycleBin' | 'persons'>('library')
  const [showDuplicates, setShowDuplicates] = useState(false)
  const [showSettings, setShowSettings] = useState(false)

  // iCloud download — deliberately owned here (not SettingsPanel) so the
  // toolbar button below and Settings' own status display never run two
  // independent pollers racing to interpret the same in-flight download.
  // Settings keeps the Connect/Disconnect/scope-editing UI; this is only
  // "trigger one, watch it, answer its 2FA prompt if Apple asks for one."
  const [iCloudStatus, setICloudStatus] = useState<iCloudSyncStatus | null>(null)
  const [iCloudBusy, setICloudBusy] = useState(false)
  const [iCloudDownloading, setICloudDownloading] = useState(false)
  const [awaitingICloudCode, setAwaitingICloudCode] = useState(false)
  const [iCloudCode, setICloudCode] = useState('')
  const iCloudSyncStartedAtRef = useRef<string | null>(null)
  const iCloudOutcomeHandledRef = useRef(false)

  useEffect(() => {
    getAuthStatus()
      .then(setAuthStatus)
      .catch((err) => setError(err instanceof Error ? err.message : String(err)))
  }, [])

  // Once logged in, silently load a library without any manual step — this
  // is a read-only GET (no re-scan), so it's cheap and never touches files.
  // The configured "Library Folder" (Settings) wins when set: it's a
  // server-side setting shared across every browser/device, and if you've
  // set one up (e.g. for iCloud sync) it's clearly "the" library you care
  // about — synced content should just show up, not require separately
  // remembering to open it. Falls back to this browser's own last-scanned
  // path (localStorage) for the plain manual-folder workflow, when no
  // Library Folder is configured at all.
  useEffect(() => {
    if (!authStatus?.isAuthenticated) {
      return
    }

    setAutoLoading(true)
    getLibraryFolder()
      .then((configuredPath) => configuredPath ?? recallPath())
      .then((pathToLoad) => {
        if (!pathToLoad) {
          return
        }

        setPath(pathToLoad)
        return getLibrary(pathToLoad).then((lib) => {
          setLibrary(lib)
          setLoadedPath(pathToLoad)
          rememberPath(pathToLoad)
        })
      })
      .catch(() => {
        // The folder may have moved/been unmounted since last time — fall
        // back to the empty "type a path" state rather than showing an
        // error for something that just happened automatically.
      })
      .finally(() => setAutoLoading(false))
  }, [authStatus?.isAuthenticated])

  // Favorite categories change rarely compared to the library itself, so
  // this only re-fetches when the open library actually changes, not on
  // every library mutation — Settings calls onFavoriteCategoriesChanged
  // directly after an add/delete instead, the same pattern
  // refreshLibraryIfCurrentlyOpen below uses for an iCloud download.
  useEffect(() => {
    if (!loadedPath) {
      setFavoriteCategories([])
      return
    }

    getFavoriteCategories(loadedPath)
      .then(setFavoriteCategories)
      .catch(() => {
        // Best-effort — the star button just hides itself with zero categories.
      })
  }, [loadedPath])

  function refreshFavoriteCategoriesIfCurrentlyOpen(path: string) {
    if (!loadedPath || loadedPath.toLowerCase() !== path.toLowerCase()) {
      return
    }

    getFavoriteCategories(path)
      .then(setFavoriteCategories)
      .catch(() => {
        // Best-effort, same reasoning as refreshLibraryIfCurrentlyOpen below.
      })
  }

  // Silently re-fetches the currently-open library if it's the one that
  // just changed — for after the toolbar's "Download from iCloud" completes.
  // Confirmed live this was needed: a download updates the library folder's
  // database, but the main grid (if already loaded before it started) has
  // no way to know that happened on its own, so new content stayed
  // invisible until a manual hard refresh. Deliberately a no-op if a
  // different library is currently open — never surprise-swap
  // what's on screen out from under whatever you're actually looking at.
  function refreshLibraryIfCurrentlyOpen(path: string) {
    if (!loadedPath || loadedPath.toLowerCase() !== path.toLowerCase()) {
      return
    }

    getLibrary(path)
      .then(setLibrary)
      .catch(() => {
        // Best-effort — the sync's own success/error status already
        // reported the result; a failed silent refresh isn't worth
        // surfacing as a separate error.
      })
  }

  // Views a library, without necessarily re-scanning — for a folder that's
  // already indexed (e.g. an iCloud sync already ran ScanService
  // server-side; there's nothing to gain from re-scanning just to look at
  // it). Same shape as the auto-load-on-login effect above. If it comes back
  // with nothing at all, though, that's not "genuinely empty", it's "never
  // scanned yet" (the common case right after pointing Settings at a new
  // folder) — confirmed live as confusing otherwise (an empty grid with no
  // hint that a Scan is what's actually needed), so that case scans it right
  // away instead of leaving the user to notice and click Scan themselves.
  async function handleOpenLibrary(pathToOpen: string) {
    setBusy(true)
    setError(null)
    try {
      const lib = await getLibrary(pathToOpen)
      setLibrary(lib)
      setLoadedPath(pathToOpen)
      setPath(pathToOpen)
      rememberPath(pathToOpen)
      setShowSettings(false)
      setActiveView('library')
      if (lib.standaloneEvents.length === 0 && lib.trips.length === 0) {
        await scanPath(pathToOpen)
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
    } finally {
      setBusy(false)
    }
  }

  // Shared with handleOpenLibrary's auto-scan-on-first-open above — both
  // just need "run a scan against this path and refresh the grid from it"
  // with the same status/busy/abort handling.
  //
  // Resilient to the one long-lived scanLibrary() request never actually
  // resolving back to this tab (confirmed live: a 54,000+ file scan over a
  // network share finished and safely persisted everything server-side, but
  // the connection carrying the response apparently got dropped somewhere
  // along the way, leaving the page stuck on "Scanning…" forever even
  // though a manual refresh immediately showed the completed library) — the
  // progress-poll below independently notices the server-side
  // ScanProgressTracker count drop back to 0 (only ever cleared once
  // ScanController's scan actually finishes, success or not) and wraps
  // things up on its own if the original request's own resolution never
  // arrives. finishScan is shared by both paths and guarded so only
  // whichever one notices first actually does anything.
  async function scanPath(pathToScan: string) {
    const controller = new AbortController()
    scanAbortRef.current = controller
    setBusy(true)
    setScanning(true)
    setScannedCount(0)
    setError(null)
    setStatus('Scanning…')

    let hasFinished = false
    let lastSeenCount = 0

    async function finishScan(statusMessage: string) {
      if (hasFinished) return
      hasFinished = true
      clearInterval(progressTimer)
      try {
        const lib = await getLibrary(pathToScan)
        setLibrary(lib)
        setLoadedPath(pathToScan)
        rememberPath(pathToScan)
        setStatus(statusMessage)
      } catch (err) {
        setError(err instanceof Error ? err.message : String(err))
      } finally {
        setBusy(false)
        setScanning(false)
        scanAbortRef.current = null
      }
    }

    const progressTimer = setInterval(() => {
      getScanProgress(pathToScan)
        .then((p) => {
          if (p.filesFoundSoFar === 0 && lastSeenCount > 0) {
            finishScan('Scan finished — refreshing library…')
          } else {
            lastSeenCount = p.filesFoundSoFar
            setScannedCount(p.filesFoundSoFar)
          }
        })
        .catch(() => {
          // Best-effort — a failed poll just means the counter doesn't tick
          // this round; the scan itself doesn't depend on this at all.
        })
    }, 800)
    try {
      const result = await scanLibrary(pathToScan, controller.signal)
      await finishScan(
        `${result.scan.mediaItemCount} items total \u2014 new this scan: ` +
          `${result.scan.newAutoOrganizedEventCount} auto-organized event(s), ` +
          `${result.scan.newAdoptedEventCount} adopted event(s), ` +
          `${result.scan.newTripGroupCount} trip(s), ` +
          `${result.scan.itemsAddedToExistingEvents} item(s) added to existing events.` +
          formatRemovedBareYearTripsNote(result.scan.removedBareYearTripGroupCount) +
          formatRemovedEmptyEventsNote(result.scan.removedEmptyEventCount) +
          formatRemovedMissingFilesNote(result.scan.removedMissingFileCount) +
          formatRepairedVideoTakenAtNote(result.scan.repairedVideoTakenAtCount) +
          formatPicasaSummary(result.picasaImport),
      )
    } catch (err) {
      if (hasFinished) return
      if (err instanceof DOMException && err.name === 'AbortError') {
        // A real stop now (ScanService.Scan actually checks the token
        // mid-walk and persists whatever it found so far), not just this
        // tab detaching from a scan that keeps running regardless.
        await finishScan('Scan stopped — files found so far were saved; run Scan again to pick up where it left off.')
      } else {
        hasFinished = true
        clearInterval(progressTimer)
        setError(err instanceof Error ? err.message : String(err))
        setBusy(false)
        setScanning(false)
        scanAbortRef.current = null
      }
    }
  }

  async function handleScan() {
    if (!path.trim()) return
    await scanPath(path)
  }

  function handleCancelScan() {
    scanAbortRef.current?.abort()
  }

  async function handleOrganize() {
    if (!loadedPath) return
    setBusy(true)
    setError(null)
    setStatus('Organizing…')
    try {
      const result = await organizeLibrary(loadedPath)
      setStatus(
        `Organize: ${result.eventsOrganized} event(s) arranged, ${result.filesMoved} file(s) moved.` +
          (result.filesSkippedMissing > 0
            ? ` ${result.filesSkippedMissing} file(s) skipped — missing from disk (something changed them outside Curato). Re-scan to reconcile.`
            : ''),
      )
      const lib = await getLibrary(loadedPath)
      setLibrary(lib)
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
    } finally {
      setBusy(false)
    }
  }

  async function handleUpload(fileList: FileList | null) {
    if (!fileList || fileList.length === 0 || !loadedPath) return
    setBusy(true)
    setError(null)
    setStatus('Uploading…')
    try {
      const result = await uploadFiles(loadedPath, fileList)
      setStatus(
        `Uploaded ${result.filesSaved} file(s)` +
          (result.filesSkipped > 0 ? ` (${result.filesSkipped} skipped — unsupported type or too large)` : '') +
          `. ${result.scan.newAutoOrganizedEventCount} new event(s), ` +
          `${result.scan.itemsAddedToExistingEvents} item(s) added to existing events.` +
          formatRemovedBareYearTripsNote(result.scan.removedBareYearTripGroupCount) +
          formatRemovedEmptyEventsNote(result.scan.removedEmptyEventCount) +
          formatRepairedVideoTakenAtNote(result.scan.repairedVideoTakenAtCount) +
          formatPicasaSummary(result.picasaImport),
      )
      const lib = await getLibrary(loadedPath)
      setLibrary(lib)
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
    } finally {
      setBusy(false)
    }
  }

  async function handleDelete(mediaItemId: number) {
    if (!loadedPath) return
    setError(null)
    try {
      await deleteMediaItem(loadedPath, mediaItemId)
      const lib = await getLibrary(loadedPath)
      setLibrary(lib)
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
    }
  }

  async function handleDeleteEvents(eventIds: number[]) {
    if (!loadedPath) return
    setError(null)
    try {
      await deleteEvents(loadedPath, eventIds)
      const lib = await getLibrary(loadedPath)
      setLibrary(lib)
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
    }
  }

  async function handleDeleteMediaItems(mediaItemIds: number[]) {
    if (!loadedPath) return
    setError(null)
    try {
      await deleteMediaItems(loadedPath, mediaItemIds)
      const lib = await getLibrary(loadedPath)
      setLibrary(lib)
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
    }
  }

  async function handleTakenAtChanged(mediaItemId: number, takenAt: string) {
    if (!loadedPath) return
    setError(null)
    try {
      await setTakenAt(loadedPath, mediaItemId, takenAt)
      const lib = await getLibrary(loadedPath)
      setLibrary(lib)
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
    }
  }

  async function handleResetTakenAt(mediaItemId: number) {
    if (!loadedPath) return null
    setError(null)
    try {
      const result = await resetTakenAt(loadedPath, mediaItemId)
      const lib = await getLibrary(loadedPath)
      setLibrary(lib)
      return result
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
      return null
    }
  }

  // The actual rotate call already happened inside Lightbox (it needs the
  // result immediately, to bust its own <img>'s cache) — this just keeps
  // the rest of the app's data fresh afterward, same as the other handlers.
  async function handleRotated() {
    if (!loadedPath) return
    const lib = await getLibrary(loadedPath)
    setLibrary(lib)
  }

  async function handleRenameEvent(eventId: number, title: string) {
    if (!loadedPath) return
    setError(null)
    try {
      await renameEvent(loadedPath, eventId, title)
      const lib = await getLibrary(loadedPath)
      setLibrary(lib)
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
    }
  }

  async function handleRenameTrip(tripGroupId: number, title: string) {
    if (!loadedPath) return
    setError(null)
    try {
      await renameTripGroup(loadedPath, tripGroupId, title)
      const lib = await getLibrary(loadedPath)
      setLibrary(lib)
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
    }
  }

  async function handleMoveMediaItemToEvent(mediaItemId: number, targetEventId: number | null) {
    if (!loadedPath) return
    setError(null)
    try {
      await moveMediaItemToEvent(loadedPath, mediaItemId, targetEventId)
      const lib = await getLibrary(loadedPath)
      setLibrary(lib)
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
    }
  }

  async function handleMoveMediaItemsToEvent(mediaItemIds: number[], targetEventId: number) {
    if (!loadedPath) return
    setError(null)
    try {
      await moveMediaItemsToEvent(loadedPath, mediaItemIds, targetEventId)
      const lib = await getLibrary(loadedPath)
      setLibrary(lib)
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
    }
  }

  async function handleSetFavoriteCategory(mediaItemId: number, tagId: number | null) {
    if (!loadedPath) return
    setError(null)
    try {
      await setMediaItemFavoriteCategory(loadedPath, mediaItemId, tagId)
      const lib = await getLibrary(loadedPath)
      setLibrary(lib)
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
    }
  }

  async function handleCreateTripGroup(eventIds: number[]) {
    if (!loadedPath) return
    setError(null)
    try {
      await createTripGroup(loadedPath, eventIds)
      const lib = await getLibrary(loadedPath)
      setLibrary(lib)
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
    }
  }

  async function handleUngroupTripGroup(tripGroupId: number) {
    if (!loadedPath) return
    setError(null)
    try {
      await ungroupTripGroup(loadedPath, tripGroupId)
      const lib = await getLibrary(loadedPath)
      setLibrary(lib)
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
    }
  }

  // Sequential, not fired-off-in-parallel — same reasoning as
  // handleMergeEventsInto just below.
  async function handleRemoveEventsFromTrip(eventIds: number[]) {
    if (!loadedPath) return
    setError(null)
    try {
      for (const eventId of eventIds) {
        await removeEventFromTrip(loadedPath, eventId)
      }
      const lib = await getLibrary(loadedPath)
      setLibrary(lib)
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
    }
  }

  async function handleFixEventDates(eventIds: number[], takenAt: string) {
    if (!loadedPath) return
    setError(null)
    try {
      await fixEventDates(loadedPath, eventIds, takenAt)
      const lib = await getLibrary(loadedPath)
      setLibrary(lib)
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
    }
  }

  // Sequential, not fired-off-in-parallel: several merges hitting the same
  // library.db concurrently risked overlapping write transactions, and this
  // also means one final library refresh instead of one per source Event.
  async function handleMergeEventsInto(sourceEventIds: number[], targetEventId: number) {
    if (!loadedPath) return
    setError(null)
    try {
      for (const sourceEventId of sourceEventIds) {
        await mergeEventInto(loadedPath, sourceEventId, targetEventId)
      }
      const lib = await getLibrary(loadedPath)
      setLibrary(lib)
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
    }
  }

  // Sequential, same reasoning as handleMergeEventsInto above — the backend
  // only ever adds one Event to a Trip per call.
  async function handleAddEventsToTrip(eventIds: number[], tripGroupId: number) {
    if (!loadedPath) return
    setError(null)
    try {
      for (const eventId of eventIds) {
        await addEventToTripGroup(loadedPath, eventId, tripGroupId)
      }
      const lib = await getLibrary(loadedPath)
      setLibrary(lib)
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
    }
  }

  // Refreshes just the small connected/last-download summary shown both by
  // the toolbar button's title and Settings' iCloud tab — Settings fetches
  // its own copy for the fuller connect/scope-editing UI, but this is the
  // one that drives whether the toolbar button appears at all.
  function refreshICloudStatus() {
    getICloudSyncStatus()
      .then(setICloudStatus)
      .catch(() => {
        // Not logged in yet / no library open yet — the toolbar button
        // just stays hidden until a later poll succeeds.
      })
  }

  useEffect(() => {
    if (authStatus?.isAuthenticated) refreshICloudStatus()
  }, [authStatus?.isAuthenticated])

  // Marks a download attempt as done, however that was actually noticed —
  // see the polling effect below for why both this request's own response
  // AND an independent poll can each legitimately be the one to notice.
  function finishICloudDownload(outcome: { success: boolean; errorMessage?: string | null }) {
    if (iCloudOutcomeHandledRef.current) return
    iCloudOutcomeHandledRef.current = true

    setICloudDownloading(false)
    setAwaitingICloudCode(false)
    setICloudCode('')
    setICloudBusy(false)
    if (!outcome.success) {
      setError(outcome.errorMessage ?? 'Download failed.')
    } else {
      setError(null)
      if (loadedPath) refreshLibraryIfCurrentlyOpen(loadedPath)
    }
    refreshICloudStatus()
  }

  // Downloading from iCloud (and verifying a 2FA code) are single
  // long-running requests with no progress in their own response — poll the
  // status endpoint on the side while one's in flight. Also the ONLY
  // reliable way to notice either a download parked at Apple's 2FA prompt,
  // or one that's actually finished: confirmed live that a download slow
  // enough to reach that prompt (or slow enough to finish afterward) can
  // also be slow enough for the dev proxy to drop that one long-lived
  // connection along the way, in which case the triggering request's own
  // promise never resolves — this poll catches both regardless, using the
  // server's own LastSyncAt advancing as proof it actually finished.
  useEffect(() => {
    if (!iCloudDownloading) return
    const interval = setInterval(() => {
      getICloudSyncStatus()
        .then((s) => {
          setICloudStatus(s)
          if (s.twoFactorRequired) {
            setAwaitingICloudCode(true)
            setICloudDownloading(false)
          } else if (s.lastSyncAt && s.lastSyncAt !== iCloudSyncStartedAtRef.current) {
            finishICloudDownload({ success: s.lastSyncStatus === 'Success', errorMessage: s.lastSyncMessage })
          }
        })
        .catch(() => {
          // Transient poll failure — the main call's own error handling
          // covers real failures, so this is safe to ignore.
        })
    }, 1500)
    return () => clearInterval(interval)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [iCloudDownloading])

  useEffect(() => {
    if (!awaitingICloudCode) return
    function onKeyDown(e: KeyboardEvent) {
      if (e.key === 'Escape') handleICloudCancelCode()
    }
    window.addEventListener('keydown', onKeyDown)
    return () => window.removeEventListener('keydown', onKeyDown)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [awaitingICloudCode])

  // Deliberately not a try/catch-await — every completion path (success,
  // failure, or the request itself erroring) funnels through
  // finishICloudDownload instead, which is guarded against running twice,
  // so the poll above can safely race against this request's own response.
  function handleICloudDownloadNow() {
    iCloudOutcomeHandledRef.current = false
    iCloudSyncStartedAtRef.current = iCloudStatus?.lastSyncAt ?? null
    setICloudDownloading(true)
    setICloudBusy(true)
    setError(null)
    iCloudSyncNow()
      .then((result) => {
        if (result.twoFactorRequired) {
          // icloudpd is parked waiting on this exact process's stdin —
          // handleICloudVerifyCode below answers it, not a fresh attempt.
          setAwaitingICloudCode(true)
          setICloudDownloading(false)
          setICloudBusy(false)
        } else {
          finishICloudDownload({ success: result.success, errorMessage: result.errorMessage })
        }
      })
      .catch((err) => finishICloudDownload({ success: false, errorMessage: err instanceof Error ? err.message : String(err) }))
  }

  function handleICloudVerifyCode() {
    iCloudOutcomeHandledRef.current = false
    iCloudSyncStartedAtRef.current = iCloudStatus?.lastSyncAt ?? null
    setICloudDownloading(true)
    setICloudBusy(true)
    setError(null)
    iCloudVerifySyncTwoFactor(iCloudCode)
      .then((result) => finishICloudDownload({ success: result.success, errorMessage: result.errorMessage }))
      .catch((err) => finishICloudDownload({ success: false, errorMessage: err instanceof Error ? err.message : String(err) }))
  }

  function handleICloudCancelCode() {
    setICloudBusy(true)
    iCloudCancelSync()
      .then(() => {
        setAwaitingICloudCode(false)
        setICloudCode('')
      })
      .catch((err) => setError(err instanceof Error ? err.message : String(err)))
      .finally(() => setICloudBusy(false))
  }

  async function handleLogout() {
    await logout()
    setLibrary(null)
    setLoadedPath(null)
    setAuthStatus((prev) => (prev ? { ...prev, isAuthenticated: false, username: null } : prev))
  }

  if (!authStatus) {
    return (
      <main className="app-shell">
        <h1>Curato</h1>
      </main>
    )
  }

  if (authStatus.needsSetup || !authStatus.isAuthenticated) {
    return (
      <AuthGate
        needsSetup={authStatus.needsSetup}
        onSignedIn={() => getAuthStatus().then(setAuthStatus)}
      />
    )
  }

  return (
    <main className="app-shell">
      <header className="app-header">
        <div className="app-brand">
          <img src="/favicon.svg" alt="" className="app-logo" width={40} height={40} />
          <h1>Curato</h1>
        </div>
        <div className="account">
          {loadedPath && (
            <div className="view-switcher">
              <button
                type="button"
                className={activeView === 'library' ? 'is-active' : ''}
                onClick={() => setActiveView('library')}
              >
                Library
              </button>
              <button
                type="button"
                className={activeView === 'albums' ? 'is-active' : ''}
                onClick={() => setActiveView('albums')}
              >
                Albums
              </button>
              <button
                type="button"
                className={activeView === 'persons' ? 'is-active' : ''}
                onClick={() => setActiveView('persons')}
              >
                Persons
              </button>
              <button
                type="button"
                className={activeView === 'recycleBin' ? 'is-active' : ''}
                onClick={() => setActiveView('recycleBin')}
              >
                Recycle Bin
              </button>
            </div>
          )}
          {loadedPath && (
            <button type="button" onClick={() => setShowDuplicates(true)}>
              Find Duplicates
            </button>
          )}
          {loadedPath && (
            <label className="upload-button">
              {busy ? 'Working…' : 'Add photos'}
              <input
                type="file"
                accept="image/*,video/*"
                multiple
                disabled={busy}
                onChange={(e) => {
                  handleUpload(e.target.files)
                  e.target.value = ''
                }}
              />
            </label>
          )}
          {iCloudStatus?.connected && (
            <button
              type="button"
              onClick={handleICloudDownloadNow}
              disabled={iCloudBusy || awaitingICloudCode}
              title="Download any new photos/videos from iCloud"
            >
              {iCloudDownloading ? 'Downloading…' : 'Download from iCloud'}
            </button>
          )}
          <button
            type="button"
            onClick={handleScan}
            disabled={busy || !path.trim()}
            title={path ? `Re-scan ${path} for new/changed files` : 'Set a library folder in Settings first'}
          >
            {busy ? 'Scanning…' : 'Scan'}
          </button>
          <button type="button" onClick={() => setShowSettings(true)}>
            Settings
          </button>
          <span>{authStatus.username}</span>
          <button type="button" onClick={handleLogout}>
            Sign out
          </button>
        </div>
      </header>

      {scanning && (
        <div className="scan-progress-bar">
          <span className="scan-progress-label">
            {scannedCount > 0 ? `Scanning… ${scannedCount} file(s) found so far` : 'Scanning…'}
          </span>
          <div className="scan-progress-track">
            <div className="scan-progress-fill" />
          </div>
          <button
            type="button"
            onClick={handleCancelScan}
            title="Actually stops the scan mid-walk — whatever it's found so far is kept, not discarded"
          >
            Stop
          </button>
        </div>
      )}

      {!loadedPath && !autoLoading && (
        <p className="tagline">No library folder configured yet — open Settings to point Curato at your photos.</p>
      )}

      {autoLoading && <p className="status">Loading your library…</p>}
      {status && <p className="status">{status}</p>}
      {error && <p className="error">{error}</p>}

      {showDuplicates && loadedPath && (
        <DuplicateFinderPanel
          libraryPath={loadedPath}
          onClose={() => {
            setShowDuplicates(false)
            getLibrary(loadedPath).then(setLibrary)
          }}
        />
      )}

      {showSettings && (
        <SettingsPanel
          onClose={() => setShowSettings(false)}
          onOpenLibrary={handleOpenLibrary}
          onFavoriteCategoriesChanged={refreshFavoriteCategoriesIfCurrentlyOpen}
        />
      )}

      {awaitingICloudCode && (
        <div className="modal-overlay" onClick={handleICloudCancelCode}>
          <div className="modal-dialog icloud-2fa-dialog" onClick={(e) => e.stopPropagation()}>
            <div className="modal-header">
              <h2>Verify iCloud</h2>
            </div>
            <div className="modal-body">
              <p className="settings-meta">
                Apple sent a verification code to one of your devices — enter it to continue this download.
              </p>
              <input
                type="text"
                inputMode="numeric"
                autoComplete="one-time-code"
                value={iCloudCode}
                onChange={(e) => setICloudCode(e.target.value)}
                autoFocus
              />
            </div>
            <div className="modal-footer">
              <button type="button" className="modal-footer-cancel" onClick={handleICloudCancelCode} disabled={iCloudBusy}>
                Cancel
              </button>
              <button
                type="button"
                className="modal-footer-save"
                onClick={handleICloudVerifyCode}
                disabled={iCloudBusy || !iCloudCode.trim()}
              >
                {iCloudBusy ? 'Verifying…' : 'Verify'}
              </button>
            </div>
          </div>
        </div>
      )}

      {loadedPath && activeView === 'recycleBin' && (
        <div className="library">
          <RecycleBinPanel libraryPath={loadedPath} />
        </div>
      )}

      {loadedPath && activeView === 'albums' && (
        <div className="library">
          <AlbumsPanel
            libraryPath={loadedPath}
            onDelete={handleDelete}
            onTakenAtChanged={handleTakenAtChanged}
            onResetTakenAt={handleResetTakenAt}
            onRotated={handleRotated}
            favoriteCategories={favoriteCategories}
            onSetFavoriteCategory={handleSetFavoriteCategory}
          />
        </div>
      )}

      {loadedPath && activeView === 'persons' && (
        <div className="library">
          <div className="library-tab-panel">
            <div className="recyclebin-header">
              <h2>Persons</h2>
            </div>
            <p className="recyclebin-hint">Browsing photos by person isn't built yet — coming later.</p>
          </div>
        </div>
      )}

      {library && loadedPath && activeView === 'library' && (
        <div className="library">
          {hasPendingOrganize(library) && (
            <div className="organize-bar">
              <button type="button" onClick={handleOrganize} disabled={busy}>
                {busy ? 'Working…' : 'Organize'}
              </button>
              <span className="organize-hint">
                Moves newly auto-organized items into their dated folders. Adopted folders are
                never touched.
              </span>
            </div>
          )}
          <LibraryGrid
            library={library}
            libraryPath={loadedPath}
            favoriteCategories={favoriteCategories}
            scanning={scanning}
            onDelete={handleDelete}
            onDeleteEvents={handleDeleteEvents}
            onDeleteMediaItems={handleDeleteMediaItems}
            onTakenAtChanged={handleTakenAtChanged}
            onResetTakenAt={handleResetTakenAt}
            onRotated={handleRotated}
            onRenameEvent={handleRenameEvent}
            onRenameTrip={handleRenameTrip}
            onMoveMediaItemToEvent={handleMoveMediaItemToEvent}
            onMoveMediaItemsToEvent={handleMoveMediaItemsToEvent}
            onCreateTripGroup={handleCreateTripGroup}
            onAddEventsToTrip={handleAddEventsToTrip}
            onUngroupTripGroup={handleUngroupTripGroup}
            onMergeEventsInto={handleMergeEventsInto}
            onRemoveEventsFromTrip={handleRemoveEventsFromTrip}
            onFixEventDates={handleFixEventDates}
            onSetFavoriteCategory={handleSetFavoriteCategory}
          />
        </div>
      )}
    </main>
  )
}

export default App
