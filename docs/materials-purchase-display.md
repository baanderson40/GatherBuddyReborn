# Policy-driven Materials grouping

## Display behavior

Materials no longer has a separate **Prefer Vendors** checkbox. The window
resolves the selected list's global/disabled/custom purchase policy. A matching
retained active or paused run uses its immutable captured policy instead; edits
to saved settings apply only to the next run. Completed/cancelled/idle queues and
other lists do not supply a captured display policy.

The policy summary identifies **Next run** versus **Captured run**, policy origin,
and enabled state. This is presentation only: it never enables purchasing,
modifies policy, changes currency priority, resets spending or starts automation.

Eligible acquisition-material rows move to **Vendor** and receive `[Auto]`.
Mixed mandatory-HQ/NQ-compatible demand receives `[Auto partial]`; the candidate
claim applies only to its NQ-compatible demand. HQ-only rows cannot qualify through
the NQ offer adapter. Reference/precraft rows do not become purchase requirements.
Every row remains counted once, using existing quantities/retainer availability.

Without an eligible candidate, existing source classification is retained.
Vendor-only/Other rows may still appear in Vendor with no Auto marker even when
automatic purchasing is disabled: the panel is a source category, not permission
to spend. Existing manual vendor actions remain available under their own rules.
When automatic offer data is loading/unavailable, normal grouping is shown with
an explicit pending message rather than treating loading as confirmed no offer.

## Candidate meaning

Automatic candidates use the same read-only offer adapter and stackable-material
support filter as CraftingAcquisitionPort. The extracted support filter preserves
existing execution restrictions: supported gil/GC/validated gemstone routes,
no housing/equipment materials, known stack size greater than one and bundle
output fitting a stack. Existing adapter rejections, known inaccessible locations,
quest locks, unsupported exchange forms and wrong-GC offers remain blocked.

The acquisition planner's structural offer validator is also shared. All exchange
cost components must be authorized by the displayed policy, GC costs must belong
to the current GC, and a zero gil cap excludes gil-funded candidates. Currency
badges/tooltips follow policy priority. Unknown access still requires executor
verification; it is not claimed to be accessible.

**Candidate is not a guaranteed purchase or allocation.** No live balance/reserve/
budget allocation simulation is performed by Materials. Final balances, reserves,
cumulative spending, capacity, unlocks and live shop validation remain the executor's
responsibility. Sheet-backed candidate data is not live price authorization.
Purchases may fall back to gathering or shortage reporting at execution time.

Manual currency selections and totals do not change automatic policy or allocation.
Their tooltips explicitly describe manual totals; the existing selection dictionary
is not read by automatic candidate classification. No new saved shopping list,
transaction, reservation, allocation or vendor interaction is created by viewing
Materials. No recipe expansion or buy-instead-of-craft substitution is introduced.

## Caching and lifecycle

The Materials view cache uses a structural policy key, not settings-object identity:
list/run identity, origin, enabled state, allowed groups, priority, reserves and cap.
In-place policy edits invalidate previews; captured-run keys stay stable through
saved-policy edits. Existing material/retainer/display-control invalidation remains.

Vendor publication stamp, vendor-location revision/readiness, current GC currency,
character identity and player readiness also invalidate the cache. Unversioned
quest/access state is rechecked in batches at most every five seconds when the
cache is otherwise unchanged. Cache requests/offer scanning occur on rebuild, not
once per row/frame. Offer capture is deferred while player/screen readiness is
unavailable. No native shop interaction is used to produce this view.

## Verification

566 acquisition/source tests + 122 actual-plugin compatibility tests pass
(688 total, including the earlier Automatic Purchases layout regression check).
Coverage includes policy inheritance/overrides, captured-run isolation, stop/new-list
context, structural cache keys, currency priority, all-cost authorization, current-GC
filtering, gemstone eligibility, zero gil cap, quality limitations, reference rows,
loading versus absent offers, invalid metadata and shared execution-support rules.
Boundary checks cover checkbox removal, read-only context, common offer projection,
cache invalidation and absence of purchase-start/planning calls in the display path.
Forced Debug/Release builds and `git diff --check` pass; only the two existing
ElliCon/ElliLib nullable warnings remain.

Native acceptance pending:

- Toggle inherited global purchases and verify gatherable gil candidates move
  between Gather and Vendor automatically, without the removed checkbox.
- Test list Disabled and Custom modes, current-GC restrictions and gemstone toggles.
- Verify `[Auto]` explanations, partial-HQ wording, unchanged totals and manual actions.
- Edit settings during active/paused runs: matching list retains captured policy;
  another list uses its own policy; stopped/completed list returns to preview.
- Verify vendor-data loading recovery, login/GC changes and narrow-window layout.
- Confirm actual acquisition behavior, authorization, reserves and spending unchanged.

Automatic Purchases settings remain directly visible in the selected right panel;
this grouping feature does not add another settings collapse or purchase toggle.
