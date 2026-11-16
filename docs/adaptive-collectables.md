# Adaptive collectables

Adaptive now captures ordinary collectable reward thresholds from local game
sheets before synthesis. No HTTP request, hardcoded recipe target, legacy fallback,
or fabricated native field is used at runtime.

## Goals

General settings exposes **Collectable target** for Adaptive, default tier3.
Tier1 is always mandatory for a collectability goal; the selected tier is desired,
not guaranteed. A validated completion between tier1 and the selected higher tier
can be used. Below tier1 never counts as a valid completion. Existing strict-target
replay still requires the selected tier; no new strict-target setting is introduced.
Recipe required quality can raise either floor. Standard/Raphael settings and
the default Pure Raphael mode remain unchanged. Changing Adaptive tier saves config
and reloads solvers, revoking preparing/live context rather than reusing stale work.

## Data

`AdaptiveCollectabilityReader` matches the output item across `CollectablesShopItem`,
`HWDCrafterSupply`, `SatisfactionSupply`, `SharlayanCraftWorksSupply`, and
`BankaCraftWorksSupply`. Ratings multiply by10 into simulator quality units.
Sharlayan declares two tiers: lower maps to slots1/2, upper to slot3, matching the
existing two-tier convention. Positive, ordered ratings must fit maximum recipe
quality without clamping. Missing/malformed rows, conflicting reward contexts,
or more than16 matching rows reject preparation. Identical duplicate tiers are
accepted while retaining first provenance. Successful lookups cache at most128
item/maximum-quality entries; failures are not cached. Cache lives only in process.

Immutable snapshot evidence includes result item, source row and original ratings.
Validation checks evidence against all scalar quality thresholds. Evidence and goal
participate in snapshot/preparation equality, preventing stale context reuse. Raphael
seeds remain candidates only, replayed from the actual confirmed root under this goal;
an under-tier seed never bypasses the mandatory floor. Existing budgets are unchanged.

Frozen `collectable-tacos.json` records external game-data evidence for recipe35829,
Rarefied Tacos de Carne Asada, output44232. Reward source is
`CollectablesShopItem/30/27` → `CollectablesShopRefine/187`:
ratings660/900/1140 → quality6600/9000/11400, maximum12000. URLs, schema revision,
and data version are in the fixture. This is data evidence, not native acceptance.

## Rejected starts

Failed capture/context preparation logs a recipe-specific warning and pauses the
queue with the actual reason, retaining work. Synthesis is not sent and no legacy
executor takes over. Preparation exports include attempted recipe/ticket,
`Phase=BeforeSynthesis`, `StartAccepted=false`, and empty current `LiveAnalysis`.
Previous recipe5282 roots cannot be presented as recipe35829 startup evidence.

## Acceptance

### Collectable output reconciliation follow-up

Supplied session `d0e8365a-a7c4-4840-b81b-43be31c02e4e` successfully completed
Rarefied Tacos:13 acknowledged actions,12 independently correlated full frames,
terminal progress6600/quality11826/durability15 and managed craft end, with no
mismatch or dropped decision roots. Veneration correlated normally. The retained
events do not show the500ms status-settling path being exercised; this is successful
collectable execution evidence, not proof that every intermittent race is resolved.

The queue subsequently paused: run `d8c6272c-6365-4328-bd9f-768cc4a0b2f1` logged
recipe35829/output44232, before0/after0, attempted1/confirmed0/cancelledFalse.
Its ordinary NQ/HQ count did not observe collectable output despite native success.

Output reconciliation now captures `AlwaysCollectable` from the result item and
counts matching collectable slots directly across the four loaded player bags.
Base item IDs come from ClientStructs, not guessed encoded-ID arithmetic. Only
output accounting uses this path: ordinary output counting, ingredient NQ/HQ
availability, acquisition requirements, retainer/saddlebag scope and Adaptive
terminal confirmation remain unchanged. Missing/unloaded/unsupported bags return
unavailable rather than zero or partial counts. Quantity overflow fails closed.

The same fresh counter is used before and after synthesis. Existing delta/yield/
requested-count caps, deadline and pause behavior stay intact; a native success
packet alone never advances queue progress. Existing collectables do not satisfy
a new craft without a positive output delta. Result logs include `collectable`.
Pure regression tests cover Tacos0→1, existing stock, unchanged/decreased counts,
multiple slots, unrelated/noncollectable items, yield/caps, consecutive baselines
and invalid/overflow counts. Compiled boundaries verify both capture sites and
separation from ingredient accounting.

This does not retroactively credit the old paused attempt. Check inventory/remove
already-completed work before resuming to avoid another craft. An attended fresh
queue test must still verify `before=N; after=N+1; confirmed=1; collectable=True`
and normal queue completion. Logs: `/tmp/opencode/collectable-output-*.log`.
Verification:636 Adaptive tests passed each Debug/Release,286 compatibility and
652 acquisition tests passed. Forced sequential Debug/Release builds passed with
only the two existing ElliCon/ElliLib nullable warnings. Tracked whitespace clean;
native queue-output acceptance still pending. No commit or push.

### Veneration status-replication follow-up

Supplied session `f6876b63-0f0e-46d5-952d-5e28cd23d62c` captured the correct
collectable thresholds and confirmed seven actions, reaching quality11490.
Action8 Veneration matched native action19304/step9 and CP358, but the independent
status list still read Innovation2/Veneration0 across two host frames. Expected
statuses were Innovation1/Veneration4; the subsequent screenshot shows those exact
buffs, Good condition, step9 and quality11490. This supports status replication
lag, not a Veneration arithmetic correction. Screenshot arrival time is unknown;
neither eventual craft completion nor a measured maximum lag is established.

`AdaptiveShadowSession` now permits a status-only mismatch to settle within500ms
of the exact matched acknowledgement. This conservative fixed bound is policy,
not a latency measurement from the screenshot. Step/progress/quality/durability,
condition and CP must already match; scalar or protocol mismatches still stop
immediately once stable. No acknowledgement means no settlement window. No
prediction becomes an observation. The full corrected frame must repeat on two
distinct host frames before any state is promoted or another action can dispatch.
Normal matching frames incur no added delay. The deadline never slides on status
changes and fails closed even if reads vanish or correction arrives at/after expiry
once settling began. Existing eight-second request timeout still applies. This
does not retry actions, weaken exact final comparisons, or change legacy execution.

Regression tests reconstruct the eight recorded requests/frames and the old
Innovation2/Veneration0 sample, then supply the screenshot-corroborated statuses
at synthetic times. They also cover repeated-read requirements, no premature
promotion, fixed deadline, missing reads, late correction, scalar mismatches, and
missing acknowledgement. No in-game retest has been performed by the coding agent.
Follow-up logs: `/tmp/opencode/adaptive-status-settlement-*.log`.
Follow-up verification:636 Adaptive tests passed each Debug/Release,285
compatibility and642 acquisition tests passed. Forced sequential Debug/Release
builds passed with only the two existing submodule nullable warnings; whitespace
checks passed. Legacy executor unchanged. Attended native retest remains pending.

Managed tests cover frozen conversion, ordering/range/overflow, duplicates/conflicts,
two-tier normalization, snapshot evidence, selected-tier and metadata drift,
mandatory/strict seed replay, and empty rejected-start exports. Compiled-plugin
boundaries check local-sheet capture, persisted tier default, revocation, queue pause,
and preparation export routing. Native acknowledgement/frame checks remain strict.

Attended collectable execution is still needed. Reload plugin, select Adaptive,
craft Rarefied Tacos, then inspect exported snapshot thresholds6600/9000/11400,
mandatory6600, selected desired tier, native confirmations and terminal quality.
Do not infer successful native acceptance from build/tests alone.

Verification logs: `/tmp/opencode/adaptive-collectable-*.log`.
Final verification:625 Adaptive tests passed each Debug/Release,285 compatibility
and642 acquisition tests passed. Forced sequential Debug/Release builds passed
with zero errors and only the two existing ElliCon/ElliLib nullable warnings.
Tracked whitespace and all110 untracked text-file checks passed. Legacy action
executor remains unchanged. Native collectable acceptance remains pending.
No coding-agent in-game actions, commit or push.
