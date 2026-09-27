import { useVirtualizer } from '@tanstack/react-virtual'
import { useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
import type { EventDto, FavoriteCategoryDto, LibraryDto, MediaItemDto, TripGroupDto } from '../api'
import { bumpImageVersion, rotateMediaItem, thumbnailUrl } from '../api'
import type { DateMarker } from './DateScrubber'
import { DateScrubber } from './DateScrubber'
import type { EventSummary } from './EventPicker'
import { EventPicker } from './EventPicker'
import { FavoriteCategoryMenu } from './FavoriteCategoryMenu'
import { formatTakenAt, toDatetimeLocalValue } from '../format'
import { Lightbox } from './Lightbox'
import { Slideshow } from './Slideshow'
import { useThumbMinWidth } from '../thumbSize'
import { useUse24HourTime } from '../timeFormat'
import { YearJumpBar } from './YearJumpBar'

// Mirrors the old CSS grid's `repeat(auto-fill, minmax(120px, 1fr))` — kept
// in JS now so row height can be computed for virtualization instead of
// letting the browser lay out an unbounded number of rows at once. The
// minimum itself is a user preference now (thumbSize.ts), not a constant.
const GAP = 6
// These are exact, not generous guesses — see the getItemKey/estimateSize
// comment below for why headers are never remeasured against reality once
// rendered (that's the scrollbar drag-handle desync bug's real remaining
// cause). Several estimate-vs-reality mismatches have already come from
// this file guessing wrong here, all confirmed live:
// (1) an under-filled EVENT_HEADER_HEIGHT left a dead gap below every
//     non-Organized event's header, and (2) a title or "Will move to" hint
//     that wrapped to a second line made the reserved slot too SHORT,
//     which is worse — it corrupts the next block's position, not just
//     wastes space. Rather than keep guessing a buffer, .editable-title-text
//     and .proposed-path (App.css) are both forced to a single line via
//     `text-overflow: ellipsis` — the actual rendered height of a header is
//     now a true constant, not a function of arbitrary-length content, so
//     these numbers can be tight instead of generous. TripGroup headers
//     still stack `.proposed-path` on its own line below the title (real
//     vertical space, not wasted) rather than cramming it into the same row
//     — its own container is wide enough that this hasn't needed the same
//     right-aligned same-row treatment .event-header got.
// (3) The visual gap under a header has to live *inside* this number, not
//     in CSS margin — every block here is `position: absolute`, placed by
//     summing estimateSize()'s return values, not normal document flow, so
//     a margin between two virtualized siblings is silently ignored
//     (confirmed live: bumping .event-header's margin-bottom from 8px to
//     20px changed nothing on screen). EVENT_HEADER_CONTENT_HEIGHT is the
//     header's real measured height (padding-top + its one text row, no
//     wrap possible per (2) above); EVENT_HEADER_GAP is genuine breathing
//     room added on top of that, big enough that a hovered thumbnail's
//     `transform: scale(1.08)` (App.css) has room to grow into without
//     visually overlapping the "Will move to" pill above it.
const EVENT_HEADER_CONTENT_HEIGHT = 50
const EVENT_HEADER_GAP = 11
const EVENT_HEADER_HEIGHT = EVENT_HEADER_CONTENT_HEIGHT + EVENT_HEADER_GAP
const TRIP_HEADER_HEIGHT = 84
// The exact extra top space .event-header-after-trip adds over the base
// .event-header's own padding-top (see that class, App.css) — a standalone
// Event immediately following a Trip gets the same accent top-border
// treatment as a Trip header itself, closing off the Trip visually instead
// of the line only ever appearing on the way in. Must stay a real, accounted
// -for constant like every other height here: LibraryGrid.tsx never
// remeasures a header against its actual rendered DOM height (the scrollbar
// drag-handle desync bug's own root cause), so any extra CSS height has to
// be added here too, not just in the stylesheet.
const EVENT_HEADER_AFTER_TRIP_EXTRA_HEIGHT = 20

type Block =
  | { type: 'trip-header'; key: string; trip: TripGroupDto }
  | { type: 'event-header'; key: string; event: EventDto; closesTripAbove: boolean }
  | { type: 'thumb-row'; key: string; event: EventDto; items: MediaItemDto[]; startIndex: number }

// Shared by the virtualizer's own estimateSize and buildDateMarkers's
// cumulative-height math below — both need the exact same per-block height
// to agree, or the date scrubber's markers wouldn't actually line up with
// where their content really sits (which is exactly the bug this fixes:
// markers used to be spaced by counting distinct months rather than by real
// rendered height, so they never matched the native scrollbar next to them,
// which moves by real pixel height like every scrollbar does).
function blockHeight(block: Block, rowHeight: number): number {
  if (block.type === 'trip-header') {
    return TRIP_HEADER_HEIGHT
  }
  if (block.type === 'event-header') {
    return EVENT_HEADER_HEIGHT + (block.closesTripAbove ? EVENT_HEADER_AFTER_TRIP_EXTRA_HEIGHT : 0)
  }
  return rowHeight
}

function formatRange(startAt: string, endAt: string): string {
  const start = new Date(startAt)
  const end = new Date(endAt)
  const sameDay = start.toDateString() === end.toDateString()
  const dateFmt: Intl.DateTimeFormatOptions = { year: 'numeric', month: 'short', day: 'numeric' }
  return sameDay
    ? start.toLocaleDateString(undefined, dateFmt)
    : `${start.toLocaleDateString(undefined, dateFmt)} – ${end.toLocaleDateString(undefined, dateFmt)}`
}

// Trips and standalone events each arrive pre-sorted from the server, but as
// two separate arrays — merged here into one true chronological timeline
// instead of "every trip, then every standalone event", so scrolling (and
// the date scrubber) actually reflects when things happened.
type TimelineEntry = { date: string } & (
  | { kind: 'trip'; trip: TripGroupDto }
  | { kind: 'event'; event: EventDto }
)

function buildTimeline(library: LibraryDto): TimelineEntry[] {
  const entries: TimelineEntry[] = [
    ...library.trips.map((trip): TimelineEntry => ({ kind: 'trip', date: trip.startDate, trip })),
    ...library.standaloneEvents.map((event): TimelineEntry => ({ kind: 'event', date: event.startAt, event })),
  ]
  // Real date parsing, not a raw string compare: the server serializes some
  // DateTimes with a UTC offset suffix (e.g. Apple's location-aware video
  // metadata) and others without (plain Exif-derived local times), so two
  // otherwise-adjacent dates can have differently-shaped ISO strings —
  // confirmed live as a freshly-created singleton Event (via "move to a new
  // Event") landing several *weeks* out of order in the timeline, since
  // localeCompare has no idea "20:28:37" (no offset) and "18:15:03+02:00"
  // don't compare the way their actual instants do.
  entries.sort((a, b) => new Date(a.date).getTime() - new Date(b.date).getTime())
  return entries
}

function buildBlocks(library: LibraryDto, columnCount: number): Block[] {
  const blocks: Block[] = []

  function pushEvent(event: EventDto, closesTripAbove: boolean) {
    blocks.push({ type: 'event-header', key: `eh-${event.id}`, event, closesTripAbove })
    for (let i = 0; i < event.items.length; i += columnCount) {
      blocks.push({
        type: 'thumb-row',
        key: `tr-${event.id}-${i}`,
        event,
        items: event.items.slice(i, i + columnCount),
        startIndex: i,
      })
    }
  }

  const timeline = buildTimeline(library)
  timeline.forEach((entry, i) => {
    if (entry.kind === 'trip') {
      blocks.push({ type: 'trip-header', key: `th-${entry.trip.id}`, trip: entry.trip })
      entry.trip.events.forEach((event) => pushEvent(event, false))
    } else {
      // The same accent top-border a Trip header itself gets, but on the
      // standalone Event right after one ends — otherwise the line only ever
      // marked entering a Trip, never leaving it (confirmed live as
      // inconsistent/asymmetric).
      const closesTripAbove = i > 0 && timeline[i - 1].kind === 'trip'
      pushEvent(entry.event, closesTripAbove)
    }
  })

  return blocks
}

function blockDate(block: Block): string | null {
  if (block.type === 'trip-header') return block.trip.startDate
  if (block.type === 'event-header') return block.event.startAt
  return block.event.startAt
}

// heightFraction is cumulative rendered height *before* each marker's block,
// as a fraction of the grid's total height — the same quantity a native
// scrollbar's thumb position represents, so the two agree with each other.
// (Confirmed live this needed fixing: marker positions used to come from
// counting distinct months instead, which only matches real proportional
// height when every month happens to have the same number of photos.)
// scrollableRange is (total content height - viewport height) — the same
// quantity a native scrollbar's thumb position is computed against
// (thumbFraction = scrollTop / scrollableRange, never / totalHeight). Using
// plain totalHeight here looked right in isolation (a click landed exactly
// on the real first item of a year) but confirmed live as still wrong in
// context: the native scrollbar's own handle sat measurably away from where
// that same marker's label was drawn, since the two were computed against
// different denominators. Content within the last one viewport's worth of
// height can never have a distinct scrollbar position anyway (you're
// already at max scroll once any of it is visible), so its heightFraction
// is clamped to 1 rather than allowed past it.
// Where to jump for the YearJumpBar's "Before" button — the first (in scroll
// order) block belonging to a ".Before"-recognized Event/Trip (see
// FolderTitleParser.IsBeforeDigitalScansFolder), i.e. undated/mis-dated scans
// that don't have a trustworthy year of their own to put on a year button.
function findBeforeDigitalScansFraction(blocks: Block[], rowHeight: number, scrollableRange: number): number | null {
  let cumulativeHeight = 0
  for (const block of blocks) {
    const needsDateReview =
      (block.type === 'trip-header' && block.trip.needsDateReview) ||
      (block.type === 'event-header' && block.event.needsDateReview)
    if (needsDateReview) {
      return scrollableRange > 0 ? Math.min(1, cumulativeHeight / scrollableRange) : 0
    }
    cumulativeHeight += blockHeight(block, rowHeight)
  }
  return null
}

function buildDateMarkers(blocks: Block[], rowHeight: number, scrollableRange: number): DateMarker[] {
  const markers: DateMarker[] = []
  let lastMonthKey = ''
  let lastYear = -1
  let cumulativeHeight = 0

  blocks.forEach((block) => {
    const dateStr = blockDate(block)
    if (dateStr) {
      const date = new Date(dateStr)
      const monthKey = `${date.getFullYear()}-${date.getMonth()}`
      if (monthKey !== lastMonthKey) {
        lastMonthKey = monthKey

        const isMajor = date.getFullYear() !== lastYear
        if (isMajor) lastYear = date.getFullYear()

        markers.push({
          heightFraction: scrollableRange > 0 ? Math.min(1, cumulativeHeight / scrollableRange) : 0,
          label: String(date.getFullYear()),
          tooltipLabel: date.toLocaleDateString(undefined, { year: 'numeric', month: 'short' }),
          isMajor,
        })
      }
    }

    cumulativeHeight += blockHeight(block, rowHeight)
  })

  return markers
}

export function LibraryGrid({
  library,
  libraryPath,
  favoriteCategories,
  scanning,
  onDelete,
  onTakenAtChanged,
  onResetTakenAt,
  onRotated,
  onRenameEvent,
  onRenameTrip,
  onMoveMediaItemToEvent,
  onCreateTripGroup,
  onUngroupTripGroup,
  onMergeEventsInto,
  onRemoveEventsFromTrip,
  onSetFavoriteCategory,
  onFixEventDates,
}: {
  library: LibraryDto
  libraryPath: string
  favoriteCategories: FavoriteCategoryDto[]
  /** A scan is currently running against this same library — an empty grid means "not indexed yet", not "genuinely empty", so the message below needs to say that instead. */
  scanning: boolean
  onDelete: (mediaItemId: number) => void
  onTakenAtChanged: (mediaItemId: number, takenAt: string) => void
  onResetTakenAt: (mediaItemId: number) => Promise<void>
  onRotated: (mediaItemId: number) => void
  onRenameEvent: (eventId: number, title: string) => void
  onRenameTrip: (tripGroupId: number, title: string) => void
  onMoveMediaItemToEvent: (mediaItemId: number, targetEventId: number | null) => void
  onCreateTripGroup: (eventIds: number[]) => void
  onUngroupTripGroup: (tripGroupId: number) => void
  onMergeEventsInto: (sourceEventIds: number[], targetEventId: number) => void
  onRemoveEventsFromTrip: (eventIds: number[]) => void
  onSetFavoriteCategory: (mediaItemId: number, tagId: number | null) => void
  /** Bulk date-fix for undated/mis-dated scans — see LibraryRepository.SetTakenAtForEvents. */
  onFixEventDates: (eventIds: number[], takenAt: string) => void
}) {
  const scrollRef = useRef<HTMLDivElement>(null)
  // null = not yet measured. Deliberately never guess a fallback column
  // count here anymore: a guessed value (previously 4) could leak through
  // as visibly huge, pixelated thumbnails whenever a race meant no
  // subsequent resize ever corrected it — reported as intermittent and
  // fixed by a full page reload, both consistent with a timing race rather
  // than a permanently wrong calculation. Waiting for a real, confirmed
  // ResizeObserver measurement before rendering any thumbnails at all
  // removes the wrong state entirely rather than needing to find and patch
  // every path that could produce it.
  const [containerWidth, setContainerWidth] = useState<number | null>(null)
  // The scroll container's own viewport height — needed for the date
  // scrubber's markers/jump math below (buildDateMarkers, onJump) to match
  // the native scrollbar's own thumb-position convention: a thumb's
  // position is scrollTop / (scrollHeight - clientHeight), not
  // scrollTop / scrollHeight. Confirmed live as a real, reproducible bug
  // from omitting the clientHeight term: even with the height-proportional
  // marker math already exactly correct, the native scrollbar's handle sat
  // measurably away from where a marker's label was drawn, since the two
  // were being computed against two different denominators.
  const [containerHeight, setContainerHeight] = useState<number | null>(null)
  // The native scrollbar is hidden entirely now (App.css) — DateScrubber
  // renders its own thumb instead. Three separate attempts to make a
  // *native* scrollbar's thumb line up with this rail's labels (matching
  // its proportional math, then its exact denominator, then its arrow-
  // button insets) each looked correct in isolation yet was still reported
  // as visibly misaligned — the DOM simply can't query a native scrollbar's
  // exact pixel geometry to guarantee agreement with it. A custom thumb
  // computed from the same numbers as the labels can't help but agree.
  //
  // Its position is written directly to this ref's DOM node on every scroll
  // event (see the effect below), not through React state — reported live
  // as "jumps in big steps" when it briefly went through a scrollTop state
  // value instead: that meant a full re-render of this whole heavy
  // virtualized grid on every single scroll event, the same class of
  // problem hoveredPhotoRef/requestedIdsRef below already exist to avoid.
  const scrubberThumbRef = useRef<HTMLDivElement>(null)
  // Owned here (not inside DateScrubber) so its real rendered height can be
  // read directly for the thumb's pixel-based transform below — see that
  // prop's own doc comment in DateScrubber.tsx for why a separately-measured
  // "container height" state wasn't the same reference frame.
  const scrubberRailRef = useRef<HTMLDivElement>(null)
  // Deliberately just an eventId (not a frozen items snapshot) — the items
  // themselves are looked up fresh from `library` on every render below, so
  // a mutation made from inside the open Lightbox (favorite, rotate, date
  // edit) is reflected immediately instead of the Lightbox going on showing
  // stale data (confirmed live as a real bug: the favorite star didn't
  // update after picking a category, since favoriteCategoryId lived only
  // on the old snapshot's item objects) until closed and reopened.
  const [lightbox, setLightbox] = useState<{ eventId: number; index: number } | null>(null)
  const [slideshow, setSlideshow] = useState<{ items: MediaItemDto[]; index: number } | null>(null)
  // A ref, not state — this changes on every mouse move between thumbnails,
  // and none of that needs to trigger a re-render of the (potentially huge)
  // virtualized grid; a keydown handler only ever needs its *current* value
  // at the moment L/R is actually pressed.
  const hoveredPhotoRef = useRef<MediaItemDto | null>(null)
  const [, forceRerenderForRotate] = useState(0)
  // Tracks every item whose <img> has ever actually been mounted with a real
  // src — NOT reset on scroll. virtualizer.isScrolling is a blanket flag for
  // "a scroll is happening right now" with no idea which tiles are already
  // loaded vs. genuinely new; suspending every tile by that flag alone made
  // already-visible, already-loaded thumbnails flicker out and back in on
  // even a one-row scroll, since they'd suspend and then immediately
  // re-request. Consulting this set lets a tile that's already been
  // requested once just keep rendering through any later isScrolling spans,
  // so only tiles that have genuinely never been requested before suspend.
  const requestedIdsRef = useRef<Set<number>>(new Set())
  const use24Hour = useUse24HourTime()
  const thumbMinWidth = useThumbMinWidth()
  // Checkboxes on standalone Event headers (never on ones already inside a
  // Trip — ungrouping the whole Trip is the way to change those) feed this,
  // and a floating action bar appears once it's non-empty. Plain component
  // state, not a ref: unlike the high-frequency refs above, this only
  // changes on an explicit click and a handful of headers re-rendering in
  // response is completely fine.
  const [selectedEventIds, setSelectedEventIds] = useState<Set<number>>(new Set())
  // "Fix dates…" in the selection bar below — a folder of undated/mis-dated
  // scans (e.g. a legacy "pre-1998" album) needs every member set to one
  // approximate date at once; one-by-one via the Lightbox isn't realistic
  // for hundreds of scans. See LibraryRepository.SetTakenAtForEvents.
  const [fixingDatesEventIds, setFixingDatesEventIds] = useState<number[] | null>(null)
  const [fixDateDraft, setFixDateDraft] = useState('')
  function toggleEventSelected(eventId: number) {
    setSelectedEventIds((current) => {
      const next = new Set(current)
      if (next.has(eventId)) next.delete(eventId)
      else next.add(eventId)
      return next
    })
  }
  // Backs the shared "pick an Event" popup for both a single photo's own
  // Move icon (grid tile, item mode) and the selection bar's "Merge into
  // another Event" action (event mode — merges every checked Event into
  // whichever target gets picked, so it works the same whether one or
  // several are checked; "Group into Trip" is offered alongside it once 2+
  // are checked, since that action alone needs more than one source).
  const [moveTarget, setMoveTarget] = useState<
    | { kind: 'item'; item: MediaItemDto; eventId: number }
    | { kind: 'event'; eventIds: number[]; referenceStartAt: string }
    | null
  >(null)
  const [moveSearch, setMoveSearch] = useState('')
  function closeMovePicker() {
    setMoveTarget(null)
    setMoveSearch('')
  }
  // The star button's popup — only opened when there's more than one
  // favorite category to choose from (see handleStarClick); a single
  // category just toggles directly, no picker needed.
  const [favoritePickerItem, setFavoritePickerItem] = useState<MediaItemDto | null>(null)
  function handleStarClick(item: MediaItemDto) {
    if (favoriteCategories.length === 1) {
      const only = favoriteCategories[0]
      onSetFavoriteCategory(item.id, item.favoriteCategoryId === only.id ? null : only.id)
      return
    }
    setFavoritePickerItem(item)
  }
  // Flattened once per library load for the Lightbox's "Move to Event"
  // picker — it needs every Event's id/title/date range regardless of
  // whether it's standalone or nested under a Trip, but never the
  // (potentially huge) per-event item lists that come with them.
  const allEventSummaries = useMemo(
    () => [
      ...library.standaloneEvents,
      ...library.trips.flatMap((t) => t.events),
    ].map((e) => ({
      id: e.id,
      title: e.title,
      startAt: e.startAt,
      endAt: e.endAt,
      tripGroupId: e.tripGroupId,
    })),
    [library],
  )

  // The Lightbox's own items, looked up fresh by eventId every render — see
  // the `lightbox` state's own comment for why this isn't just a snapshot.
  const eventsById = useMemo(
    () => new Map([...library.standaloneEvents, ...library.trips.flatMap((t) => t.events)].map((e) => [e.id, e])),
    [library],
  )
  const lightboxItems = lightbox ? (eventsById.get(lightbox.eventId)?.items ?? []) : []

  // Lets you rotate a photo by hovering its grid thumbnail and pressing
  // L/R, without opening the fullscreen viewer first — same non-destructive
  // Edit-row mechanism as the Lightbox toolbar (rotateMediaItem never
  // touches the original file).
  useEffect(() => {
    async function onKeyDown(e: KeyboardEvent) {
      if (e.target instanceof HTMLInputElement || e.target instanceof HTMLTextAreaElement) return
      if (e.key !== 'l' && e.key !== 'L' && e.key !== 'r' && e.key !== 'R') return

      const hovered = hoveredPhotoRef.current
      if (!hovered) return

      await rotateMediaItem(libraryPath, hovered.id, e.key === 'r' || e.key === 'R' ? 90 : -90)
      bumpImageVersion(hovered.id)
      forceRerenderForRotate((n) => n + 1)
    }
    window.addEventListener('keydown', onKeyDown)
    return () => window.removeEventListener('keydown', onKeyDown)
  }, [libraryPath])

  useLayoutEffect(() => {
    const el = scrollRef.current
    if (!el) return
    // No synchronous getBoundingClientRect read here on purpose (there
    // used to be one, to avoid a one-frame delay): it returns the
    // border-box, which — unlike ResizeObserver's contentRect — does NOT
    // exclude a scrollbar's width, only padding. Manually subtracting just
    // the known padding still left it wrong by the scrollbar's width
    // whenever one was actually present, which is most of the time for
    // this view. ResizeObserver's own first callback is the only
    // measurement used now, since it's the one that's actually guaranteed
    // correct (content-box, padding and scrollbar both already excluded).
    const observer = new ResizeObserver((entries) => {
      setContainerWidth(entries[0].contentRect.width)
      setContainerHeight(entries[0].contentRect.height)
    })
    observer.observe(el)
    return () => observer.disconnect()
  }, [])

  // Kept in sync with .date-scrubber's own CSS width (App.css) — the gutter
  // this view reserves so thumbnails never render underneath the scrubber
  // rail. No native scrollbar to additionally avoid overlapping anymore —
  // it's hidden entirely (App.css) in favor of the rail's own thumb.
  const DATE_SCRUBBER_WIDTH = 16
  const scrollGutter = DATE_SCRUBBER_WIDTH

  const columnCount = containerWidth !== null ? Math.max(1, Math.floor((containerWidth + GAP) / (thumbMinWidth + GAP))) : 1
  const thumbSize = containerWidth !== null ? (containerWidth - (columnCount - 1) * GAP) / columnCount : thumbMinWidth
  const rowHeight = thumbSize + GAP

  const blocks = useMemo(() => buildBlocks(library, columnCount), [library, columnCount])
  // Computed independently of the virtualizer's own getTotalSize() (even
  // though the two are numerically identical, confirmed live) so this and
  // the scrubber's onJump wiring below share one unambiguous source for the
  // same quantity, rather than each recomputing it its own way.
  const totalContentHeight = useMemo(
    () => blocks.reduce((sum, block) => sum + blockHeight(block, rowHeight), 0),
    [blocks, rowHeight],
  )
  const scrollableRange = containerHeight !== null ? Math.max(0, totalContentHeight - containerHeight) : totalContentHeight
  const dateMarkers = useMemo(
    () => buildDateMarkers(blocks, rowHeight, scrollableRange),
    [blocks, rowHeight, scrollableRange],
  )
  const beforeDigitalScansFraction = useMemo(
    () => findBeforeDigitalScansFraction(blocks, rowHeight, scrollableRange),
    [blocks, rowHeight, scrollableRange],
  )
  const scrubberThumbSizeFraction =
    totalContentHeight > 0 && containerHeight !== null ? Math.min(1, containerHeight / totalContentHeight) : 1
  const scrubberTravelFraction = Math.max(0, 1 - scrubberThumbSizeFraction)

  // Drives DateScrubber's custom thumb position directly via its DOM node
  // (scrubberThumbRef), not React state — see that ref's own declaration
  // for why. Re-synced whenever scrollableRange/scrubberTravelFraction
  // themselves change (a resize, a thumbnail-size change, content loading
  // in), not just on every scroll event, so the thumb never sits stale
  // relative to a range that just changed out from under it.
  //
  // Writes transform, never top — still reported as "not smooth, jumps in
  // big steps" even after moving this off React state: top is a layout-
  // triggering property, forcing a layout recompute on every single scroll
  // event, right when the virtualized grid's own much heavier per-scroll
  // DOM churn is already competing for that same main-thread time.
  // transform is compositor-only. Reads the rail's own real height on each
  // call (via scrubberRailRef, not the separately-measured containerHeight
  // state) — confirmed live as a real bug when containerHeight was used
  // instead: it's the *scroll element's* content-box height, excluding that
  // element's own padding, which isn't the same box every label's
  // percentage-based position is relative to.
  useEffect(() => {
    const el = scrollRef.current
    if (!el) return
    function updateThumbPosition() {
      const thumb = scrubberThumbRef.current
      const rail = scrubberRailRef.current
      if (!el || !thumb || !rail) return
      const fraction = scrollableRange > 0 ? Math.min(1, el.scrollTop / scrollableRange) : 0
      const pixelOffset = fraction * scrubberTravelFraction * rail.getBoundingClientRect().height
      thumb.style.transform = `translateY(${pixelOffset}px)`
    }
    updateThumbPosition()
    el.addEventListener('scroll', updateThumbPosition, { passive: true })
    return () => el.removeEventListener('scroll', updateThumbPosition)
  }, [scrollableRange, scrubberTravelFraction])

  const virtualizer = useVirtualizer({
    count: blocks.length,
    getScrollElement: () => scrollRef.current,
    // Without this, TanStack Virtual's internal size-measurement cache is
    // keyed by raw array index. `blocks` gets rebuilt (different length/
    // shape) whenever `library` changes — e.g. after a delete, which shifts
    // every later row's item-chunking by one — or when `columnCount` first
    // settles from its initial guess to the real measured value. A stale
    // index-keyed cache then gets misapplied to a structurally different
    // block, corrupting both getTotalSize() (the "list ends early" bug) and
    // individual row positioning (the overlapping/offset-labels bug).
    // Keying by the block's own stable id fixes both.
    getItemKey: (index) => blocks[index].key,
    estimateSize: (index) => blockHeight(blocks[index], rowHeight),
    overscan: 6,
  })

  // The scroll container itself must always render, at the same JSX root
  // shape, on every branch below (so React never unmounts/remounts it —
  // confirmed live: a branch that returned a differently-typed root swapped
  // the scrollRef div for a brand new DOM node when the branch changed,
  // silently orphaning the ResizeObserver's mount-only subscription on the
  // old, now-detached node and permanently freezing containerWidth at
  // whatever it read last, corrupting the column count). Only the content
  // *inside* the wrapper differs per branch.
  if (containerWidth === null) {
    return (
      <>
        <YearJumpBar
          markers={dateMarkers}
          beforeDigitalScansFraction={beforeDigitalScansFraction}
          onJump={(fraction) => virtualizer.scrollToOffset(fraction * scrollableRange, { align: 'start' })}
        />
        <div className="library-scroll-wrapper">
          <div
            className="library-scroll library-scroll-with-scrubber"
            ref={scrollRef}
            tabIndex={0}
            style={{ paddingRight: scrollGutter }}
          />
        </div>
      </>
    )
  }

  if (blocks.length === 0) {
    return (
      <>
        <YearJumpBar
          markers={dateMarkers}
          beforeDigitalScansFraction={beforeDigitalScansFraction}
          onJump={(fraction) => virtualizer.scrollToOffset(fraction * scrollableRange, { align: 'start' })}
        />
        <div className="library-scroll-wrapper">
          <div
            className="library-scroll library-scroll-with-scrubber"
            ref={scrollRef}
            tabIndex={0}
            style={{ paddingRight: scrollGutter }}
          >
            <p>
              {scanning
                ? 'Scanning your library — this can take a while for a large folder, especially the first time.'
                : 'No media found in this folder.'}
            </p>
          </div>
        </div>
      </>
    )
  }

  return (
    <>
      <YearJumpBar
        markers={dateMarkers}
        beforeDigitalScansFraction={beforeDigitalScansFraction}
        onJump={(fraction) => virtualizer.scrollToOffset(fraction * scrollableRange, { align: 'start' })}
      />
      <div className="library-scroll-wrapper">
      <div
        className="library-scroll library-scroll-with-scrubber"
        ref={scrollRef}
        tabIndex={0}
        style={{ paddingRight: scrollGutter }}
      >
      <div style={{ height: virtualizer.getTotalSize(), position: 'relative', width: '100%' }}>
        {virtualizer.getVirtualItems().map((virtualRow) => {
          const block = blocks[virtualRow.index]
          return (
            <div
              key={block.key}
              data-index={virtualRow.index}
              // No measureElement for ANY block type now, including headers
              // (previously still remeasured — see EVENT_HEADER_HEIGHT etc.
              // above for why that was the scrollbar drag-handle bug's real
              // remaining cause). getTotalSize() is now fixed the moment
              // blocks are built and never changes during a scroll session.
              style={{
                position: 'absolute',
                top: 0,
                left: 0,
                width: '100%',
                transform: `translateY(${virtualRow.start}px)`,
              }}
            >
              {block.type === 'trip-header' && (
                <header className="trip-header-block">
                  <div className="trip-header-row">
                    <h2>
                      <EditableTitle
                        title={block.trip.title}
                        onRename={(title) => onRenameTrip(block.trip.id, title)}
                      />
                    </h2>
                    <span className="trip-meta">
                      {formatRange(block.trip.startDate, block.trip.endDate)}
                      {block.trip.namingMode === 'Adopted' && <span className="badge">Adopted</span>}
                      {block.trip.needsDateReview && <span className="badge badge--warn">Needs dates fixed</span>}
                      {block.trip.location && <> &middot; {block.trip.location}</>}
                    </span>
                    <button
                      type="button"
                      className="slideshow-start-btn"
                      title="Slideshow of this whole Trip"
                      aria-label="Slideshow"
                      onClick={() => setSlideshow({ items: block.trip.events.flatMap((e) => e.items), index: 0 })}
                    >
                      <svg viewBox="0 0 24 24" width="14" height="14" fill="currentColor"><path d="M8 5v14l11-7z" /></svg>
                      Slideshow
                    </button>
                    <button
                      type="button"
                      className="trip-ungroup-btn"
                      title="Ungroup — its Events become standalone again (no file is moved)"
                      onClick={() => onUngroupTripGroup(block.trip.id)}
                    >
                      Ungroup
                    </button>
                    {block.trip.proposedFolderPath && (
                      <p className="proposed-path" title={`Will move to: ${block.trip.proposedFolderPath}`}>
                        Will move to: {block.trip.proposedFolderPath}
                      </p>
                    )}
                  </div>
                </header>
              )}

              {block.type === 'event-header' && (
                <header className={`event-header ${block.closesTripAbove ? 'event-header-after-trip' : ''}`}>
                  <div className="event-header-row">
                    <input
                      type="checkbox"
                      className="event-select-checkbox"
                      title="Select for Trip grouping, merging, or removing from a Trip"
                      checked={selectedEventIds.has(block.event.id)}
                      onChange={() => toggleEventSelected(block.event.id)}
                    />
                    <h3>
                      <EditableTitle
                        title={block.event.title}
                        onRename={(title) => onRenameEvent(block.event.id, title)}
                      />
                    </h3>
                    <span className="event-meta">
                      {formatRange(block.event.startAt, block.event.endAt)} &middot;{' '}
                      {block.event.items.length} items
                      {block.event.namingMode === 'Adopted' && <span className="badge">Adopted</span>}
                      {block.event.needsDateReview && <span className="badge badge--warn">Needs dates fixed</span>}
                      {block.event.location && <> &middot; {block.event.location}</>}
                    </span>
                    <button
                      type="button"
                      className="slideshow-start-btn"
                      title="Slideshow of this Event"
                      aria-label="Slideshow"
                      onClick={() => setSlideshow({ items: block.event.items, index: 0 })}
                    >
                      <svg viewBox="0 0 24 24" width="14" height="14" fill="currentColor"><path d="M8 5v14l11-7z" /></svg>
                      Slideshow
                    </button>
                    {block.event.proposedFolderPath && (
                      <p className="proposed-path" title={`Will move to: ${block.event.proposedFolderPath}`}>
                        Will move to: {block.event.proposedFolderPath}
                      </p>
                    )}
                  </div>
                </header>
              )}

              {block.type === 'thumb-row' && (
                <div className="thumb-grid" style={{ gridTemplateColumns: `repeat(${columnCount}, 1fr)` }}>
                  {block.items.map((item, i) => (
                    <ThumbTile
                      key={item.id}
                      item={item}
                      libraryPath={libraryPath}
                      suspendLoad={virtualizer.isScrolling}
                      requestedIdsRef={requestedIdsRef}
                      use24Hour={use24Hour}
                      onOpen={() => setLightbox({ index: block.startIndex + i, eventId: block.event.id })}
                      onDelete={onDelete}
                      onMove={() => setMoveTarget({ kind: 'item', item, eventId: block.event.id })}
                      onHoverChange={(hovering) => {
                        hoveredPhotoRef.current = hovering && item.kind === 'Photo' ? item : null
                      }}
                      showFavoriteButton={favoriteCategories.length > 0}
                      onFavoriteClick={() => handleStarClick(item)}
                      onFixOutlierDate={
                        item.isDateOutlier ? () => onTakenAtChanged(item.id, block.event.startAt) : undefined
                      }
                    />
                  ))}
                </div>
              )}
            </div>
          )
        })}
      </div>
      </div>

      <DateScrubber
        markers={dateMarkers}
        onJump={(fraction) => virtualizer.scrollToOffset(fraction * scrollableRange, { align: 'start' })}
        thumbSizeFraction={scrubberThumbSizeFraction}
        thumbRef={scrubberThumbRef}
        railRef={scrubberRailRef}
      />

      {selectedEventIds.size > 0 && (
        <div className="trip-select-bar">
          <span>{selectedEventIds.size} event(s) selected</span>
          <button
            type="button"
            onClick={() => {
              const ids = [...selectedEventIds]
              // Real date parsing (see buildTimeline's identical fix) — a
              // raw string compare can misorder two ISO dates whose UTC-offset
              // suffix presence differs.
              const earliest = ids
                .map((id) => allEventSummaries.find((e) => e.id === id))
                .filter((e): e is EventSummary => e !== undefined)
                .sort((a, b) => new Date(a.startAt).getTime() - new Date(b.startAt).getTime())[0]
              if (earliest) setMoveTarget({ kind: 'event', eventIds: ids, referenceStartAt: earliest.startAt })
            }}
          >
            Merge into another Event
          </button>
          {selectedEventIds.size >= 2 &&
            [...selectedEventIds].every((id) => allEventSummaries.find((e) => e.id === id)?.tripGroupId == null) && (
              <button
                type="button"
                onClick={() => {
                  onCreateTripGroup([...selectedEventIds])
                  setSelectedEventIds(new Set())
                }}
              >
                Group into Trip
              </button>
            )}
          {[...selectedEventIds].some((id) => allEventSummaries.find((e) => e.id === id)?.tripGroupId != null) && (
            <button
              type="button"
              onClick={() => {
                const idsInATrip = [...selectedEventIds].filter(
                  (id) => allEventSummaries.find((e) => e.id === id)?.tripGroupId != null,
                )
                onRemoveEventsFromTrip(idsInATrip)
                setSelectedEventIds(new Set())
              }}
            >
              Remove from Trip
            </button>
          )}
          <button
            type="button"
            title="Set every photo in the selected Event(s) to the same date/time — for a folder of undated or mis-dated scans"
            onClick={() => {
              const ids = [...selectedEventIds]
              // A folder matching the standard ".Pre" convention (the
              // "Needs dates fixed" badge) has no reliable date of its own
              // to start from — prefill with the day before the earliest
              // reliable date found anywhere in the library, a safe
              // "definitely before this" anchor, instead of leaving the
              // field empty. Still fully editable either way.
              const allNeedDateReview = ids.every((id) => eventsById.get(id)?.needsDateReview)
              if (allNeedDateReview && library.earliestReliableTakenAt) {
                const dayBefore = new Date(library.earliestReliableTakenAt)
                dayBefore.setDate(dayBefore.getDate() - 1)
                setFixDateDraft(toDatetimeLocalValue(dayBefore.toISOString()))
              } else {
                setFixDateDraft('')
              }
              setFixingDatesEventIds(ids)
            }}
          >
            Fix dates…
          </button>
          <button type="button" onClick={() => setSelectedEventIds(new Set())}>
            Cancel
          </button>
        </div>
      )}

      {moveTarget && (
        <div className="event-picker-overlay" onClick={closeMovePicker}>
          <EventPicker
            allEvents={allEventSummaries}
            referenceTakenAt={moveTarget.kind === 'item' ? moveTarget.item.takenAt : moveTarget.referenceStartAt}
            excludeEventIds={moveTarget.kind === 'item' ? [moveTarget.eventId] : moveTarget.eventIds}
            allowNewEvent={moveTarget.kind === 'item'}
            search={moveSearch}
            onSearchChange={setMoveSearch}
            onPick={(targetEventId) => {
              if (moveTarget.kind === 'item') {
                onMoveMediaItemToEvent(moveTarget.item.id, targetEventId)
              } else if (targetEventId !== null) {
                onMergeEventsInto(moveTarget.eventIds, targetEventId)
                setSelectedEventIds(new Set())
              }
              closeMovePicker()
            }}
            onCancel={closeMovePicker}
          />
        </div>
      )}

      {fixingDatesEventIds && (
        <div className="event-picker-overlay" onClick={() => setFixingDatesEventIds(null)}>
          <div className="confirm-dialog" onClick={(e) => e.stopPropagation()}>
            <p>
              Set every photo/video in {fixingDatesEventIds.length === 1 ? 'this Event' : `these ${fixingDatesEventIds.length} Events`} to
              the same date and time — for a folder of undated or mis-dated scans, not a precise
              per-photo correction.
            </p>
            <input type="datetime-local" value={fixDateDraft} onChange={(e) => setFixDateDraft(e.target.value)} autoFocus />
            <div className="confirm-dialog-actions">
              <button type="button" className="modal-footer-cancel" onClick={() => setFixingDatesEventIds(null)}>
                Cancel
              </button>
              <button
                type="button"
                className="modal-footer-save"
                disabled={!fixDateDraft}
                onClick={() => {
                  onFixEventDates(fixingDatesEventIds, fixDateDraft)
                  setSelectedEventIds(new Set())
                  setFixingDatesEventIds(null)
                }}
              >
                Set dates
              </button>
            </div>
          </div>
        </div>
      )}

      {favoritePickerItem && (
        <div className="event-picker-overlay" onClick={() => setFavoritePickerItem(null)}>
          <FavoriteCategoryMenu
            categories={favoriteCategories}
            currentId={favoritePickerItem.favoriteCategoryId}
            onPick={(tagId) => {
              onSetFavoriteCategory(favoritePickerItem.id, tagId)
              setFavoritePickerItem(null)
            }}
            onCancel={() => setFavoritePickerItem(null)}
          />
        </div>
      )}

      {lightbox && (
        <Lightbox
          items={lightboxItems}
          index={lightbox.index}
          libraryPath={libraryPath}
          allEvents={allEventSummaries}
          currentEventId={lightbox.eventId}
          onClose={() => setLightbox(null)}
          onNavigate={(index) => setLightbox((current) => (current ? { ...current, index } : current))}
          onTakenAtChanged={onTakenAtChanged}
          onResetTakenAt={onResetTakenAt}
          onRotated={onRotated}
          onMoveToEvent={onMoveMediaItemToEvent}
          onStartSlideshow={() => {
            setSlideshow({ items: lightboxItems, index: lightbox.index })
            setLightbox(null)
          }}
          onDelete={onDelete}
          favoriteCategories={favoriteCategories}
          onSetFavoriteCategory={onSetFavoriteCategory}
        />
      )}

      {slideshow && (
        <Slideshow
          items={slideshow.items}
          startIndex={slideshow.index}
          libraryPath={libraryPath}
          onClose={() => setSlideshow(null)}
        />
      )}
    </div>
    </>
  )
}

// Deliberately stays a single line, matching the h2/h3's normal single-line
// height, whether displaying or editing — LibraryGrid's virtualizer no
// longer remeasures header blocks against reality after they render (see
// the getItemKey/estimateSize comment above), so a taller editing state
// here would silently reintroduce the scrollbar drag-handle desync bug that
// took four attempts to actually fix.
// A title lives freely in the database — no filesystem restrictions apply
// to it here at all. Whatever isn't safe in a real folder name gets
// silently sanitized server-side (Curato.Core.FolderNameSanitizer) at the
// one point it actually becomes one, rather than being rejected this early
// for a rule that doesn't even apply yet (a CuratoManaged Event/Trip might
// not have a folder at all until Organize runs). Empty is still rejected
// here since it's meaningless as a title, database or not.
function validateFolderName(title: string): string | null {
  if (!title) return 'Title cannot be empty.'
  return null
}

function EditableTitle({ title, onRename }: { title: string; onRename: (newTitle: string) => void }) {
  const [editing, setEditing] = useState(false)
  const [draft, setDraft] = useState(title)

  if (!editing) {
    return (
      <>
        <span className="editable-title-text" title={title}>
          {title}
        </span>
        <button
          type="button"
          className="title-rename-button"
          title="Rename"
          aria-label="Rename"
          onClick={() => {
            setDraft(title)
            setEditing(true)
          }}
        >
          <svg viewBox="0 0 24 24" width="13" height="13" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <path d="M17 3a2.828 2.828 0 1 1 4 4L7.5 20.5 2 22l1.5-5.5L17 3z" />
          </svg>
        </button>
      </>
    )
  }

  const trimmedDraft = draft.trim()
  const validationError = validateFolderName(trimmedDraft)

  function commit() {
    if (validationError) return // Save is already disabled; belt-and-suspenders against a stray Enter
    setEditing(false)
    if (trimmedDraft !== title) {
      onRename(trimmedDraft)
    }
  }

  return (
    <span className="title-edit" onClick={(e) => e.stopPropagation()}>
      <input
        value={draft}
        onChange={(e) => setDraft(e.target.value)}
        onKeyDown={(e) => {
          if (e.key === 'Enter') commit()
          if (e.key === 'Escape') setEditing(false)
        }}
        // Validity shown via border color + a native tooltip rather than a
        // separate error line — this input lives in a virtualized row whose
        // height is a hard-coded constant (see EVENT_HEADER_HEIGHT above);
        // adding a second line here would silently reintroduce the
        // scrollbar drag-handle desync bug that took real effort to fix.
        className={validationError ? 'title-edit-input-invalid' : undefined}
        title={validationError ?? undefined}
        autoFocus
      />
      <button type="button" onClick={commit} disabled={!!validationError} title={validationError ?? undefined}>
        Save
      </button>
      <button type="button" onClick={() => setEditing(false)}>
        Cancel
      </button>
    </span>
  )
}

function ThumbTile({
  item,
  libraryPath,
  suspendLoad,
  requestedIdsRef,
  use24Hour,
  onOpen,
  onDelete,
  onMove,
  onHoverChange,
  showFavoriteButton,
  onFavoriteClick,
  onFixOutlierDate,
}: {
  item: MediaItemDto
  libraryPath: string
  use24Hour: boolean
  // While the virtualizer reports active scrolling (a scrollbar drag across
  // a large distance fires a real DOM scroll event, and therefore a fresh
  // batch of newly-mounted row/image elements, on every pixel of travel —
  // not just once at the final resting position) every row passed through
  // along the way was mounting an <img> with a real src, so all of them
  // queued a thumbnail request. The browser's ~6-connections-per-origin cap
  // then had to drain that whole backlog of requests for rows the user
  // already scrolled past before it could even start on the rows actually
  // left on screen — confirmed live: an instant, non-dragged scrollTop jump
  // loads its thumbnails in under 40ms, but a real scrollbar drag left the
  // final rows waiting multiple seconds behind that backlog. Omitting `src`
  // while still scrolling (TanStack Virtual's own recommended pattern for
  // this) means only the row(s) still on screen once scrolling actually
  // stops ever ask for an image.
  suspendLoad: boolean
  // See LibraryGrid's declaration — this tile only actually honors
  // suspendLoad the first time it's ever asked to render, so a tile that
  // was already showing a loaded thumbnail before this scroll started never
  // flickers away just because *some* scroll is in progress.
  requestedIdsRef: React.RefObject<Set<number>>
  onOpen: () => void
  onDelete: (mediaItemId: number) => void
  onMove: () => void
  onHoverChange: (hovering: boolean) => void
  /** Hidden entirely (not just disabled) when zero favorite categories exist — nothing to assign. */
  showFavoriteButton: boolean
  onFavoriteClick: () => void
  /** Set only when item.isDateOutlier is true (EventDateOutlierDetector) — clicking snaps this one item's date to its own Event's date, for the classic "camera clock wasn't set" stray photo. Undefined (not just a no-op) hides the badge entirely for a normal tile. */
  onFixOutlierDate?: () => void
}) {
  // Deliberately reading/writing the ref synchronously during render rather
  // than in an effect: an effect only runs after the first paint, so an
  // already-seen tile would still flash blank for one frame before the
  // effect discovered that and re-rendered it with the real image — exactly
  // the flicker this exists to prevent. This project has no React Compiler
  // in its build, so there's no memoization to invalidate; the Set mutation
  // is idempotent (a re-render of the same tile just re-adds the same id).
  // oxlint-disable-next-line react/refs
  const alreadyRequested = requestedIdsRef.current.has(item.id)
  const shouldLoad = !suspendLoad || alreadyRequested
  // oxlint-disable-next-line react/refs
  if (shouldLoad) requestedIdsRef.current.add(item.id)

  return (
    <div
      className="thumb"
      title={`${item.relativePath}\n${formatTakenAt(item.takenAt, use24Hour)}`}
      onMouseEnter={() => onHoverChange(true)}
      onMouseLeave={() => onHoverChange(false)}
    >
      {/* No <img> at all while suspended, rather than one with no src —
          an <img> with a missing/empty src still renders Chrome's own
          broken-image glyph and border, which looked far worse than the
          plain-grey .thumb background it now falls back to showing
          instead (confirmed live: reported as an ugly bordered icon in
          the corner of every not-yet-loaded tile while scrolling). */}
      {shouldLoad && (
        <img
          src={thumbnailUrl(libraryPath, item.relativePath, item.id)}
          alt=""
          loading="lazy"
          onClick={onOpen}
          onError={(e) => {
            e.currentTarget.replaceWith(
              Object.assign(document.createElement('div'), {
                className: 'thumb-fallback',
                // A video already shows its own play-icon badge as the "this
                // is a video" signal — "No preview" text underneath it just
                // looked cluttered, so only photos get the text explanation.
                textContent: item.kind === 'Photo' ? 'No preview' : '',
              }),
            )
          }}
        />
      )}
      {item.kind === 'Video' && (
        <div className="video-play-badge" aria-hidden="true">
          <svg viewBox="0 0 24 24" width="32" height="32">
            <circle cx="12" cy="12" r="11" fill="rgba(0,0,0,0.55)" />
            <path d="M9.5 7.5v9l7-4.5z" fill="white" />
          </svg>
        </div>
      )}
      {item.liveVideoRelativePath && <span className="live-badge">LIVE</span>}
      <button
        type="button"
        className="thumb-move"
        title="Move to a different Event"
        onClick={(e) => {
          e.stopPropagation()
          onMove()
        }}
      >
        <svg viewBox="0 0 24 24" width="12" height="12" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round">
          <path d="M9 5l7 7-7 7" />
        </svg>
      </button>
      <button type="button" className="thumb-delete" title="Move to Recycle Bin" onClick={() => onDelete(item.id)}>
        <svg viewBox="0 0 24 24" width="12" height="12" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round">
          <path d="M3 6h18M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2m3 0-1 14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2L4 6h16z" />
        </svg>
      </button>
      {showFavoriteButton && (
        <button
          type="button"
          className={`thumb-favorite ${item.favoriteCategoryId !== null ? 'is-set' : ''}`}
          title="Favorite category"
          onClick={(e) => {
            e.stopPropagation()
            onFavoriteClick()
          }}
        >
          <svg
            viewBox="0 0 24 24"
            width="11"
            height="11"
            fill={item.favoriteCategoryId !== null ? 'currentColor' : 'none'}
            stroke="currentColor"
            strokeWidth="2.5"
            strokeLinejoin="round"
          >
            <path d="M12 .587l3.668 7.568 8.332 1.151-6.064 5.828 1.48 8.279-7.416-3.967-7.416 3.967 1.481-8.279-6.064-5.828 8.332-1.151z" />
          </svg>
        </button>
      )}
      {onFixOutlierDate && (
        <button
          type="button"
          className="thumb-date-outlier"
          title="This date looks wrong — it's way off from the rest of this Event (likely a camera clock issue). Click to set it to this Event's own date."
          onClick={(e) => {
            e.stopPropagation()
            onFixOutlierDate()
          }}
        >
          <svg viewBox="0 0 24 24" width="12" height="12" fill="none" stroke="currentColor" strokeWidth="3" strokeLinecap="round" strokeLinejoin="round">
            <circle cx="12" cy="12" r="9" />
            <line x1="12" y1="7.5" x2="12" y2="13" />
            <circle cx="12" cy="16.5" r="0.75" fill="currentColor" stroke="none" />
          </svg>
        </button>
      )}
    </div>
  )
}
