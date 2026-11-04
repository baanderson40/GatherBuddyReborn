# Gemstones and transaction-aware material acquisition

## Configuration

Vulcan → Settings → Automatic Vendor Purchases exposes **Allow Bicolor Gemstones**,
**Bicolor Gemstone reserve**, and currency-priority Up/Down controls. The same
settings are available in a crafting list's Custom purchase policy. Inherit and
Disabled modes retain their existing meaning.

Gemstones are not added to existing/default allowlists. Reserve defaults to zero;
an explicit zero survives serialization. Actual balances and the run's captured
policy authorize spending, not the materials window's selected currency. Mid-run
edits do not alter an active/resumed run. Retries retain confirmed spending.

## Exchange scope

The planner/accounting model supports one NQ output item, bundles, and multiple
cost components. Every component must be supported and explicitly allowed; all
component reserves, commitments, and applicable gil caps must permit the
transaction. Multiple slots for the same currency are summed.

Native gemstone execution currently verifies the source-backed **basic
ShopExchangeCurrency, one distinct currency** format. Multi-currency metadata is
retained but marked unsupported for integrated execution until a complete native
reader is established. ShopExchangeItem and inclusion-shop layouts also remain
blocked. This is an explicit native capability limit, not a restriction to buying
one item or reopening after each batch. Gil/current-GC execution is unchanged.

Ordinary payment items, other special currencies, unknown cost types, collectable
payments, HQ outputs, multiple distinct outputs, and unverified inclusion-shop
routes remain rejected. No recursive payment-item acquisition or crafting-stock
consumption is authorized. Other currencies are not implicitly enabled.

Whole transactions determine cost and output. Minimum unavoidable bundle overbuy
is always permitted without a prompt: shortage 7, output 5 → two transactions,
output 10. If only one transaction is affordable, purchase 5 and retain shortage 2.
Targets retain actual demand rather than rounded output, so later external inventory
changes reduce the required number of transactions. Mandatory HQ demand cannot be
fulfilled by NQ overbuy. Output must fit the supported material stack size.

The checked-out English SpecialShop CSV contained 487 candidate gemstone listings
with unit output during investigation (raw gemstone ID or mapped currency slot).
This is reference-data evidence, not a claim about every current/live listing.
Bundle/multiple-cost behavior therefore needs its own native acceptance where
real applicable listings exist.

## Identity and access

Acquisition discovery keeps original SpecialShop source indices separately from
legacy filtered purchase-row indices. Its separate offer collection does not
expose multi-cost listings to standalone purchasing's flattened cost model.

Before routing, known listing quest requirements and current-GC restrictions are
checked. Cached NPC positions do not prove unlock/rank access; otherwise access
remains Unknown. Before a direct special-shop callback, the intended NPC target,
live output item/count, and every live cost must match uniquely. Unknown, missing,
ambiguous, or mismatched rows fail closed. Confirmation also checks the selected
live row. Native row counts are bounded before reading cost arrays.

Live output comes from AgentShop and live price/currency from a source-backed
basic-currency addon contract. No event-handler cast or inferred AgentShop cost
array mapping is used for authorization. Disabled/locked shops that
cannot complete interaction terminate through existing bounded timeouts; no unlock
automation is performed.

The planner can authorize alternate vendors only for identical output, all costs,
primary currency, and shop type. A failed start or certain zero-output failure may
try such a vendor after cleanup, once per authorized route. It never changes price,
currency, or output as an implicit substitution. Uncertain/submitted transactions
do not trigger fallback/replay. Final reconciliation retains unresolved material
demand and permits unrelated acquisition/ready recipes to continue as before.
Shop-opening/route failures before submission are remembered for the session so
other materials do not repeatedly reopen the same unavailable shop. Listing-only
failures do not blacklist unrelated accessible items at that NPC. A new explicit
acquisition retry can reconsider the route.

## Batches and ownership

A request contains at most 99 exchange transactions, additionally bounded by item
stack capacity. Output count and native transaction count are distinct. The native
adapter converts output to transactions before selecting the direct special-shop
row. Each batch rechecks original metadata, live balances, every reserve/budget,
remaining inventory target, and conservative free-slot availability.

After certain settlement, compatible owned interactions remain open for the next
batch or item. Compatibility requires the same session, NPC, shop type, menu route,
shop ID, source shop ID, visible shop, and expected NPC target. This applies to gil,
GC seals, and gemstones. GC tabs can be reselected within the same shop. Different
source shops still require cleanup, even at the same NPC.

Pause/Stop, unsafe state, incompatible contexts, failures, and final completion
retain cleanup and lease-release requirements. Pause between retained batches also
closes the owned interaction. Stop cannot submit another batch. Saved shopping
lists and unrelated automation are not modified or cancelled.

Receipts reconcile output inventory with every cost component. Unmatched inventory
or currency changes stop further purchasing as accounting-uncertain rather than
guessing or repeating a transaction. Settled spending and outstanding commitments
remain distinct; later batches and retries must not double-subtract spending.

## Diagnostics

### Vendor-cache initialization regression and recovery

The first gemstone implementation used `FirstOrDefault` on sheet-backed Lumina
cost structs and then read `CurrencyCost` without establishing a match. A default
Lumina cost struct has no backing page: the compatibility regression reproduces
the resulting `NullReferenceException` without a game process. Nongemstone listings
could therefore prevent the entire vendor cache from initializing.

Discovery now iterates actual cost rows and returns a managed amount only on an
explicit gemstone match. Failure diagnostics include the full exception/stack,
initialization stage, and available source shop/row context. Identical failures and
DataShare readiness messages are throttled independently of the two-second retry
policy. Success logs `Vendor cache initialized` or `Vendor cache recovered`.

Completed offer collections and NPC IDs publish together after location-cache
scheduling succeeds. Failed builds do not advance revision or expose a partially
replaced offer generation as ready. Materials-window cache metadata now captures
vendor revision/readiness before building and invalidates when either changes,
including when a window remained open during initialization. The window shows a
single incomplete-vendor-data notice while initialization is pending/unavailable.
Informational currency options remain independent of purchase authorization.

Checked-out reference CSV evidence for the reported items:

| Item | Item ID | Source shop | Source row | Cost/output |
| --- | --- | --- | --- | --- |
| Tumbleclaw Weeds | 44071 | 1770736 | 13 | 3 gemstones / 1 item |
| Alexandrian Axe Beak Wing | 44072 | 1770736 | 14 | 3 gemstones / 1 item |

Additional listings exist in the reference data. No item IDs, prices, or icons were
hardcoded into production behavior. The user subsequently confirmed gemstone icons
for both material rows and the panel's 12-gemstone total. This verifies UI recovery,
not a completed integrated purchase.

- Existing `[Vulcan Acquisition]` P2/P3 traces retain session/allocation identity.
- `KeepShopOpen`: certain receipt; compatible owned context retained.
- `AlternativeVendor`: explicit planner-authorized, identical-cost/output fallback.
- Preview reports list transactions, output per transaction, and all expected costs.
- Vendor errors explain changed/unavailable live rows, missing quest access, budgets,
  capacity, or uncertain settlement. No per-frame diagnostic stream was added.

### Live exchange validation diagnostics — 2026-11-04

Run `42d807ea-0656-44af-a175-157d902ed3a5` planned two Tumbleclaw Weeds for six
gemstones, reached Kajeel Ja, and rejected live-row validation before purchase.
It recorded zero acquired/spent, cleaned up, and selected the planner-authorized
same-cost alternative Beryl. The user stopped during subsequent travel; the run
ended Cancelled with cleanup and certain zero spending. This is not evidence of
a successful gemstone transaction or proof that Kajeel Ja was locked.

`[Vendor exchange]` Debug messages now identify selection/confirmation phase,
session/allocation, visible addon, target/expected NPC, AgentShop availability,
receive/cost row counts, selected index, expected output/costs, and bounded observed
rows. Reasons distinguish target/addon/agent readiness, unsupported or incomplete
cost layout, missing item, output/cost mismatch, ambiguity, and selected-row mismatch.
No invalid cost array is read just to produce diagnostics; receive samples and
requested-item/cost observations are limited to four rows each.

Potentially incomplete selection snapshots receive one three-second wait beginning
at the first incomplete observation. Polls and reason changes cannot extend it.
Missing requested rows and zero output/cost fields receive the same bounded grace;
fully populated mismatches and ambiguous rows remain rejected. Waiting submits no
selection callback. Confirmation retains its existing timeout and uncertain-result
safeguards; no extra confirmation/replay is authorized.

Diagnostic admission is bounded to 64 session/allocation/phase keys and one Debug
snapshot per five seconds per key, even if observed reasons change every frame.
Final failure includes the latest detailed snapshot. Existing exact-cost, reserve,
inventory, selected-row, and native-array bounds are retained. The diagnostic/wait
patch did not by itself establish that native gemstone purchases succeeded.

### Source-backed basic currency reader patch

Run `1311cd9d-baf8-4cc4-9edd-9efe934b77b0` supplied the decisive failed snapshot:
27 receive rows, 81 cost slots, correct NPC/item, live row 26, output 1, but no
costs under the old `row * 3 + slot` interpretation. The count ratio establishes
cardinality only, not arrangement or cost-field semantics. The actual AgentShop
cost mapping remains unestablished; this patch does not guess a replacement.

At 10:05 the user's standalone shopping list successfully remapped source row 13
to live row 26, bought one Tumbleclaw Weeds at the same NPC, and completed cleanup.
That establishes vendor/item availability and the existing selection callback's
working path, not the integrated cost reader's correctness.

Checked-out ECommons commit `6e3524d0c6d229b854d300d949b6a315aab97ab3`,
`UIHelpers/AddonMasterImplementations/ShopExchangeCurrency.cs`, provides a basic
currency-shop reader used by WahTools' bait purchasing flow:

| Addon value | Meaning |
| --- | --- |
| 4 | Entry count |
| 87 | Currency icon; resolve to item identity |
| 1066 + display row | Output item ID |
| 456 + display row | Live price |
| 1310 + display row | Native callback index |

The source comments also mention an older shifted item/price base; the new reader
uses the implementation's explicit contract only, not runtime probing of guessed
variants. Loaded-addon compatibility still requires controlled native acceptance.

The reader validates numeric value types, pointer/count bounds, and every nonempty
row's item/callback agreement with AgentShop output. The currency icon must resolve
to exactly one nonzero item ID; ambiguity is not resolved by taking the first row.
The reference Item CSV has one match for gemstone icon 65071: item 26807. That ID
and icon are not hardcoded into runtime matching.

`LiveVendorExchange.cs` separates detached reading and matching. A readable snapshot
is not authorization: the pure matcher requires exact output and complete live
costs, rejects HQ and ambiguity, and waits on incomplete duplicates. Missing live
prices never fall back to sheet prices. The original source listing is still
revalidated before each batch. Selection submits the validated callback index
without another item-only remap, and confirmation must retain both its selected-row
check and the callback index recorded at submission. Existing budget checks and
all-cost inventory/currency reconciliation remain unchanged.

Unsupported native layouts are flagged separately from inaccessible vendors. A
certain pre-submission unsupported-layout receipt skips equivalent-vendor fallback
for that allocation, retains the shortage, and permits other acquisition/ready
recipes to proceed after cleanup. Ordinary access/listing failures retain existing
authorized alternatives. Multi-currency offers are filtered before planning live
execution rather than reserving funds for a reader that cannot execute them.

Failed validation retains bounded receive/output observations and a raw diagnostic
snapshot of up to 96 AgentShop cost slots (including all 81 from the reported case).
These fields are evidence only, never authorization. Invalid pointers/counts are
not traversed. Existing throttling controls emission; no raw-cost snapshot is built
on successful matching. A new failed log can establish the real cost representation
or a loaded-addon contract difference without experimental purchase callbacks.

### Successful integrated purchase — 2026-11-04

User-provided run `5394fcd9-28ef-4386-bcd9-b984d476444b`, crafting list `test`
(26465), used the global enabled policy with retainer restock disabled. At
10:32:36 Kajeel Ja delivered one Tumbleclaw Weeds. The transient receipt confirmed
one acquired and three gemstones spent with certain accounting. At 10:32:38 the
shop/navigation cleanup completed, transient purchasing finished Completed, and
inventory reconciliation advanced to gathering two material types.

Remaining Tumbleclaw Weeds and Alexandrian Axe Beak Wing shortages were retained.
The user confirmed insufficient currency for the remaining purchases and accepted
the tested purchase path. This supports affordable partial acquisition and actual
spending reconciliation, not a reserve-boundary test. Sungilt Aethersand remained
an UnsupportedOffer shortage. The user stopped during subsequent gathering travel;
neither full crafting-list completion nor multi-batch shop reuse was demonstrated.

The direct-ground route rather than local aethernet follows the existing live-NPC
shortcut-skip rule. No routing-efficiency change was made as part of this patch.

## Verification and native acceptance

Managed tests cover opt-in, reserve boundaries/explicit zero, shared balances,
bundles/overbuy, external inventory, multiple limiting costs, disallowed secondary
costs, cumulative gil caps, policy snapshots, immutable costs, authorized vendor
fallback bounds, all-cost receipts, retained batches, and Stop/Pause. Existing
gil/GC, acquisition, crafting-progress, and teleport-boundary tests remain required.
Reader/matcher fixtures cover the documented basic addon contract with 27 receive
rows and callback 26, changed live prices/currency, callback/output disagreement,
bounds and missing numeric fields, duplicate/incomplete rows, HQ/output mismatch,
complete multiple-cost matching, immutable snapshots, and selected-row checks.
Unsupported-reader receipts are tested both for suppressing equivalent-vendor
fallback and for continuing unrelated currency work without losing the shortage.

Controlled native checks (partial acceptance):

- [x] Integrated gemstone material purchase, confirmed spending, cleanup, and
      gathering continuation for the reported one-item Kajeel Ja path.
- [x] Retain unaffordable gemstone shortages and continue other acquisition;
      insufficient funds confirmed by the user.
- [ ] Verify a configured nonzero reserve at its exact spending boundary.
- [ ] Verify quest-locked and rank/unavailable shop behavior without repeated loops.
- [ ] Buy more than 99 units and multiple materials from one compatible shop.
- [ ] Verify gil/GC batches no longer close/reopen compatible shops unnecessarily.
- [ ] Verify reserve exhaustion and partial acquisition retain correct shortages.
- [ ] Stop/Pause during travel, selection, confirmation, and between retained batches.
- [ ] Retry without duplicate purchases, lost spending, or lost crafting progress.
- [ ] Exercise a real supported bundle listing if one is available; multi-currency
      native execution needs a separate verified reader before acceptance.
- [ ] Confirm normal gathering/crafting and AR handoff/resume after shop cleanup.

Managed tests do not execute game callbacks and cannot establish native purchase
correctness. No commit/push is implied by this implementation.

Automated evidence: 484 acquisition/source tests and 95 actual-plugin compatibility
tests pass (579 total). Forced Debug/Release builds and `git diff --check` pass;
full builds retain only the two pre-existing ElliCon/ElliLib nullable warnings.
