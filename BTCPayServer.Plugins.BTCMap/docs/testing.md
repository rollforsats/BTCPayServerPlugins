# BTC Map Plugin — Testing Guide

This plugin uses the OpenStreetMap Overpass API to search for nearby places during the create flow. Overpass is frequently slow, rate-limited, or unavailable, so round-trip testing against the real API is unreliable.

Submissions themselves no longer write to OSM. They go through plugin-builder's `/apis/btcmaps/v1/submit` endpoint, which mediates btcmap-api's `submit_place` JSON-RPC and (optionally) opens a directory PR. See `BTCMAP_PLUGINBUILDER_SCENARIO` below for fixture-mode coverage of the submit lane.

**Overpass fixture mode** swaps the real `OverpassApiClient` for `FixtureOverpassApiClient` which returns hardcoded scenario data for the search step. It activates only when all three conditions are met:

1. `BTCMAP_OVERPASS_SCENARIO=<name>` environment variable is set
2. `ASPNETCORE_ENVIRONMENT=Development` (set by the `Bitcoin-HTTPS` launch profile)
3. `BTCPayNetworkProvider.NetworkType` is not Mainnet (regtest, testnet, or signet)

If the env var is set but the other two checks fail, the plugin **refuses to start** with a loud `InvalidOperationException`. Test data cannot leak into production.

Fixture-mode code lives under `Services/`:
- `IOverpassApiClient.cs` — interface
- `OverpassApiClient.cs` — real implementation (HTTP against `overpass-api.de`)
- `FixtureOverpassApiClient.cs` — dev-only fake
- `OverpassFixtureScenarios.cs` — hardcoded scenario data
- `Plugin.cs` — conditional DI registration

## Prerequisites

1. **Running dev environment.** This repo ships a `dev.sh` at the root that builds all plugins and launches BTCPay via the `Bitcoin-HTTPS` launch profile (defined in `btcpayserver/BTCPayServer/Properties/launchSettings.json`). BTCPay listens on `https://localhost:14142/` with a self-signed dev cert.
2. **Postgres running** in the `btcpayservertests-postgres-1` Docker container on port 39372.
3. **Plugin-builder token configured** (only if exercising the submit lane end-to-end). The token is set per-store in the plugin settings UI and routes through BTCPay's `PoliciesSettings.PluginSource` to the configured plugin-builder. Pure UI verification of the search/picker step works without it; use `BTCMAP_PLUGINBUILDER_SCENARIO` to fake submit responses without a real token.
4. **Plugin not disabled.** If BTCPay previously crashed on this plugin, it will have written `disable:BTCPayServer.Plugins.BTCMap` to `~/.btcpayserver/Plugins/commands`. Delete that file before starting.

## How to run a scenario

Launch the dev environment with the scenario env var prepended:

```bash
BTCMAP_OVERPASS_SCENARIO=fresh-cafe ./dev.sh
```

**Do not** use raw `dotnet run` — it bypasses the launch profile and BTCPay won't load plugins (`BTCPAY_DEBUG_PLUGINS` is unset), won't bind the HTTPS cert, and won't connect to regtest.

Open `https://localhost:14142/` in a browser (accept the self-signed cert once per session).

### Verify fixture mode is active

Watch the `dev.sh` console during plugin load for this warning:

```
warn: BTCPayServer.Plugins.BTCMap.Services.FixtureOverpassApiClient[0]
      Overpass fixture mode ACTIVE — scenario 'fresh-cafe'. All Overpass search calls will return hardcoded data.
```

If you don't see it, either the env var didn't reach the child process or the plugin DLL is stale — stop `dev.sh` fully (Ctrl+C) and restart.

On each search request, the fake logs which bucket was hit and how many elements it returned:

```
info: [FIXTURE:fresh-cafe] CheckExistingBitcoinTags() → 0 elements
info: [FIXTURE:fresh-cafe] SearchNearby(name='Bitcoin Cafe') → 1 elements
```

### Form inputs used by all scenarios

All scenarios are calibrated to a synthetic Coronado, CA search point (938 Ocean Blvd):

- **Latitude:** `32.6838298`
- **Longitude:** `-117.1839771`

Enter those exact values in the BTC Map store page form. Distances in the search results are computed against this point.

## Scenarios

### 1. `empty-everywhere`

**What it tests:** bug 1 — culture-invariant decimal rendering in the `SearchResults.cshtml` hidden inputs. Forces the Create path because no search results are returned.

**How to run:**
```bash
BTCMAP_OVERPASS_SCENARIO=empty-everywhere ./dev.sh
```

**Form inputs:**
| Field | Value |
|---|---|
| Business Name | `Bitcoin Cafe` (anything) |
| Category | `cafe` |
| Latitude | `32.6838298` |
| Longitude | `-117.1839771` |

**Expected UI:**
- Page shows "No existing locations found near your coordinates."
- "Create a New Location" section visible with a green **Create New Listing** button.
- View the page source — the hidden `Settings.Latitude` and `Settings.Longitude` inputs must render with `.` decimal separators (`32.6838298`, `-117.1839771`) regardless of OS locale. If they render with `,` (e.g. `32,6838298`), bug 1 is not fixed.

**Happy path:** click Create New Listing. The controller calls `_btcMapService.SubmitListing`, which posts to plugin-builder's `/apis/btcmaps/v1/submit`. With `BTCMAP_PLUGINBUILDER_SCENARIO` unset, the call hits the real plugin-builder; pair this with a `BTCMAP_PLUGINBUILDER_SCENARIO=btcmap-only-success` (or `both-lanes-success` when the directory checkbox is also ticked) to exercise the rest of the flow without a real token. On success a `BtcMapListing` row is persisted with `Status = Active` + `BtcMapSubmissionId` populated; on `PluginBuilderApiException` the row is rolled back via the snapshot pattern and you see an error banner.

---

### 2. `fresh-cafe`

**What it tests:** UX-5 display (name, category, address, distance, coordinates all render), Link path for a fresh untagged node.

**How to run:**
```bash
BTCMAP_OVERPASS_SCENARIO=fresh-cafe ./dev.sh
```

**Form inputs:**
| Field | Value |
|---|---|
| Business Name | anything (e.g. `Bitcoin Cafe`) |
| Category | `cafe` |
| Latitude | `32.6838298` |
| Longitude | `-117.1839771` |

**Expected UI:** one list item with:
- Name heading: **Bitcoin Burgers**
- Category line: `Amenity: cafe · node/1000001`
- Address line: `1100 Orange Ave, Coronado, 92118, US`
- Coordinates + distance: `32.68396, -117.18383 · ~20 m away`
- Button: solid blue **Use this location**

**Happy path:** click **Use this location**. The picker prefills the create form with the selected element's name/category/address/coordinates and routes the merchant into the same `CreateNew` action. Submission then goes through plugin-builder (pair with a `BTCMAP_PLUGINBUILDER_SCENARIO` if you don't want a real token round-trip). The synthetic node id `1000001` is used only for picker prefill display — the plugin no longer writes to OSM, so there is no OSM round-trip step on this path.

---

### 3. `already-tagged`

**What it tests:** bug 3 dedupe (same element returned from two sources collapses to one row), `hasBtc` badge, existing-tags line. Awareness signal: merchant sees the element is already on BTC Map before re-submitting.

**How to run:**
```bash
BTCMAP_OVERPASS_SCENARIO=already-tagged ./dev.sh
```

**Form inputs:**
| Field | Value |
|---|---|
| Business Name | anything |
| Category | `seafood` |
| Latitude | `32.6838298` |
| Longitude | `-117.1839771` |

**Expected UI:** exactly **one** list item (not two — this proves dedupe) with:
- Name heading: **Bitcoin Sushi**
- Category line: `Shop: seafood · node/2000001`
- Address line: `868 Orange Ave, San Diego, 92107, US`
- Coordinates + distance: `32.68786, -117.17920 · ~530 m away`
- Yellow badge: **Already on BTC Map**
- Below the badge, muted small text: `currency:XBT=yes  payment:onchain=yes  payment:lightning=yes`
- Button: solid blue **Use this location** (same button as untagged elements; the badge is informational only)

**Dedupe failure signal:** if you see two "Bitcoin Sushi" items, the `HashSet<(string, long)>` dedupe in `UIBtcMapStoreController.Search` is broken.

**Happy path:** click **Use this location**. The picker prefills the create form with the element's name/category/address/coordinates and routes through `CreateNew` → plugin-builder. The plugin does not distinguish "already on BTC Map" from "fresh" at submit time — both produce a `submit_place` upsert (btcmap-api's `(origin, external_id)` upsert handles dedup server-side when the same store later resubmits). The "Already on BTC Map" badge is purely a heads-up that the merchant's location may already exist on the public map; submitting still creates the BTCPay-origin record on the moderator queue.

---

### 4. `cascading`

**What it tests:** full cascading search (name empty → address hits), bug 3 merge-tagged-first ordering across multiple source lists, mixed category keys (`amenity` vs `shop`).

**How to run:**
```bash
BTCMAP_OVERPASS_SCENARIO=cascading ./dev.sh
```

**Form inputs:**
| Field | Value |
|---|---|
| Business Name | anything |
| Category | `bar` |
| **Street** | `Orange Ave` |
| **City** | `Coronado` |
| Latitude | `32.6838298` |
| Longitude | `-117.1839771` |

⚠️ **Street and City are required** for this scenario. Without them, `BtcMapService.SearchNearby` skips the address cascade step and falls through to `SearchByCoordinates`, which is empty in this scenario — you'll see "No existing locations found."

**Expected UI:** two list items in this exact order:
1. **Bitcoin Beach Bar** (tagged, from duplicates check)
   - `Amenity: bar · node/3000001`
   - Yellow "Already on BTC Map" badge
   - Existing tags line: `currency:XBT=yes  payment:lightning=yes`
   - Solid "Use this location" button
2. **Bitcoin Brewery** (untagged restaurant, from address search)
   - `Amenity: restaurant · node/3000002`
   - Address: `1134 Orange Ave, Coronado, 92118, US`
   - Solid "Use this location" button

**Failure signals:**
- Bitcoin Beach Bar not at the top → merge-tagged-first logic broken
- Only one item → address cascade didn't fire (Street/City were blank)

**Log lines to watch for:**
```
info: [FIXTURE:cascading] CheckExistingBitcoinTags() → 1 elements
info: [FIXTURE:cascading] SearchNearby(name='...') → 0 elements
info: [FIXTURE:cascading] SearchByAddress(street='Orange Ave', city='Coronado') → 1 elements
```

If the third line is missing, the cascade didn't escalate to the address step.

---

### 5. `multiple-nearby-untagged`

**What it tests:** realistic disambiguation case — several untagged businesses within Overpass's 200m radius, user must visually pick the right one. Exercises list layout at common-use scale (4 items) and varied category labels.

**How to run:**
```bash
BTCMAP_OVERPASS_SCENARIO=multiple-nearby-untagged ./dev.sh
```

**Form inputs:**
| Field | Value |
|---|---|
| Business Name | anything (e.g. `Bitcoin Pizza`) |
| Category | `bakery` |
| Latitude | `32.6838298` |
| Longitude | `-117.1839771` |

**Expected UI:** four list items, each untagged (no yellow badge), each with a solid "Use this location" button:

1. **Bitcoin Pizza** — `Shop: bakery · node/4000001`, address `1100 Orange Ave, Coronado, 92118`, ~20 m away
2. **Bitcoin Coffee** — `Amenity: restaurant · node/4000002`, address `1134 Orange Ave, Coronado, 92118`, ~30 m away
3. **Bitcoin Barbecue** — `Amenity: restaurant · node/4000003`, address `1107 Orange Ave, Coronado, 92118`, ~100 m away
4. **Bitcoin Tacos** — `Shop: pastry · node/4000004`, address `1031 Orange Ave, Coronado, 92118`, ~170 m away

**What to verify:**
- All four items render without layout issues
- Category labels show both `shop` and `amenity` keys correctly formatted (`Shop: bakery`, `Amenity: restaurant`, etc.)
- Distances span a realistic range from ~20 m to ~170 m
- No duplicate button behavior — each "Use this location" submits the correct prefilled coordinates / category / address

**Happy path:** click **Use this location** on any item; confirm the create form is prefilled with that item's coordinates and address. Repeat with a different item to verify the forms don't bleed state between each other.

---

### 6. `name-mismatch-fallback-to-address`

**What it tests:** the core reason the cascading search exists — a business has been renamed or OSM data is out of date, so exact name matching fails and the address step has to rescue the search. One untagged result comes back from the address cascade only.

**How to run:**
```bash
BTCMAP_OVERPASS_SCENARIO=name-mismatch-fallback-to-address ./dev.sh
```

**Form inputs:**
| Field | Value |
|---|---|
| Business Name | `Bitcoin Cafe` (the merchant's current BTCPay store name — **doesn't match OSM**) |
| Category | `cafe` |
| **Street** | `Orange Ave` |
| **City** | `Coronado` |
| Latitude | `32.6838298` |
| Longitude | `-117.1839771` |

⚠️ **Street and City are required.** Name search returns empty on purpose; without Street and City the cascade falls through to `SearchByCoordinates` which is also empty in this scenario.

**Expected UI:** one list item:
- Name heading: **Bitcoin Diner** *(note: this is NOT the name the merchant typed — it's the stale OSM record)*
- Category line: `Amenity: cafe · node/5000001`
- Address line: `1134 Orange Ave, Coronado, 92118, US`
- Coordinates + distance visible
- Solid blue "Use this location" button

**Why this matters:** the merchant sees a name that doesn't match what they typed, and they have to trust the address match to click the button. After clicking, the create form is prefilled with the OSM element's coordinates and address; the merchant's typed business name (`Bitcoin Cafe`) is preserved on the form so the submission carries the merchant-supplied name, not the stale OSM record.

**Log lines to watch for:**
```
info: [FIXTURE:name-mismatch-fallback-to-address] CheckExistingBitcoinTags() → 0 elements
info: [FIXTURE:name-mismatch-fallback-to-address] SearchNearby(name='Bitcoin Cafe') → 0 elements
info: [FIXTURE:name-mismatch-fallback-to-address] SearchByAddress(street='Orange Ave', city='Coronado') → 1 elements
```

All three lines must appear — that's the full cascade executing as designed.

---

## Gating (failure cases that SHOULD refuse to start)

These verify that the triple-gate in `Plugin.cs` actually refuses to serve fake data outside of valid dev conditions.

### Unknown scenario name

```bash
BTCMAP_OVERPASS_SCENARIO=bogus ./dev.sh
```

Startup succeeds. The first Search click throws:

```
ArgumentException: Unknown BTCMAP_OVERPASS_SCENARIO 'bogus'.
Valid values: empty-everywhere, fresh-cafe, already-tagged, cascading,
multiple-nearby-untagged, name-mismatch-fallback-to-address
```

The exception bubbles through `UIBtcMapStoreController.Search` into `TempData["StatusMessage"]` and renders as a red error banner on Index.

### Production environment

The `Bitcoin-HTTPS` profile hardcodes `ASPNETCORE_ENVIRONMENT=Development`, so to force Production you need to temporarily edit `launchSettings.json` (change line 58 to `"ASPNETCORE_ENVIRONMENT": "Production"`) and then run:

```bash
BTCMAP_OVERPASS_SCENARIO=fresh-cafe ./dev.sh
```

The plugin should fail to start with:

```
InvalidOperationException: BTCMAP_OVERPASS_SCENARIO='fresh-cafe' refused:
ASPNETCORE_ENVIRONMENT is not Development
```

Revert the launch profile edit after testing.

### Mainnet

Switch to the mainnet launch profile (not the default regtest one) and set the env var. The plugin should fail to start with:

```
InvalidOperationException: BTCMAP_OVERPASS_SCENARIO='fresh-cafe' refused:
running on mainnet
```

---

## Troubleshooting

**Browser shows `SSL_ERROR_RX_RECORD_TOO_LONG` on `https://localhost:14142`**
BTCPay isn't listening on HTTPS — most likely it crashed during startup, or was launched without the `Bitcoin-HTTPS` launch profile. Check the `dev.sh` console for exceptions and look for `Now listening on: https://localhost:14142`.

**"Plugin commands" disable file**
If BTCPay crashed mid-startup with the plugin, it writes `disable:BTCPayServer.Plugins.BTCMap` to `~/.btcpayserver/Plugins/commands`. Delete that file before restarting:
```bash
rm -f ~/.btcpayserver/Plugins/commands
```

**No "fixture mode ACTIVE" log line**
Either the env var didn't reach the `dotnet watch` child process (make sure it's prepended *before* `./dev.sh`, not after), or the plugin DLL is stale. `dotnet watch` doesn't always pick up new `.cs` files in `Services/` — stop `dev.sh` fully (Ctrl+C) and restart.

**Fixture mode active but Search throws an unexpected error**
Likely a scenario data problem, not a DI problem. Check the `dev.sh` console for the inner exception from `FixtureOverpassApiClient` — it logs the stack trace at the point where deserialization or iteration failed.

**Clicking Use this location or Create New Listing returns a plugin-builder error**
The picker step is offline-safe (Overpass fixtures only), but the submit step calls plugin-builder. If you don't want a real token round-trip, set `BTCMAP_PLUGINBUILDER_SCENARIO` to one of the available scenarios (e.g. `btcmap-only-success`, `both-lanes-success`, `btcmap-upstream-failed`, `rate-limited`) — see `PluginBuilderApiFixtureScenarios.cs` for the full list. Without that env var, errors like `btcmap-not-configured` or `directory-not-configured` indicate the token is missing on the real plugin-builder instance.

---

## Adding a new scenario

1. Add a new private method in `OverpassFixtureScenarios.cs` that returns an `OverpassScenario` record. Populate the four lists (`Duplicates`, `NameSearch`, `AddressSearch`, `CoordinatesSearch`) with `OverpassElement` instances. Keep coordinates within a few hundred metres of `(32.6838298, -117.1839771)` so distances render.
2. Add the scenario name to the `Names` array (for the help message on unknown scenarios).
3. Add a new branch to the `Get()` switch.
4. Document the scenario in this file: what it tests, form inputs required, expected UI, failure signals.
5. Rebuild and run: `BTCMAP_OVERPASS_SCENARIO=<new-name> ./dev.sh`.

No DI or startup changes needed — new scenarios are pure data.
