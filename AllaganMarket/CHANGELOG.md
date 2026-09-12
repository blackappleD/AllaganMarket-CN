# Changelog

All notable changes to this project will be documented in this file.

The log versioning the plugin versioning will not match as 0.0.0 technically does not match semantic versioning but the headache of trying to change this would be too much.
Instead the changelog reader and automation surrounding plugin PRs will add the back in

The format is based on [Keep a Changelog](https://keepachangelog.com/en/0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.html).

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

