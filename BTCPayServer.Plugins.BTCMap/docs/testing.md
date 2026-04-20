# BTC Map Plugin — Testing Guide

This plugin integrates with the OpenStreetMap Overpass API, which is frequently slow, rate-limited, or unavailable. It also uses OAuth against the OSM dev server (`master.apis.dev.openstreetmap.org`), which is not indexed by Overpass, so round-trip testing through real APIs is unreliable.

**Fixture mode** swaps the real `OverpassApiClient` for `FixtureOverpassApiClient` which returns hardcoded scenario data. It activates only when all three conditions are met:

1. `BTCMAP_OVERPASS_SCENARIO=<name>` environment variable is set
2. `ASPNETCORE_ENVIRONMENT=Development` (set by the `Bitcoin-HTTPS` launch profile)
3. `OsmAuthService.IsMainnet` is false (regtest, testnet, or signet)

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
3. **OSM OAuth connected.** Admin → Plugins → BTC Map → connect to OSM dev server. Only needed if you want to test Create/Link paths end-to-end; pure UI verification works without it.
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
info: [FIXTURE:fresh-cafe] SearchNearby(name='Test Cafe') → 1 elements
```

### Form inputs used by all scenarios

All scenarios are calibrated to a synthetic "fake Paris" search point:

- **Latitude:** `48.8566`
- **Longitude:** `2.3522`

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
| Business Name | `Test Cafe` (anything) |
| Category | `cafe` |
| Latitude | `48.8566` |
| Longitude | `2.3522` |

**Expected UI:**
- Page shows "No existing locations found near your coordinates."
- "Create a New Location" section visible with a green **Create New OSM Node** button.
- View the page source — the hidden `Settings.Latitude` and `Settings.Longitude` inputs must render with `.` decimal separators (`48.8566`, `2.3522`) regardless of OS locale. If they render with `,` (e.g. `48,8566`), bug 1 is not fixed.

**Happy path:** click Create New OSM Node. A pending `BtcMapListing` row is inserted, then `OsmApiClient.CreateNode()` is called against the real OSM dev server. If OAuth is connected this creates a real node and the listing transitions to `Active`. If OAuth isn't connected the listing is rolled back and you see an error banner — that's the controller's error-handling path.

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
| Business Name | anything (e.g. `Test Cafe`) |
| Category | `cafe` |
| Latitude | `48.8566` |
| Longitude | `2.3522` |

**Expected UI:** one list item with:
- Name heading: **Café de Flore**
- Category line: `Amenity: cafe · node/1000001`
- Address line: `172 Boulevard Saint-Germain, Paris, 75006, FR`
- Coordinates + distance: `48.85720, 2.35310 · ~85 m away`
- Button: solid blue **Link to this**

**Happy path:** click **Link to this**. A `BtcMapListing` row is created pointing at `osmType=node`, `osmId=1000001`. The subsequent `OsmApiClient.GetElement()` call will fail with 404 because node 1000001 doesn't exist on the real OSM dev server — **this is expected**. The test covers UI + DB insert, not the actual OSM write. For full round-trip, edit `OverpassFixtureScenarios.cs` and substitute a real osmId from a node you previously created on the dev server.

---

### 3. `already-tagged`

**What it tests:** bug 3 dedupe (same element returned from two sources collapses to one row), UX-5 link-tagged-node button (outline-primary + tooltip), `hasBtc` badge, existing-tags line.

**How to run:**
```bash
BTCMAP_OVERPASS_SCENARIO=already-tagged ./dev.sh
```

**Form inputs:**
| Field | Value |
|---|---|
| Business Name | anything |
| Category | `seafood` |
| Latitude | `48.8566` |
| Longitude | `2.3522` |

**Expected UI:** exactly **one** list item (not two — this proves dedupe) with:
- Name heading: **Satoshi's Sushi**
- Category line: `Shop: seafood · node/2000001`
- Address line: `12 Rue de Rivoli, Paris, 75004`
- Coordinates + distance: `48.85640, 2.35180 · ~45 m away`
- Yellow badge: **Already has Bitcoin tags**
- Below the badge, muted small text: `currency:XBT=yes  payment:onchain=yes  payment:lightning=yes`
- Button: **outline** blue **Link existing listing** (with tooltip on hover: "This location is already on BTC Map. Linking associates it with your BTCPay store for reverification.")

**Dedupe failure signal:** if you see two "Satoshi's Sushi" items, the `HashSet<(string, long)>` dedupe in `UIBtcMapStoreController.Search` is broken.

**Happy path:** click **Link existing listing**. A `BtcMapListing` row is created even though the node was already tagged. This validates UX-5's core requirement: merchants can claim already-tagged nodes for reverification tracking.

---

### 4. `cascading-mixed`

**What it tests:** full cascading search (name empty → address hits), bug 3 merge-tagged-first ordering across multiple source lists, `way` type with `center` fallback via `EffectiveLat`/`EffectiveLon`, mixed category keys (`amenity` vs `tourism`).

**How to run:**
```bash
BTCMAP_OVERPASS_SCENARIO=cascading-mixed ./dev.sh
```

**Form inputs:**
| Field | Value |
|---|---|
| Business Name | anything |
| Category | `bar` |
| **Street** | `Rue de Rivoli` |
| **City** | `Paris` |
| Latitude | `48.8566` |
| Longitude | `2.3522` |

⚠️ **Street and City are required** for this scenario. Without them, `BtcMapService.SearchNearby` skips the address cascade step and falls through to `SearchByCoordinates`, which is empty in this scenario — you'll see "No existing locations found."

**Expected UI:** three list items in this exact order:
1. **Bitcoin Beach Bar** (tagged, from duplicates check)
   - `Amenity: bar · node/3000001`
   - Yellow "Already has Bitcoin tags" badge
   - Existing tags line: `currency:XBT=yes  payment:lightning=yes`
   - Outline "Link existing listing" button
2. **Le Procope** (untagged restaurant, from address search)
   - `Amenity: restaurant · node/3000002`
   - Address: `13 Rue de l'Ancienne Comédie, Paris, 75006`
   - Solid "Link to this" button
3. **Jardin du Luxembourg** (untagged `way` element, from address search)
   - `Tourism: park · way/3000003`
   - Address: `Paris`
   - Coordinates from `Center` via `EffectiveLat`/`EffectiveLon` (`48.85590, 2.35080`)
   - Solid "Link to this" button

**Failure signals:**
- Bitcoin Beach Bar not at the top → merge-tagged-first logic broken
- Only two items → address cascade didn't fire (Street/City were blank)
- Jardin du Luxembourg shows no coordinates or throws → `OverpassElement.EffectiveLat`/`EffectiveLon` isn't picking up `Center`

**Log lines to watch for:**
```
info: [FIXTURE:cascading-mixed] CheckExistingBitcoinTags() → 1 elements
info: [FIXTURE:cascading-mixed] SearchNearby(name='...') → 0 elements
info: [FIXTURE:cascading-mixed] SearchByAddress(street='Rue de Rivoli', city='Paris') → 2 elements
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
| Business Name | anything (e.g. `My Bakery`) |
| Category | `bakery` |
| Latitude | `48.8566` |
| Longitude | `2.3522` |

**Expected UI:** four list items, each untagged (no yellow badge), each with a solid "Link to this" button:

1. **Boulangerie Poilâne** — `Shop: bakery · node/4000001`, address `8 Rue du Cherche-Midi, Paris, 75006`, ~20 m away
2. **Le Relais de l'Entrecôte** — `Amenity: restaurant · node/4000002`, address `20 Rue Saint-Benoît, Paris, 75006`, ~30 m away
3. **Brasserie Lipp** — `Amenity: restaurant · node/4000003`, address `151 Boulevard Saint-Germain, Paris, 75006`, ~100 m away
4. **Patisserie Stohrer** — `Shop: pastry · node/4000004`, address `51 Rue Montorgueil, Paris, 75002`, ~170 m away

**What to verify:**
- All four items render without layout issues
- Category labels show both `shop` and `amenity` keys correctly formatted (`Shop: bakery`, `Amenity: restaurant`, etc.)
- Distances span a realistic range from ~20 m to ~170 m
- No duplicate button behavior — each "Link to this" submits the correct `osmId`

**Happy path:** click **Link to this** on any item; confirm the correct `osmId` is recorded in the `BtcMapListing` row. Repeat with a different item to verify the forms don't bleed state between each other.

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
| Business Name | `Satoshi Coffee Co` (the merchant's current BTCPay store name — **doesn't match OSM**) |
| Category | `cafe` |
| **Street** | `Rue de Rivoli` |
| **City** | `Paris` |
| Latitude | `48.8566` |
| Longitude | `2.3522` |

⚠️ **Street and City are required.** Name search returns empty on purpose; without Street and City the cascade falls through to `SearchByCoordinates` which is also empty in this scenario.

**Expected UI:** one list item:
- Name heading: **Coffee on 5th** *(note: this is NOT the name the merchant typed — it's the stale OSM record)*
- Category line: `Amenity: cafe · node/5000001`
- Address line: `5 Rue de Rivoli, Paris, 75004, FR`
- Coordinates + distance visible
- Solid blue "Link to this" button

**Why this matters:** the merchant sees a name that doesn't match what they typed, and they have to trust the address match to click Link. This is exactly how the cascade fallback plays out in the real world — it's a common case where UI clarity matters most.

**Log lines to watch for:**
```
info: [FIXTURE:name-mismatch-fallback-to-address] CheckExistingBitcoinTags() → 0 elements
info: [FIXTURE:name-mismatch-fallback-to-address] SearchNearby(name='Satoshi Coffee Co') → 0 elements
info: [FIXTURE:name-mismatch-fallback-to-address] SearchByAddress(street='Rue de Rivoli', city='Paris') → 1 elements
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
Valid values: empty-everywhere, fresh-cafe, already-tagged, cascading-mixed,
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

**Clicking Link or Create fails with 404**
Expected if you're using synthetic osmIds that don't exist on the real OSM dev server. The fixture mode tests the UI + DB insert; for full OSM round-trip, substitute a real osmId from a node you previously created on the dev server into the relevant method in `OverpassFixtureScenarios.cs`.

---

## Adding a new scenario

1. Add a new private method in `OverpassFixtureScenarios.cs` that returns an `OverpassScenario` record. Populate the four lists (`Duplicates`, `NameSearch`, `AddressSearch`, `CoordinatesSearch`) with `OverpassElement` instances. Keep coordinates within a few hundred metres of `(48.8566, 2.3522)` so distances render.
2. Add the scenario name to the `Names` array (for the help message on unknown scenarios).
3. Add a new branch to the `Get()` switch.
4. Document the scenario in this file: what it tests, form inputs required, expected UI, failure signals.
5. Rebuild and run: `BTCMAP_OVERPASS_SCENARIO=<new-name> ./dev.sh`.

No DI or startup changes needed — new scenarios are pure data.
