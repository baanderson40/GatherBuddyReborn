# Marketboard search and comparison

Marketboard searches all marketable game items locally. Search results rank exact
names, then prefixes, then other case-insensitive matches. Item selection (not
typing or opening Recent) requests Universalis prices. Results use virtualized item
rows and responsive side-by-side/stacked panels.

## Scope and quality

One dropdown contains:

- **Cross-DC:** Aether, Primal, Crystal, and Dynamis, combined into a North American
  comparison. Each DC has its own request/cache status.
- **Cross-World:** all worlds in the logged-in character's home DC.
- **Individual worlds:** public worlds in that home DC, alphabetically ordered.

Default is the character's home world. Scope uses stable IDs, not dropdown indices;
scope and Any/NQ/HQ quality preference persist in configuration. Scope survives item
changes. Character changes rebind Cross-World and replace invalid individual-world
choices with the new home world. Missing identity waits for login; there is no
assumed Aether fallback. Cross-DC always means the four NA DCs, even for a character
whose home region differs.

## Listings and freshness

The table sorts returned listings by unit price, with deterministic tie-breaking.
Columns show quality, unit price, quantity, 64-bit stack total, and world; Cross-DC
also shows DC. Each target requests up to 20 listings per quality for HQ-capable
items, or 20 listings total otherwise. These are **returned listings**, not a full
market inventory or guaranteed in-game availability.

- **Fetched:** when Vulcan received a successful response.
- **Latest source upload:** Universalis `lastUploadTime`, when supplied. This is
  the latest upload for the target, not a guarantee that every world/listing was
  updated at that time. HQ/NQ requests may have different source timestamps.
- Missing source timestamps are explicitly unavailable.

Cached responses are reusable for 15 minutes. Refresh bypasses cache freshness
without deleting existing data. Expired data and failed-refresh data are marked
stale; fetching continues to display previous listings. Empty listings, missing
data, pending requests, HTTP/parsing failures, and partial NA comparisons are
distinct. Retry failed requests only retries failed targets.

Background work is keyed by item and target; late responses cannot replace other
selections or restore forgotten cache entries. Duplicate pending requests are
coalesced. HTTP concurrency is capped at three, with bounded response bodies,
timeouts, and retries. Shutdown cancels and drains work before disposing HTTP
resources. A target is successful only after all required quality queries succeed.

## Recent and material shortcuts

Existing `mb_history.json` entries retain their `ItemId`, `Name`, and `IconId`
schema and ordering, capped at 100 items. Names/icons are refreshed from game data
when available. Clear History requires confirmation; individual removals remain
available through mouse/controller context menus. Unreadable history is preserved,
with a warning instead of overwriting the file. Successful history writes replace
the file through a same-directory temporary file.

Crafting Materials → right-click → Search Marketboard still opens/selects the
requested item, uses remembered scope, and records it in Recent. Non-marketable
items show an explanation and are never sent to Universalis. Purchase policy,
vendor shopping lists, crafting progress, and automation are unchanged. There is
no automatic purchase, sales-history, or profitability feature. Explicit listing
travel is described below; searching/refreshing never initiates travel.

## Listing travel through Lifestream

Left-click, right-click, or use the controller context menu on a listing row to
choose **Travel to marketboard on <world>**. Already on that world: **Go to
marketboard on <world>**. Item-search rows remain selection-only. Listing/world
identity is captured when the menu opens; the destination is resolved against
public game-world data rather than executing listing text as a command.

Choosing the travel menu action starts immediately; there is no confirmation
window. The menu retains Ul’dah destination, teleport-cost/no-purchase/no-return,
and cross-DC logout/queue warnings. Listing availability is not guaranteed.
Preconditions are rechecked on action selection. Requires Lifestream idle, ready player,
supported world, and no active GBR/Vulcan/vendor/collectable automation or AR run.
An open marketboard must be closed before requesting a different world.

Verified checked-out Lifestream contracts in `IPC/IPCProvider.cs`:

- `ChangeWorldById(uint) -> bool`, `CanVisitSameDC(string) -> bool`,
  `CanVisitCrossDC(string) -> bool`, and `IsBusy() -> bool`.
- `ExecuteCommand(string)` invokes the constant `mb` command. Its handler in
  `Lifestream.cs` calls `TaskMBShortcut`, whose static alias teleports/aethernets,
  walks to, and interacts with Ul’dah's marketboard (`Data/StaticAlias.cs`).
- Accepted world-change requests can still be rejected downstream. Neither a true
  result nor idle alone proves arrival. The marketboard command has no result or
  caller-provided operation ID. Installed IPC availability is checked at runtime.

Travel observes the original content ID/home-world identity and actual current
world across cross-DC logout/loading. After destination arrival, it requires 500 ms
stable readiness and Lifestream idle before submitting `mb` once. Already-open
marketboard on the destination skips the shortcut. Completion requires a visible
`ItemSearch` addon on the requested world/character and settled Lifestream. Native
addon/interaction acceptance remains pending.

Start acknowledgement has 15 seconds, world transfer 15 minutes, and marketboard
routing 5 minutes. Failure after possible submission retains the movement gate.
No purchase, item-search injection, return trip, or global Lifestream abort occurs.
**Stop follow-up** prevents subsequent stages but permits submitted Lifestream
work to continue; it releases only after original-character idle/readiness settles.
**Recover travel** explicitly releases failed requests after the same checks.
Wrong-character, missing-IPC, or conflicting-automation cases remain gated. Stop
settlement has a 5-minute observation deadline; timeouts never abort Lifestream.

The framework updates travel before other automation. Shared start guards cover
gather/fishing, queue starts/resumes, direct crafts, vendor navigation/purchases,
vendor lists, transient vendor ownership, and collectables. The shared AR
coordinator cannot initiate a handoff while travel holds the gate. Browsing and
HTTP requests remain available. Unload stops local observation, never external
travel. Terminal status is printed to chat even with Vulcan closed. Transition logs
use `[Marketboard Travel][request-id][world]` without per-frame logging.

AR conflict handling distinguishes startup from an owned trip. In checked-out
AutoRetainer source, `PluginState.IsBusy` forwards `Utils.IsBusy`, which includes
`Lifestream.IsBusy()` alongside AR tasks/GC hand-in. Aggregate AR busy is therefore
checked only before ownership; it is not queried/treated as an independent conflict
during owned travel or recovery. Actual AR Multi-mode and shared handoffs remain
explicit conflicts. This does not prove AR's internal task queue is idle during a
trip: public aggregate busy cannot distinguish it from Lifestream activity.

User logs at 21:10:59 showed Cactuar world travel submitted, followed by the old
false AR conflict 29 ms later. The ownership-aware policy and regression tests fix
that self-conflict. The excerpt is not evidence of successful arrival; native
retest of the corrected route remains pending.

## Verification

Managed tests link production search/scope/cache/HTTP sources and cover marketable
filtering, ranking, preferences, home/DC binding, character changes, four-DC scopes,
sorting/quality, stack totals, source timestamps, partial failures, retries,
cache reuse/expiry, failed refresh, duplicate and out-of-order responses, clearing,
cancellation, HTTP errors, response-size bounds, and concurrency. An actual-plugin
compatibility test verifies the unchanged legacy history DTO. No tests render
ImGui or execute native game functions.

Travel adds 39 linked-production policy/state-machine cases: rejected/busy/missing IPC,
same-world and already-open marketboard, cross-DC logout/loading, correct character
and world, stable readiness, no-op/throwing submissions, single-stage submission,
timeouts, stop-follow-up/draining, recovery, conflicts, AR's mirrored Lifestream busy
state, genuine Multi-mode conflicts, and throttled diagnostics.
Combined managed evidence: 404 acquisition/source tests and 54 actual-plugin
compatibility tests (458 total). Native start-guard and IPC/UI acceptance still
requires the controlled in-game checks below.

Read-only schema smoke check: Universalis `/api/v2/Aether/2?listings=1&entries=0`
returned `itemID`, `lastUploadTime`, and listings with unit price, quantity, quality,
and world name during implementation. This is not native plugin acceptance.

### Live travel evidence

User-provided logs from 2026-11-03 confirm:

- **Faerie / Aether**, request `93c45dd3-faaa-4188-ab52-0041384a9462`:
  world change submitted 21:17:18; destination observed 21:17:26; marketboard
  shortcut submitted 21:17:28; arrival/marketboard verified 21:17:37 (~19 seconds).
- **Seraph / Dynamis**, request `9a90d066-dd33-40cc-9370-f271308485d6`:
  world change submitted 21:18:48; destination observed after cross-DC transition
  21:20:40; shortcut submitted 21:20:54; arrival/marketboard verified 21:21:02
  (~2 minutes 15 seconds).

Both routes completed without the false AR aggregate-busy conflict and released
the movement gate. These excerpts verify normal same-DC/cross-DC travel and native
marketboard-open detection, not stop/recovery, browsing, or every UI interaction.
Repeated Legacy mode messages during logout/login are a separately planned
investigation; their cause is not established and they did not prevent arrival.

### In-game acceptance — partial

- [x] Normal same-DC and cross-DC travel to requested world and open marketboard
      (Faerie/Seraph evidence above).
- [x] Installed Lifestream world-change/marketboard IPC and native `ItemSearch`
      arrival detection on those routes.

- [ ] Search an item absent from history; verify no HTTP requests while typing.
- [ ] Confirm marketable-item coverage, icons, exact/prefix ranking, and empty states.
- [ ] Home world, Cross-World, Cross-DC dropdown and persistent quality/scope.
- [ ] Compare NA results/world/DC labels against Universalis; partial failure/retry.
- [ ] Fresh/stale/refresh-failure distinctions and source/fetch timestamps.
- [ ] Scope/item changes during requests never display another selection's data.
- [ ] Material shortcut, Recent filtering/removal, and confirmed Clear History.
- [ ] Existing history survives reload; malformed-history warning preserves file.
- [ ] Character relog/DC travel uses home identity and does not assume Aether.
- [ ] Narrow layouts, UI scaling, keyboard/gamepad selection, and context menus.
- [ ] Listing left/right/controller click opens menu; captured row survives refresh.
- [ ] Direct menu travel (no confirmation window): same world, same DC, different
      NA DC, already-open marketboard; cost/logout warnings visible in menu.
- [ ] Busy/missing Lifestream, unsupported destination, disabled DC travel, and no-op command.
- [ ] Wrong-character/world, timeout, stop-follow-up, and manual recovery behavior.
- [ ] Competing automation starts/AR handoffs are rejected without stopping unrelated work.
- [ ] Closing Vulcan does not cancel observation; no purchase or automatic return occurs.
