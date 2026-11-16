# Adaptive equipped-tool Good bonus

Implemented for ordinary crafting: **150%** normal Good quality or **175%**
enhanced Good quality. Cosmic territory/mission mechanics, expert recipes, and
unsupported effects remain excluded. Adaptive remains historical-only/non-executable.
Existing Standard/Raphael readers, selection, and settings are unchanged.

## Capture and provenance

`AdaptiveShadowNativeReader` reads the loaded `EquippedItems` container, slot **0**,
using the actual `InventoryItem.ItemId` (not a name, model, or glamour item). It
resolves that row in the installed **English** Item sheet, regardless of client
language. It requires a main-hand-only crafting tool matching the active job.

The audited tools' `ItemSpecialBonus`, `ItemSpecialBonusParam`, and `ItemAction`
are all zero; there is no numeric 175% effect marker in these fields. The effect
is in `Item.Description`. `AdaptiveToolBonus` therefore accepts:

- Empty English description with all three other effect channels zero: **150%**.
- One of 17 exact audited English descriptions containing the 1.75× effect:
  five Splendorous stage descriptions, Cosmic phases 9–19, and perfected Cosmic:
  **175%**. Mission-credit paragraphs are accepted only as part of these full
  audited templates; they do not enable mission mechanics.
- Nothing else. Missing rows/English data, unknown nonempty descriptions, another
  percentage, extra effect text, other populated effect channels, or job/slot
  mismatch block capture. Early Cosmic stages and unrelated lore descriptions
  are not automatically treated as ordinary; they need separate auditing.

Matching is ordinal full-description equality, normalizing CRLF to LF only. No
localized text fallback, fuzzy/name matching, item-ID range inference, network
lookup, or correction from observed quality. Replica names confer no bonus:
their empty effect description resolves to 150%.

Provenance used for the audited templates and examples:

- [XIVAPI English Item effect search](https://v2.xivapi.com/api/search?sheets=Item&query=Description~%221.75%22&fields=Name,Description&limit=500)
  returned 136 crafting rows / 17 unique descriptions.
- Item rows [45686](https://v2.xivapi.com/api/sheet/Item/45686) (Cosmic Frypan),
  [38744](https://v2.xivapi.com/api/sheet/Item/38744) (Crystalline Frypan),
  [39739](https://v2.xivapi.com/api/sheet/Item/39739),
  [39761](https://v2.xivapi.com/api/sheet/Item/39761) (replica), and
  [49060](https://v2.xivapi.com/api/sheet/Item/49060) (Stellar Frypan).
- API data version `a72e645591e5bf8b`, schema
  `exdschema@2:rev:e773c41a90aed788cf4c1c48469fa85618ef01fb`.
- Frozen extracted descriptions/examples:
  `tests/GatherBuddy.Adaptive.Tests/Fixtures/tool-effects.json`. Metadata evidence,
  not a simulation reference. Runtime uses installed data, not this fixture/API.

## Model and lifecycle

`AdaptiveCraftSnapshot` model version **2** contains `GoodQualityPercent` and
optional immutable `Tool` evidence (item/job IDs, percentage, detection source,
effect description). Only 150/175 are supported. Offline fixtures without the
new field default to 150; native capture always supplies tool evidence. Conflicting
tool/model percentages are rejected. All fields participate in record equality and
therefore in captured search identities.

The adapter maps 175 to the existing shared simulator's `SplendorCosmic` flag.
That flag changes only Good quality. Progress, Normal/Excellent/Poor, single-final-
floor arithmetic, scratch simulation, and validated replay use existing shared
code; no second quality formula or shared-solver behavior change was added.

During shadow observation and immediately before recording each executor request,
the framework rechecks the loaded main-hand item ID. A changed or unavailable ID
blocks correlation and revokes historical work, including late results. It does
not delay or stop the existing selected solver. This checks item ID, not complete
equipment-instance/materia/stat parity; broader native state acceptance remains open.

## Regression and acceptance

The supplied trace's step 10→11 has base quality 300, Preparatory Touch 200%,
Innovation 150%, IQ 130%, and Good 175%:

`floor(300 × 2 × 1.5 × 1.3 × 1.75) = 2047`; **7290 → 9337**.

Ordinary Good gives **7290 → 9045** instead. Tests replay all ten supplied
request-correlated transitions: enhanced model accepts the final transition;
ordinary control blocks with an explicit Quality expected/observed difference.

Additional coverage: all 17 metadata templates, replicas, unknown/extra effects,
job/slot/effect-channel validation, snapshot round-trip/identity, percentage
validation, strict-target replay, tool-context revocation, 352 buff/IQ/condition/
cap combinations through allocating and scratch paths, and actual-plugin checks.

Pinned Raphael's simulator exposes only ordinary Good (3/2), with no 175% setting.
Its 22,504 frozen independent trials remain unchanged; enhanced-tool non-Good
cases also replay against that existing independent projection. **175% Good parity
with independent Rust is not claimed.** Enhanced Good is covered by exact arithmetic
and supplied native observations, not newly generated independent runtime fixtures.

Schema **4** exports the model percentage/tool evidence and durable `LastMismatch`:
expected state, observed projection, and immutable per-field differences. Observed
projection includes tracked combo/availability, not native action-ID confirmation.
Schema-3 historical labels, counts, bounded histories, and terminal honesty remain.

**Subsequent native evidence received:** supplied schema-5 all-NQ run identifies
tool 51785/175%, correlates all 16 observed outcomes without mismatch, and exercises
Good Delicate Synthesis (0→525) and Good Preparatory Touch (2685→5047). Ordinary
controls predict 450 and 4710 respectively. Compact fixture and regression scope:
`adaptive-seeding.md`. This supports native enhanced-Good arithmetic, not independent
Rust parity, native action-ID identity, final completion, or Adaptive execution.

**Native acceptance still required:** ordinary/replica 150%, non-English clients
using English metadata, unknown-description rejection, and changed/unreadable tool
revocation. Do not infer terminal completion from a vanished addon or enable execution.
