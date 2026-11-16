# Adaptive native acknowledgement gate

## Status

First live-integration prerequisite implemented: **opt-in experimental native callback
capture and strict diagnostic acknowledgement/terminal matching**. Native callback
signature/layout validation is pending. The separate saved
[Adaptive (Experimental) option](adaptive-option.md) now authorizes execution by
selection, automatically preparing fresh capture per craft. Its host permits experimental
actions; [activation and limits](adaptive-live-host.md). Historical
search/seeding budgets and Standard/Raphael/macros/executor timing remain unchanged.

This is a staged implementation, not a released/native-validated Adaptive solver.
Experimental trial arming is explicit permission to dispatch, not a native-validation claim.
Supplied schema-6 traces now support operation/action/step/result interpretation for
one CUL recipe/route, including post-fix Normal terminal acceptance. Broader native
lifecycle acceptance remains pending; this is not blanket callback validation.
First supplied explicitly armed live trial now also supports one-action observe/replan
for this recipe/job:16 acknowledgements,15 full-frame outcomes,full-quality completion
by Groundwork at zero durability. Source totals and retained-suffix limitations are
documented in [adaptive-live-host.md](adaptive-live-host.md). Live export schema3 removes
misleading historical/disabled labels; historical shadow schema6 is unchanged.
Current live schema4 additionally retains bounded per-root validated proposals,search
diagnostics,exact transaction linkage,and host confirmation timings. These records do
not authorize execution or add native-validation claims; see the live-host report.

## Capture activation and risks

1. Before starting a managed ordinary craft, enable **Vulcan Debug → Adaptive Shadow →
   Enable for next managed craft**.
2. Enable **Experimental native acknowledgements for next craft**. Read the capture
   status; a missing signature leaves the diagnostic start rejected, not downgraded
   to request-only confirmation. Do not enable mid-craft.
3. Run the existing selected solver. Copy the bounded trace after finish.
4. Disable experimental capture after the test. Disable/logout/disposal tears down
   the hook; no hook is installed automatically at plugin startup or craft start.

The callback signature and offsets are experimental and patch-sensitive. An incorrect
native signature/layout can destabilize the client; managed guards cannot make unsafe
native memory access safe. No installed-client native execution was possible here.
Use a controlled test craft, not an unattended queue, for initial validation.

Source audit: Artisan revision `c8ee6e0b70e7ca08dcae73d6546095e3baeed9c5`,
[`GameInterop/Crafting.cs`](https://github.com/PunishXIV/Artisan/blob/c8ee6e0b70e7ca08dcae73d6546095e3baeed9c5/Artisan/GameInterop/Crafting.cs)
and [`GameInterop/CSExt.cs`](https://github.com/PunishXIV/Artisan/blob/c8ee6e0b70e7ca08dcae73d6546095e3baeed9c5/Artisan/GameInterop/CSExt.cs).
The installed FFXIVClientStructs API does not expose a crafting acknowledgement
handler, so this isolated tap uses the documented four-pointer void callback and
signature `48 89 5C 24 ?? 48 89 7C 24 ?? 41 56 48 83 EC 30 80 A1`.
No dependency download/build, action hook, payload mutation, or network send is added.

## Evidence and ownership

- Native handler must equal local `EventFramework.GetEventHandlerById(0x000A0001)`;
  callback must occur on the framework thread. The original function runs exactly
  once, outside capture exception handling/lock. The tap never changes payloads or
  bypasses native behavior. Only immutable scalar projections leave the callback.
- Fresh StartPrepare (1) then StartInfo (2) establish a stream, recipe, and starting
  quality. Attachment requires a matching strict fresh step-one frame, no prior
  action, no lost events, and no prior attachment to that stream. No mid-craft join.
- AdvanceCraftAction (9) / AdvanceNormalAction (10) project a fixed 0x48-byte prefix:
  raw action ID at 0x10, step ushort at 0x18, progress at 0x1C, quality at 0x24,
  durability at 0x30, condition-plus-one at 0x38, flags at 0x44. Unknown operations,
  malformed fields, off-thread callbacks, and overflow revoke diagnostic history.
  StartReady (3) is lifecycle-only; Finish (4) alone never proves success. Abort (6)
  and quick-synth switch (12) block the ordinary diagnostic stream.
- Native queue is bounded to 64 records. Overflow is sticky until a new prepared
  stream, not hidden by draining. Framework drains before ordinary observation,
  before the next tracked request, and before managed teardown so terminal callback
  scalars can survive addon disappearance. New streams/stale sequence/recipe or
  starting-quality mismatch cannot leak acknowledgement into another craft.
- Each request supplies the exact job-resolved action ID. One outstanding request,
   reported success in historical mode (tracked invocation and recorded raw return in
   live confirmation-only mode), matching native operation/action ID, increasing event sequence,
  and model-consistent observed packet scalars are required. Attempts/reports alone
  cannot advance strict-mode history. Paused, rejected, failed, duplicate, unexpected,
  reordered, untracked, or mismatching acknowledgements block; no guessed recovery.
- For nonterminal actions, packet acknowledgement still cannot replace independent
  CP/status reads: repeated stable framework-frame samples must agree with the
  packet and every modeled supported field. Legal randomized conditions must agree
  between packet and addon too. Non-step Final Appraisal still counts as an action.
- Terminal callback uses the **previous step index**, unlike ordinary advancing
  actions, and preserves the **input condition**: there is no next turn. The
  simulator's hypothetical Poor→Normal or Excellent→Poor next condition must not
  replace the observed terminal condition. Terminal condition equality is still
  required; nonterminal Normal randomness/forced transitions remain unchanged.
  Progress/quality/durability must match the model's normalized expected
  scalars and completion flag presence. Action-success bit 0x10 must be present.
  Completion bits 0x04/0x08 are not treated as success authority: the audited source
  notes 0x04 can occur on durability failure. Observed progress/mandatory quality
  determine success versus failure only after all checks match.

## Schema 6 and terminal honesty

Exports add raw `NativeCraftEvent` / `NativeActionMatched` / `NativeTerminalObserved`
events and `Summary.NativeEvidence`: required-for-correlation flag, matched count,
last acknowledgement, terminal evidence, layout identity, `ExecutionAuthorized=false`.
`NativeCaptureLost` revokes history through an explicit blocked reason.
An additive schema-6 diagnostic field, `Summary.NativeEvidence.LastMismatch`, now
retains rejected raw packet, expected packet-only projection, condition rule/allowed
values, sequence lower bound, reason, and read-only per-field differences. Matching
`NativeMismatch` events/UI show the same evidence; native-frame CP/buff mismatches
remain separately under `Summary.LastMismatch`. Missing request expectations stay
null rather than invented. Flags retain raw observed value; comparison checks
action-success/completion bits, not exact equality with unrelated flag bits.

An admitted terminal packet reports `ExperimentalPacketSuccess` or
`ExperimentalPacketFailure`, explicitly experimental rather than native-validated
full-state confirmation. It does **not** manufacture a full final frame, CP, buffs,
combo, unused-action availability, another correlated action, or a current search
root. `LastStableFrame` remains the last actual repeated sample. Terminal evidence
revokes remaining historical work. Finish/addon loss without matched terminal action
remains `Unobserved`. Ended/revoked sessions cannot accept late terminal evidence.

Top-level `NativeActionIdentityConfirmed=false` intentionally remains unchanged while
the callback layout is unvalidated; raw matches are evidence, not execution authority.
Default shadow mode still requires no callback and retains its existing semantics.
Runtime/UI flags are neither persisted nor solver selection settings.

## Regression evidence and checks

The additional supplied schema-5 traces reach final observed quality 11775 and 11550,
not 12000, while retaining 16/16 full-target plans. Both contain 16 comparator ties,
15 aligned Adaptive continuations, no mismatch/skips/cancellations, and an unobserved
terminal state. In the 11775 run, Good Delicate Synthesis supplies 525 quality; in the
11550 run, Good occurs on a buff turn, not a quality action. These traces do not prove
native final completion and are not controlled A/B.

All three supplied final roots (quality 11550 / 11775 / 12000, progress 6209,
durability 15, CP 86, Innovation 1, Normal) are regression cases. Delicate Synthesis
adds modeled progress 474 and quality 450: reaches 12000 even at the exact 11550
boundary. Careful Synthesis cannot meet target below cap; at cap it can conserve CP.
These tests use observed root scalars, not fabricated native terminal observations.

Managed checks cover binary offsets/length/scalar rejection, action/stream/sequence
and packet/addon matching, no CP/status substitution, non-step actions, failed/rejected/
paused outcomes, duplicate/late evidence, terminal nonincremented step, failure despite
completion-success flag, bounded native queue/overflow, opt-in compiled boundaries,
default shadow compatibility, and schema-6 terminal export semantics. Synthetic
acknowledgements are not independently captured native packets or Rust parity.

### First schema-6 native trace and terminal-condition correction

User-supplied trace matches 16 exact job-resolved action acknowledgements plus all
16 independently sampled outcomes. Examples: CUL Innovation 19011, Preparatory
Touch 100306, Great Strides 267, Byregot 100346. Historical plans remain 16/16 full
target. The final Delicate Synthesis packet (100330) arrives before addon teardown:
step17, progress6600, quality12000, durability5, condition-plus-one4 (Poor), flags22
(0x16: action succeeded and complete). Original code blocks because it expects the
simulator's hypothetical next Normal condition instead of preserved terminal Poor.
This is a validator bug, not evidence of bad progress/quality arithmetic.

Correction compares terminal condition to the actual input condition, retaining all
identity/context/order/scalar/flag checks. Offline replay now admits that packet as
`ExperimentalPacketSuccess`, increments matched acknowledgements to17, and leaves
full-frame correlations16 and `LastStableFrame` at observed progress6209. Wrong
Normal terminal condition still rejects with `ConditionPlusOne: expected4, observed1`.
No final CP/buffs/observed frame is manufactured; execution authority stays false.

`Fixtures/native-schema6-poor-terminal.json` is a labelled compact projection of all
16 observed roots and five retained raw packet projections (including terminal),
not the complete export. Earlier packet envelopes were evicted: test-prefix
acknowledgements and start sequence watermark are explicitly synthetic. Tests cover
the supplied packet, 13 independently corrupted packet fields, all four input
conditions (at that patch, other terminal conditions synthetic), durable detached mismatch export,
and actual-plugin acceptance/rejection boundaries. Native execution of the patched
terminal rule still needs a fresh trace; this single craft does not validate all
actions, non-step callbacks, failure/abort/pause paths, or future client versions.

Verification: 334 Adaptive tests pass in Debug/Release, 184 actual-plugin compatibility
tests and 566 acquisition tests pass (1084 distinct). Forced Debug/Release solution
builds pass with only the two existing nullable warnings in unchanged ElliCon/ElliLib.
Tracked/untracked code/project/docs/JSON whitespace checks pass. No commit/push.

### Post-fix Normal-terminal capture

Supplied session `7a95f7fd-d133-493e-88fc-6f03ea91bdb2` admits 17 exact native
acknowledgements, 16 independently repeated full-frame correlations, and
`ExperimentalPacketSuccess`, with no blocking reason or frame/packet mismatch.
Final CUL Delicate Synthesis 100330, stream1/sequence20/op9, preserves input step17
and Normal condition (condition-plus-one1): progress6600, quality12000, durability5,
flags22. Last actual stable frame stays progress6209/quality11550/durability15/CP86/
Innovation1. Packet supplies the missing 450 quality, not fabricated final CP/buffs.
Finish sequence21/op4 follows accepted terminal evidence; Finish alone proves no success.

Historical results remain 16/16 validated full-target plans, without skipped roots,
cancellation requests, discarded results, or dropped results. One comparison improves:
at Good step15, Byregot→Delicate takes two actions/6 modeled seconds versus seeded
Great Strides→Byregot→Delicate, three/8. Existing executor uses Great Strides, correctly
invalidating that Adaptive continuation's alignment at the next root. This is a
historical proposal, not an executed optimization or new 175% quality observation:
Good is consumed by a buff action. Event ring retains64/234, evicting170 as designed.

`Fixtures/native-schema6-normal-terminal.json` preserves a labelled compact projection
of all 16 roots, five retained acknowledgement packets, Finish, and summary counts.
Earlier acknowledgements and attachment watermark in replay are explicitly synthetic.
Regression exercises actual session matching, terminal/end handling, detached export,
the 11550→12000 boundary, and unchanged false execution authority. Historical counts
are recorded source evidence, not reproduced by the no-search terminal replay.
This post-fix Normal run does not directly retest native Poor-terminal acceptance;
the prior Poor packet is still supported by offline corrected-validator replay only.

Verification for this evidence patch: 335 Adaptive tests pass in Debug/Release,
184 compatibility and 566 acquisition tests pass (1085 distinct). Sequential forced
Debug/Release solution builds pass with only two existing unchanged submodule nullable
warnings; tracked diff and 67 untracked text files pass whitespace checks. No native
game trial was run by the coding agent. No commit/push.

### Additional supplied Good-terminal trace

Session `4c3f57ea-bdb4-4efd-a3f5-9f8bfb9cc965` also admits17 acknowledgements/16 full
frames, no block/mismatch, and16/16 target plans (all comparator ties). Terminal step17,
action100330,sequence20 preserves Good (condition-plus-one2), progress6600,quality12000,
durability5,flags22. Last stable quality11550 remains distinct. Capped terminal gain
cannot distinguish enhanced from ordinary Good arithmetic. This single export does not
establish consecutive-craft isolation or pause/resume coverage.

## Further native coverage and experimental executor

Collect broader schema-6 opt-in cases. Expect matching recipe/start context, exact
job action IDs, matched acknowledgement for every tracked request, unchanged native
CP/buff correlation, and terminal scalar evidence with unchanged final step index.
If matching fails, retain the blocked trace and investigate offsets/order; do not
relax checks or infer missing data. Test Final Appraisal, pause/revoke, cancellation,
failed/rejected actions, and consecutive crafts separately.

At user request, implementation continued without collecting further native cases first.
Separate runtime-only trial host now uses the existing one-use coordinator leases;
resolves supported active policy goals; captures fresh state/native action availability
immediately before consuming one lease; executes once; waits for admitted action
acknowledgement plus independently observed state; then replans.
No simultaneous historical/live searches, no automatic fallback/retry after ambiguity,
no macro cursor advancement, no predicted-field observations. Default solvers stay
unchanged. Controlled trial acceptance must precede selectable/persisted release mode.
Host boundaries and unsupported policies: [adaptive-live-host.md](adaptive-live-host.md).

Live return-neutral correction: `UseAction` true/false are diagnostics, not execution
proof. Live matching requires an invoked one-use transaction, exact acknowledgement,
and independent stable frame; coordinator reporting occurs only after confirmed outcome.
False never clears pending history or permits a retry. Occupied native queue blocks
dispatch without mutation. Expired outcomes remain unconfirmed, including packets
arriving after deadline before the next host tick. Historical `ReportRequest(false)`
semantics and schema6 exports remain unchanged. Live transaction export is schema2.
