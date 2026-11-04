# Vulcan UI navigation

Main tabs, in mouse/controller order:

1. Crafting Lists
2. Recipes
3. Workshops
4. Marketboard
5. Vendors
6. Settings

Macros and Standard Solver now live in the Settings sidebar. Raphael Solver has
Configuration and Solutions subtabs. Existing configuration, macro import/edit/
delete controls, and cached-solution controls are retained; no saved-data migration
is required. Recipe/list/vendor/marketboard entry points still select their tabs
and synchronize controller navigation with the selected destination.

The title-bar Font Awesome bug icon opens or restores **Vulcan Debug**, an
independently closable window containing the existing Debug sidebar and tools.
Settings → General also offers Open Debug for keyboard/gamepad navigation.
Debug is registered with the plugin window system and controller input blocking;
it does not consume the main window's shoulder-button tab indices.

Crafting Status uses one phase/recipe header, confirmed-craft progress, and
remaining craft count. During an AR handoff the header shows the handoff phase,
not the parked crafting phase. Details holds per-recipe confirmed progress,
remaining queue entries, and the existing crafting-time estimate. Material/AR
recovery remains available. Purchase-policy summaries and the duplicate Action
Delay slider are removed; the existing global slider remains in Settings → General.

The header's item belongs to its phase: Purchasing uses the active vendor request
only when its session and lease match this queue; otherwise it reads Purchasing
materials. Gathering uses the queue-owned live gathering target, otherwise
Gathering materials. Recipe names are limited to job switching, Raphael solving,
craft preparation/synthesis, and output confirmation. Other phases do not append
the next recipe. Details labels it Next queued recipe outside crafting phases
(or Parked recipe during AR handoff). This is presentation only: no sourcing,
purchase policy, inventory accounting, or queue execution changes.

## Verification

Managed tests cover main-tab order, contiguous controller indices, immutable
navigation metadata, status headers, and per-recipe confirmed-output accounting.
These tests do not render ImGui or host Dalamud. Native acceptance remains pending:

- [ ] Six main tabs appear in order; mouse and shoulder-button navigation wrap.
- [ ] Recipe/list/vendor/marketboard shortcuts open the right destination, and
      subsequent controller navigation starts from that tab.
- [ ] Settings sidebar opens Macros, Standard Solver, and Raphael Solver.
- [ ] Macro import, selection, edit, save, and delete preserve existing behavior.
- [ ] Standard Solver configuration, persistence, and reset still work.
- [ ] Raphael Configuration/Solutions switching preserves scroll and selection;
      search, cancel solves, clear cache, and auto-clear setting still work.
- [ ] Bug icon opens/focuses/restores Debug; closing it leaves Vulcan open.
- [ ] Debug tools work with Vulcan closed, including keyboard/gamepad navigation.
- [ ] Macro/solution panels remain accessible at narrow sizes and UI scales.
- [ ] Crafting Status stays compact during crafting and all AR phases; Details,
      Pause/Resume, Stop Queue, and failure-recovery controls still work.

No automation, solver, or purchase-policy behavior changes are intended.
