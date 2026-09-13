# Changelog

All notable changes to this project will be documented in this file.

The log versioning the plugin versioning will not match as 0.0.0 technically does not match semantic versioning but the headache of trying to change this would be too much.
Instead the changelog reader and automation surrounding plugin PRs will add the back in

The format is based on [Keep a Changelog](https://keepachangelog.com/en/0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.html).

## [1.4.0.2032] - 2026-09-13

### Added
- The preset panel is now a full preset editor: every mannequin slot has a searchable equipment dropdown (filtered to tradable gear equippable in that slot, highest item level first, with the item level shown), the icon updates to the chosen equipment, and a slot can be cleared from the same dropdown. Empty slots show a "选择装备" picker so gear can be added to unused slots.

### Fixed
- Panel edits no longer revert within half a second: the saved preset is now user-owned — the periodic capture only fills in slots the preset does not know about yet and never overwrites existing entries — and the panel rows, the restock plan and the status column are all driven by the preset (status compares it against the cross-checked live state).

### Changed
- Because the preset is now authoritative, repricing an item through the native window no longer updates the preset automatically; edit the price in the panel instead.

## [1.4.0.2031] - 2026-09-13

### Fixed
- Switching the equipment picker to the retainer tab now works: like the sell-as-set checkbox, the tabs ignore replayed click events, so the selected state is set directly on both radio buttons and the window is then notified to reload the list. The flow can also switch back to the bag tab as the fallback direction, and both tab nodes plus their registered events are logged for diagnosis if the tabs are ever restructured.

## [1.4.0.2030] - 2026-09-13

### Fixed
- Equipment stored in the retainer's inventory is no longer skipped with a "take it out at a summoning bell first" message: the equipment picker's retainer tab lists that gear directly, so the restock flow now switches to it up front for retainer-sourced items (and still retries the other tab before giving up). The panel tooltip for 在雇员 items was updated accordingly.

## [1.4.0.2029] - 2026-09-13

### Changed
- Restocking is markedly faster. The per-item agent confirmation is skipped when the agent entry was stale to begin with (it never updates in that state, so every item paid a guaranteed one-second timeout plus a warning), all state-driven waits poll at 50ms instead of 100ms, and the post-callback settle delay was halved.
- Sell-as-set goes straight to the strategy verified in-game — set the checked state and notify the window — instead of first spending ~1.7s on the event-replay attempt that never toggles the component.

## [1.4.0.2028] - 2026-09-13

### Fixed
- Manually taking items down no longer leaves the panel stuck on "在售" until the window is committed and reopened: the agent memory does not update in real time for manual take-downs, so each capture now cross-checks against the live native window text — slots the agent claims are listed while the window no longer shows their item are downgraded to needing restock (occurrences are counted per item name, so duplicate items across slots are handled).
- The panel recaptures every 500ms while the shop window is open instead of only while it is empty, so state changes surface without reopening; an unchanged capture signature keeps this free of log spam and UI churn.

## [1.4.0.2027] - 2026-09-13

### Fixed
- Sell-as-set clicking now delivers the replayed event to the listener the game registered it for (the checkbox component registers mouse events to itself and converts them into a ButtonClick for the window; delivering to the window skipped that conversion, which is why nothing happened). If that still does not raise the confirmation prompt, a second strategy sets the checked state directly and notifies the window.
- Success is now judged by the real signal — the confirmation prompt appearing, being answered, and the checkbox reading as ticked afterwards — instead of the click merely having been dispatched.

### Changed
- Every sell-as-set click attempt logs the checkbox node's full registered event inventory (event types, params, listener ownership), so if the control still does not react the log pinpoints what it actually listens for.

## [1.4.0.2026] - 2026-09-13

### Fixed
- Clicking the sell-as-set checkbox now actually toggles it: the synthesized click fabricated its own event object whose target pointed at the global stage, which component handlers reject, so the click was dispatched but ignored. The node's own registered event object (with the game-filled listener and target) is now handed back to the window instead, the proven approach used by other plugins.
- Component lookup only matches visible nodes, so a hidden template checkbox can never be picked over the real control.

## [1.4.0.2025] - 2026-09-13

### Fixed
- The sell-as-set checkbox is now located through the addon's flat uld node list (recursing into nested components) instead of the child-node tree, which never contained it — this is why the run always reported "未能自动勾选". If it still cannot be found, the log dumps every component in the window for diagnosis.
- A failed sell-as-set no longer falls through to pressing 确定: committing without the set-sale flag is exactly the loss the option exists to prevent, so the window is left open with a message asking to tick it manually.
- The closing steps run after the final state capture, so pressing 确定 (which closes the window) no longer overwrites the completion status with a spurious "补货流程已取消".

## [1.4.0.2024] - 2026-09-13

### Fixed
- Each relisted item stalled for the full verification timeout and was then reported as failed even though the game had accepted the listing: after collecting a sold set's earnings the agent's availability bytes can stay stale, so requiring the slot to read as "actively listed" never succeeded. The price dialog closing after confirm is now the success signal (a dialog that stays open is treated as rejected and closed), and the agent check is a short best-effort confirmation that logs a warning instead of failing the item. This also restores the closing steps (sell-as-set + 确定), which were skipped because every "failed" item kept the restocked count at zero.

## [1.4.0.2023] - 2026-09-13

### Fixed
- One-click restock reported "当前没有检测到售罄装备" while every native slot was visibly empty: after collecting the earnings of a sold set the agent data keeps the item entries with an availability value that is neither "listed" nor "sold out", and only "sold out" counted as needing restock. Any slot whose availability is not "listed" is now treated as needing restock; the removal step is only attempted for genuinely sold-out slots (a collected slot is already clear in the native window), and the final per-item verification requires the slot to read as actively listed again rather than merely holding the item id.

### Changed
- The capture log now dumps each occupied slot's raw availability value, so any further unknown availability state is identifiable from the log.

## [1.4.0.2022] - 2026-09-13

### Fixed
- "只按整套出售" is now actually ticked at the end of a run: the checkbox registers its handler as a ButtonClick event (often on a collision node inside the component), while the synthesized click only ever dispatched MouseClick at the component node, so nothing happened. Clicks now try ButtonClick first and fall back through the component's child nodes, and the run verifies the box really shows as ticked before pressing 确定 — if it does not, the status message says so instead of silently committing a per-piece listing.
- The preset panel no longer jumps to the left side whenever any dialog opens (e.g. the sell-as-set confirmation): it only docks left while the equipment picker is open (which occupies the right edge) or during a restock run, so it does not bounce between sides.

### Changed
- The collapse control now matches the retainer list overlay: a chevron icon button (left to collapse, right to expand) instead of the text button.

## [1.4.0.2021] - 2026-09-13

### Fixed
- The preset panel no longer disappears while a native dialog (equipment picker, price input) is open: instead of hiding, it now docks to the left side of the shop window, since those dialogs open on the right.
- A mannequin whose slots were emptied (items taken down but never relisted, e.g. after a failed run) showed an empty panel with nothing to restock. Slots that are empty in the game but known to the saved preset are now restored into the panel as needing restock, so one-click restock can relist them from the recorded item, HQ flag and price.

## [1.4.0.2020] - 2026-09-13

### Fixed
- Equipment selection reported every item as missing even though it was visible in the picker: the node-id chain lookup searched the whole tree per id and never descended into component nodes' uld node lists, which is where list rows actually live. The lookup now walks the chain level by level (root id must match, each id resolved among direct children, component contents searched in the uld list), matching the reference implementation, so rows 4/41001+ are found.
- Row matching is now HQ-aware: when the preset marks the item as HQ, only rows carrying the HQ glyph match (and vice versa), so the NQ copy of the same equipment is never listed by mistake.
- Closing the equipment picker after a failure now uses the window's own close routine, since the picker ignores the generic cancel callback and used to stay open.
- The retainer-tab/checkbox lookup also descends into component contents, so tabs nested inside a header component can be found.

### Changed
- When an item still cannot be located after all retries, the log now dumps the rows the picker actually shows, so a future layout change is diagnosable from the log alone.

## [1.4.0.2019] - 2026-09-13

### Fixed
- One-click restock did nothing: every addon callback was fired with the callback id passed as the *value count*, so the game read uninitialised memory instead of the intended command (e.g. "13 values" for a 1-value call). Callbacks now pass the id as the first AtkValue with the correct count, matching the native protocol.
- A restock run is no longer cancelled by a single frame in which the MerchantSetting window reports itself as not-ready while it refreshes after a slot changes; the run is only torn down once the window has really been gone for two seconds.
- The unit price is now written into the price input directly (the same way batch listing and auto-undercut already do it) and the item is skipped if that fails, instead of relying on an unverified callback that could have confirmed the listing at a stale price.
- Closing the mannequin window exactly as a run finished could throw from the cancellation token source being disposed and cancelled at the same time; the token source is now only touched under a lock.

### Changed
- Restocking now walks the manual sequence slot by slot instead of firing a burst of callbacks: take the sold-out item off the slot (confirming the prompt when the game raises one), open the equipment picker, select the item, apply the preset unit price, confirm, and wait for the slot to actually hold the item again before moving on. Each step waits on real game state rather than a fixed delay.
- When the item is not in the bag list, the equipment picker switches to the retainer tab and searches again before giving up.
- After the last slot the run ticks "只按整套出售" (answering the confirmation prompt) and presses 确定, so the shop settings are committed. Both steps can be turned off from the preset panel; the checkbox is left alone when it is already ticked, a message asks you to tick it manually if the control cannot be found, and 确定 is skipped while any dialog is still open.
- A slot that fails (item missing from the picker, a dialog that never appears) no longer aborts the whole run: stray dialogs are closed and the remaining slots are processed, with the failure count reported at the end.

## [1.4.0.2018] - 2026-09-13

### Added
- Mannequin restock preset panel: the overlay is now a collapsible panel attached beside the MerchantSetting window showing every slot with its item icon, name, an editable unit price and HQ toggle, and a colored status (在售 / 可补货 / 在雇员 / 缺装备 / 缺价格). Prices and HQ flags edited in the panel are saved per mannequin and used by one-click restock, so fully sold-out mannequins whose prices were never captured can now be restocked after filling in prices once.
- Prices are still learned automatically whenever items are seen listed; manual edits and learned prices share the same per-mannequin preset storage.

### Changed
- The restock button shows how many sold-out items are actually actionable (e.g. "一键补货 (8/10)"). Preset editing is disabled while a restock run is executing to protect the saved snapshot, and the panel clamps to the screen edges (falls back to the left side / shifts up when there is no room).

## [1.4.0.2017] - 2026-09-13

### Fixed
- The mannequin restock overlay no longer covers native controls: it now sits outside the MerchantSetting window (right-aligned below it, or above it when there is no room on screen) instead of overlapping the confirm button area.
- The overlay hides automatically while native dialogs (price adjustment, equipment selection, yes/no prompts, context menus) are open, since ImGui overlays always render above the game UI and would otherwise cover them. It stays visible during automated restocking so progress remains readable.

## [1.4.0.2016] - 2026-09-12

### Fixed
- Mannequin restock button is no longer clipped away when sold-out items are listed: the overlay previously forced a fixed 130x32 size, so everything after the first line (including the button) was cut off. The overlay now auto-sizes, anchors its bottom-right corner to the native window, and always draws the restock button first.
- Sold-out mannequin slots are now restocked correctly: the game wipes the HQ flag and the price from sold-out slots in the MerchantSetting agent data, so the plugin now saves each mannequin's prices/HQ flags automatically while items are still listed and restores them when the slots show up as sold out. Items whose price was never recorded are skipped with a clear message instead of being listed at 0 gil.
- Slot capture no longer latches an empty result when the agent data lags behind the window opening; it retries every 500ms until slots are read, and re-captures whenever the native window refreshes.
- Inventory matching falls back to ignoring the HQ flag when the recorded quality is unavailable, instead of reporting the equipment as missing.

### Changed
- Restock overlay now shows item names (with HQ tag), source, and price per sold-out slot, and the completion message reports how many items were actually relisted.
- Removed the global addon lifecycle diagnostics (every-addon PostDraw/PostRefresh logging and the 2-second poll), which were flooding dalamud.log; MerchantSetting-specific diagnostics and `/amdiag` remain.

## [1.4.0.2015] - 2026-09-12

### Fixed
- The batch listing search box is now pinned above its own scrolling item list, so scrolling the dropdown no longer drags the focused input (and the floating IME indicator) off screen.

## [1.4.0.2014] - 2026-09-12

### Added
- The batch listing item picker now has a search box that auto-focuses when the dropdown opens and filters items as you type.

### Changed
- Batch retract now removes only as many listings as the stack count input specifies (starting from the top row) instead of always retracting everything.

## [1.4.0.2013] - 2026-09-12

### Added
- Batch retract: the batch listing section gains "批量收回给雇员" and "批量收回给自己" buttons that pull every current listing back through the native context menu callbacks, aborting safely if a listing fails to disappear (for example when the target inventory is full).

### Changed
- The batch listing item picker now shows the total item quantity (e.g. "高密度轻铝矿 (999)") instead of the stack count, and the stack count input is no longer clamped by available stacks or free slots — the run simply finishes early when the inventory or the market slots run out.

## [1.4.0.2012] - 2026-09-12

### Added
- Batch listing: the retainer sell list overlay now has a "批量上架" section that lists multiple stacks of an inventory item in one click. It repeats the native put-up-for-sale flow through addon callbacks, fetches the recommended price once (optionally refreshing it from the market board first) and reuses it for every stack until the requested count, the free market slots, or the inventory stacks run out.

## [1.4.0.2011] - 2026-09-12

### Fixed
- Automatic undercut no longer requires manually viewing an item's market price first: the flow now waits (up to 10 seconds per item) for the full market board offerings batch to be processed into the price cache instead of sampling the cache for under two seconds, which always missed multi-packet listings on busy items.
- Re-click the compare-prices button once when the results window fails to open, and log a warning when no market data arrives so the fallback to cached prices is visible.

## [1.4.0.2010] - 2026-09-10

### Added
- Execute mannequin sold-out restocking for matching player-inventory equipment through the native game callbacks.
- Add step-by-step restock status, callback diagnostics, duplicate-click protection, and cancellation when the mannequin window closes.

### Limitations
- Equipment found in the active retainer inventory is reported and skipped until the retainer-bell withdrawal flow is integrated.

## [1.4.0.2009] - 2026-09-10

### Fixed
- Keep the mannequin restock button clickable while configuration or sold-out item detection is unavailable, and log the current MerchantSetting state after clicking.

## [1.4.0.2008] - 2026-09-10

### Fixed
- Bind mannequin restock detection and overlay positioning to the actual `MerchantSetting` addon used by the client.

## [1.4.0.2007] - 2026-09-10

### Added
- Added the `/amdiag` command for printing Addon lifecycle and mannequin window diagnostics to the Dalamud log.
- Logs Addon names, addresses, visibility, dimensions, node counts, Atk value counts, and text summaries.

## [1.4.0.2006] - 2026-09-10

### Fixed
- Bind the mannequin overlay directly to the `HousingMannequin` addon and poll for an already-open window after plugin reload.
- Keep the restock button overlay above the native mannequin settings window and align it with the lower-right action area.

## [1.4.0.2005] - 2026-09-10

### Added
- Added a safe MVP entry point for mannequin restocking.
- Detects the mannequin shop settings window and shows sold-out equipment sources from the player inventory or current retainer.
- Adds diagnostic logging for the detected mannequin addon and Atk values.

### Limitations
- This release does not yet automate summoning bells, retainer withdrawals, equipment selection, pricing, or relisting.
- The restock button remains unavailable until mannequin equipment and pricing have been captured through a supported configuration flow.

## [1.4.0.2004] - 2026-09-09

### Fixed
- Do not mark an item as undercut by a lower-priced listing of the other quality when matching-quality comparison is enabled.

## [4.0.2] - 2026-08-08

### Changed
- Update sig for 7.55

## [4.0.1] - 2026-05-06

### Changed
- Fix a potential crash

## [4.0.0] - 2026-05-02

### Changed
- API15 update

## [3.0.7] - 2026-03-04

### Fixed
- Fixed hooks for 7.45

## [3.0.6] - 2026-02-10

### Changed
- Added icon column to sale summary

## [3.0.5] - 2026-02-05

### Fixed
- Fixed hooks for 7.41hf1

## [3.0.4] - 2026-01-29

### Fixed
- Fixed potential crash when in moon mission

## [3.0.3] - 2026-01-29

### Fixed
- Fixed broken signature

## [3.0.2] - 2026-01-21

### Fixed
- Removing a listed item from a retainer would not be tracked correctly

## [3.0.1] - 2026-01-06

### Fixed
- Fixed an issue when a HQ listing attempts to use the NQ pricing for the recommended unit price

## [3.0.0] - 2025-22-12

### Fixed
- Support for 7.4

## [2.0.5] - 2025-10-17

### Fixed
- Fixed a broken signature, I'll see if I can make this less liable to break later

## [2.0.4] - 2025-10-08

### Fixed
- Fixed broke signature
- Changed the way the retainer order is retrieved, this should fix some edge cases where peoples retainers were in the wrong order.

## [2.0.3] - 2025-09-05

### Fixed
- Fixed signature mismatch

## [2.0.2] - 2025-08-31

### Fixed
- Fixed a bug causing features/settings to not be loaded within the wizard causing it to never close

## [2.0.1] - 2025-08-22

### Added
- Added debug windows available for end-users when debugging specific issues
- Added a /amconfig command for opening the configuration window
- If undercutting has to fall back to the NQ price a small tooltip will be displayed making it more obvious to the end user why the price was used

### Fixed
- Fixed a bug if the current sales CSV fails to parse correctly
- Fixed a bug where a retainer that you owned's listings would be ignored
- Fixed how the arrow buttons were rendered
- Changing the date in the sales summary should update the list instantly instead of needing a reorder

## [2.0.0] - 2025-08-09

### Fixed
- API13 support
- The arrow buttons inside the main UI will be weirdly sized until ArrowButton returns in a new dalamud release

