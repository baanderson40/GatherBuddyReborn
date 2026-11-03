# Current-Grand-Company seal reserve

Automatic vendor purchases now have one **GC seal reserve**, globally and in custom
list overrides. Gil reserve remains separate. The same captured seal reserve
applies to whichever Grand Company the character currently belongs to.

## Settings and migration

- `GrandCompanySealReserve` replaces per-currency reserves for Storm Seals (20),
  Serpent Seals (21), and Flame Seals (22).
- Missing/null new setting migrates to the highest legacy seal reserve, or zero
  when no legacy entries exist. This preserves protection without requiring a
  logged-in character, but may be more restrictive than the current GC's old value.
- An explicit new value wins, including zero. Normalization removes only legacy
  seal entries; gil and unrelated currency reserves remain unchanged.
- Deserialization, cloning, and serialization normalize the policy. Migration is
  idempotent, and new exports contain only the unified reserve. Differing legacy
  values produce a bounded migration diagnostic on traced normalization/load.
- Inherit/Disabled/Custom behavior is unchanged. Active runs and their retries
  retain the captured reserve amount; settings edits apply to future runs.

## Runtime protection

- Membership comes from live player state, not the selected UI currency or a
  nonzero retained balance. Settings show current GC/currency as read-only context.
  The reserve remains editable when logged out or unaffiliated.
- The offer adapter rejects other-GC offers and all seal offers when membership
  is unavailable. Automatic acquisition and Debug preview capture balances only
  for current-GC seals. Generic balance readers/manual shopping remain unchanged.
- Planner, transient execution budgets, preview, and status diagnostics use one
  resolved-policy reserve lookup. The existing native purchase constraints carry
  that reserve into live affordability checks.
- Exact-offer and current-membership checks run again before purchasing; existing
  GC shop validation also checks membership before selecting an item. A stale-GC
  allocation cannot substitute a different currency. Submitted purchases still
  settle through existing receipt/cleanup handling.
- Explicit acquisition retry replans from live membership using the captured run
  policy and cumulative spending. No automatic route retry is added. Gil/gathering
  fallback and unresolved-shortage retention remain available.

## Verification

Linked-source tests cover legacy/default migration, explicit zero precedence,
idempotence and bounded diagnostics, clone/export/import round trips, override and
snapshot isolation, eligibility for all three seal currencies, unknown membership,
reserve affordability, membership changes, and transient execution reserve budgets.
Existing split-allocation, gil, ownership, and retry-spending tests remain in place.

Native UI/callback behavior still requires in-game checks:

1. Confirm exactly one seal reserve globally and in custom list policy; current GC
   matches the character. Check inheritance and disabled purchasing.
2. Set reserve near actual seal balance. Authorize a small purchase; verify only
   current-GC seals are used and balance cannot cross reserve.
3. Verify gil fallback and material-shortage behavior when seals are protected.
4. If testing an old configuration, confirm highest legacy reserve is migrated,
   then set zero explicitly, reload, and verify zero persists.

Actual GC-transfer behavior is not live-verified; simulated membership changes do
not establish full native coverage. Earlier uncommitted Phase 5 work is preserved.
