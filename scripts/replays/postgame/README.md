# Verified postgame replay CLI (Windows x64)

One completed match, one **explicit user choice**: Download, or a local `.dem` / `.dem.bz2`. No live store, model, Steam login, game connection, GSI, observer client, background polling or match discovery. The worker cannot authorize a UI action: the parent must independently require a sealed match **before launch and before import**. Never pass this all-map data to a live model or present it as what a player knew.

## Parent handoff

**Ship the whole portable directory, not the bootstrap EXE alone.** The farm-enabled runtime is `.tmp/postgame-replay-v2/`; nothing here installs into a WPF package. The production pin for its `sha256.json` is **`BFE9801AFB125616B5C9208DA9CBD47A3BE48F0A59172C74DA7279980629D783`** (619 listed files). Check the manifest against that trusted value, check every member, and reject unlisted files/reparse points before launching. A manifest supplied by an untrusted download is not itself a trust anchor. The EXE needs no installed Python, Node, npm, .NET or network for local input.

The original `.tmp/postgame-replay-v1/` remains unchanged. Runtime **v2** is not a new wire version: `schemaVersion:1` and all five event kinds remain. The updated native importer accepts original, unmodified v1 files (no `economy` key). See **[VALIDATION.md](VALIDATION.md)** for the real importer and field-parity results.

```text
PostgameReplay.exe --match-id ID (--download | --dem FILE) --output FILE [--cancel-file FILE]
```

`ID` is canonical positive decimal, at most `9007199254740991`. `--output` is a **new** `.json` in an existing local directory. Paths may contain spaces. Do not supply a network drive, UNC path, device, alternate data stream or Windows reparse point.

```powershell
# Example: choose a fresh output filename. This command makes no network requests.
& .\.tmp\postgame-replay-v2\PostgameReplay.exe --match-id 9097559249 `
  --dem .\.tmp\farm742\9097559249.dem --output .\.tmp\review-9097559249.json
# Only on the user's Download action: replace "--dem <path>" with "--download".
```

For C#, set `ProcessStartInfo.FileName` to the absolute verified `PostgameReplay.exe`; use `UseShellExecute=false`, `CreateNoWindow=true` and `ArgumentList` (one argument per entry, no manual quoting). Give each job its own private local work directory, output path and initially absent cancel marker. Do not pass credentials or application environment. Drain both redirected streams asynchronously.

| Observable | Contract |
|---|---|
| stdout | Empty, except `--help`. Never a stream of unverified events. |
| stderr | UTF-8/ASCII NDJSON. `type:"progress"` stages `download`, `archive`, `extract`, `copy`, `decompress`, `verify`, `parse` (`tick`, `totalTicks`), `serialize`. Heartbeats may include `elapsedSeconds`. `type:"error"` has `stage` and `message`. |
| Success | Exit **0** and `{"type":"success","stage":"published","matchId":"…","bytes":…}` after atomic, **no-overwrite** publication. Check the file size, then use the strict importer. |
| Failure | **2** arguments/config/existing output; **3** acquisition/expired URL; **4** unverifiable input, decompression or parser/worker failure; **5** supervisor I/O/deadline; **130** cancelled. Never import on a nonzero exit. |
| Cancellation | Create the marker file and await process exit. The supervisor polls at 100 ms, kills/waits for its worker and removes its scratch; the marker is **caller-owned**. Ctrl+C/Break also cancels. A cancellation after the atomic commit is too late; the parent can still discard its job output instead of importing it. |
| Forced termination | On timeout after graceful cancellation, kill the **entire process tree**. A Windows kill-on-close job also bounds descendants. After exit, clean **only that caller-created job directory**. Power loss or forced termination can leave `.postgame-*` scratch; it is never an import candidate. |

## What “verified” means

* Download: exactly one bounded GET of `https://api.opendota.com/api/matches/{id}`. Require the matching ID, completed result, positive duration and a start/end in the past before requesting the archive. Accept only the exact metadata-provided `http[s]://replay<digits>.valve.net/570/{id}_<numeric-salt>.dem.bz2`. No URL input, redirects, credentials, proxy environment, retry, salt guessing, parse request or fallback host. `acquire.mts` calls the existing `../download.ts` with the bytes already read; it **does not fetch metadata twice**. An unavailable, expired or ineligible match fails; the user can select a local copy instead.
* Local: take a size-bounded private snapshot; no Node process or network. Local `.dem.bz2` and downloaded archives both go through **`../decompress.py`**: hash the archive, check its *magic* (bzip2 or Zstandard), bound decompression and hash the DEM. Trust neither extension nor filename. Verify the actually parsed DEM hash again.
* Before parsing: `verify.py` validates Source 2 magic, a SourceTV header, signon/schema frames, bounded monotone outer frames, stop and header-indexed trailers through exact EOF. Require the indexed FileInfo's **full 64-bit** match ID, winner, five heroes per side, 30-Hz playback time, playback ticks and packet count. A clean cut at a packet boundary, even after the ancient, is not sufficient. This is deliberately narrower than “anything gem can open”.
* During/after parsing: run `gem-dota==0.14.0` from the start **through the trailer**. Require no `parse_error`/truncation, the final tick, the observed game-start clock and `POST_GAME` combat-log marker, and matching internal match IDs (including embedded match details when present). Cross-check the ten terminal selected hero entities against the FileInfo roster, and require all ten in samples in both halves of the game. Fail the whole job on a failed check or exceeded budget. Not all patches, arcade modes, client POVs or partial demos are supported.
* `replaySha256` identifies the exact **uncompressed input bytes**, not the JSON or HTTP archive. This is structural, identity and completion verification, **not a Valve signature**. Some replay URLs use unauthenticated HTTP; a hash cannot establish origin/authenticity.

**Limits:** 8 MiB metadata / 30 s; 512 MiB archive / 300 s (335 s total acquisition supervisor); 2 GiB DEM / 120 s decompression; 16 MiB per outer frame (stored and Snappy-decoded), 4 GiB cumulative outer expansion, 750,000 outer frames, 6 hours of playback at 30 Hz. Extraction including copy/parse/serialization has a 900 s wall deadline and 1.5 GiB per-process Windows commit cap. At most 50,000 events, 32 item slots per hero, 32 MiB output. Budget failures are **errors**, never truncated successful timelines. Allow 3 GiB of temporary disk for Download/compressed input. Raw metadata/archive/DEM are removed before publication; the user-selected original is never edited.

## Importer v1 and coverage

UTF-8, no BOM, finite numbers, exactly:

```text
{schemaVersion:1, matchId:string, complete:true, replaySha256:64 lowercase hex,
 source:"postgame-replay", events:[{clock:number, kind:string, data:object}, ...]}
```

`clock` is **horn-relative in-game seconds**, rounded to milliseconds, mapped using the **finished, pause-aware** game clock. Pregame is negative; time freezes during recorded pauses. It is the observation packet's clock, not a wall-clock timestamp, raw tick/30, combat-log timestamp or player's discovery time. Events are in stable, nondecreasing clock order (ties allowed); `data.tick` is the DEM tick. Coordinates are centered Dota world X/Y: `cell*128 + localVector - 16384` per axis, not minimap pixels/OpenDota cell indices. Missing fields are **null**, not zero. `complete:true` describes the verified **parse**, not every entity, field or server tick.

| Kind | Data and sampling |
|---|---|
| `replay-hero` | `tick, playerId, hero, team, x, y, level, hp, maxHp, alive, items, terminal`. Ten slots (`0..9`), both teams (`2` Radiant, `3` Dire). Read selected handles including serials from PlayerResource; never substitute a similarly named illusion. Sample reconstructed state at the first completed packet in each five-second bucket of the rounded raw engine clock, plus the completed POST_GAME packet. This includes the pre-horn period; an unresolved slot produces a null-state row. `alive` derives from explicit life state, waiting-to-spawn and HP, independently of HP reporting. `items` is the observed entire `m_hItems` vector in **actual slot order**: `item_…` = resolved, `"empty"` = explicit null handle, JSON `null` = unresolved slot; `items:null` = unreadable vector. Not purchases, charges, a movement track between samples or exact death/respawn instants. |
| `replay-ward` | `tick, wardId, action, evidence, type, team, ownerPlayerId, x, y, visibleRadiant, visibleDire`. Observer/sentry entity lifecycle, no map/side/hero filter. `placed` = first observed alive; `removed` = observed dying/dead transition or entity deletion (`evidence` distinguishes them). Leaving network scope alone is **not** a kill. ID is `index:serial:placementTick`, not a recycled entity index. Team visibility comes from the actual packet-end `m_bNPCVisibleState` bit for that entity (`null` if no evidence), not proximity, an ownership guess, or proof an individual saw it. Only sampled at these transitions. No invented expiry, killer or terminal removal. |
| `replay-kill` | `tick, target, attacker, damageSource, attackerTeam, targetTeam`. Combat-log DEATH targeting a roster hero, explicitly hero/non-illusion; exclude reincarnation triggers and ambiguous target flags. Attacker and damage source are separate reported units, not inferred player kill credit. Not a universal scoreboard. |
| `replay-objective` | Combat-log deaths: `tick, type, target, attacker, damageSource, attackerTeam, targetTeam`, where `type` is `tower`, `barracks`, `ancient`, `roshan`, `tormentor` or `courier`. Typed Aegis notifications: `tick, type, playerId` with **`aegis-pickup`, `aegis-stolen`, `aegis-denied`**. No player chat, inferred objectives or “none happened” claims when the log is silent. |
| `replay-end` | Exactly one, last: `tick, winnerTeam, gameBuild, parser, hornTick, playbackTicks, playbackFrames, pauseIntervals, clockBasis, coverage`. `coverage.hero` gives cadence, frame count, known row counts per field and known/read item-slot counts. `coverage.ward.observed` counts reported transitions/hidden placements; `openAtEnd` counts observed lifetimes with **no observed removal by POST_GAME**, not a census of wards still alive. `coverage.kill.excluded` counts classified exclusions. `coverage.objective.basis` states the sources. `observedEvents` counts emitted nonterminal kinds (including terminal hero snapshots, excluding `replay-end`). An absent counter means no observations counted; it does not establish a real-world zero. |

No arbitrary entities, chat text, Steam/account IDs, inventories of couriers, every-tick vision traces or continuous trajectory data are exported.

### Optional replay-hero `data.economy`

Same five-second packet samples and terminal flush; **never** live, interpolated, backfilled, or forward-filled by the exporter. Omit the whole key on preselection, an unverifiable join, or no known fields. Otherwise emit only observed nonnegative 32-bit integers; missing keys are unknown, explicit `0` is known. Example from Dire's terminal Troll Warlord:

```json
{"lastHits":187,"netWorth":12549,"totalEarnedGold":12600,"creepKillGold":5807,"denies":10,"currentXP":15737}
```

| Key | Direct field and meaning |
|---|---|
| `lastHits` | `m_iLastHitCount`: player's recorded last-hit count. Not a count of just lane creeps. |
| `netWorth` | `m_iNetWorth`: sampled net worth; can fall. Not the wallet. |
| `totalEarnedGold` | `m_iTotalEarnedGold`: accumulated earnings counter. **Not spendable gold**, `gold_per_min * time`, or OpenDota's `total_gold` summary. |
| `creepKillGold` | `m_iCreepKillGold`: recorded creep-kill gold bucket, not all farming income. Neutral-kill gold has a separate field, which is not combined or exported here. |
| `denies` | `m_iDenyCount`: recorded deny count. |
| `currentXP` | `m_iCurrentXP` on the selected hero. Raw XP reading, not XP to next level, estimated XP from level/XPM, or a guaranteed cumulative definition across builds. |

The join uses the selected serial-checked hero handle's **resource row**, its reported team and `m_iTeamSlot`, the correct side's team-data entity, and that slot's independent `m_nPlayerID`. Player ID, team slot and resource-array index are not interchangeable. No spendable gold, jungle estimates or skills are inferred. Counts for each field on the real fixture are in `tests/fixtures/9097559249.json`, not a new event or top-level schema field.

## Offline build and regression

From the repository root:

```powershell
$py = '.\.tmp\replay-tools\Scripts\python.exe'
& $py -IB scripts\replays\postgame\build_windows.py --output .tmp\postgame-replay-v2
# Existing destination is refused. For a repeat build use a fresh subdirectory under .tmp.
Get-FileHash .tmp\postgame-replay-v2\sha256.json

# Source-mode equivalent if the parent explicitly configures this exact Python interpreter:
& $py -IB scripts\replays\postgame\cli.py --match-id 9097559249 `
  --dem .tmp\farm742\9097559249.dem --output .tmp\review-source.json
```

The builder only reads the local Windows x64 CPython **3.13.13** installation, the existing `../requirements.txt` venv and **Node 24.20.0** win-x64 (`--node PATH` if necessary, with its sibling LICENSE). No installation/download at build, launch or parse time. It verifies the tested interpreter/launcher/Node binary hashes and wheel payloads. The relocatable folder contains isolated CPython/DLLs/zipped stdlib, **only** the needed gem, numpy, protobuf, snappy, cramjam and zstandard wheel payloads, and a private Node binary for the existing downloader. No pandas/protoc/pip/compiler/venv or system PATH dependency in the shipped CLI. The distlib launcher, runtime and included wheels retain their notices; the two missing upstream gem notices in `licenses/` are from its `v0.14.0` tag. The build manifests all files with stable member order and fixed ZIP timestamps. This is a folder distribution, not a single-file EXE.

```powershell
& $py -IB scripts\replays\postgame\tests\test_contract.py -v
node --test scripts\replays\postgame\tests\acquire.test.mts
$env:POSTGAME_TEST_EXE = (Resolve-Path .tmp\postgame-replay-v2\PostgameReplay.exe).Path
& $py -IB scripts\replays\postgame\tests\test_real.py -v
& $py -IB scripts\replays\postgame\tests\test_farm_evidence.py .tmp\review-9097559249.json
```

The real suite **fails** if the pinned DEM is absent; it never downloads a substitute. It tests the portable launcher, a local Valve-compressed copy, and the actual Download/verify/decompress path using the cached public HTTP bodies with a **test-only transport**. It also cancels a real parse and corrupts the **last gameplay packet after POST_GAME**, preserving the complete trailer/ID/winner: neither publishes a timeline. The separate availability probe in the validation note is point-in-time evidence, not a guarantee of retention. `tests/importer/ImporterContract.csproj` separately links the actual native `MatchArchive` for a throwaway seal/import/re-read check; it is not part of the CLI or its build. For example:

```powershell
dotnet build scripts\replays\postgame\tests\importer\ImporterContract.csproj --artifacts-path .tmp\postgame-contract
dotnet .tmp\postgame-contract\bin\ImporterContract\debug\ImporterContract.dll .tmp\review-9097559249.json .tmp\postgame-qa\9097559249.timeline.json
```

### Verified real coverage: match `9097559249`, build 6986, Dire victory

Machine-readable expectations: `tests/fixtures/9097559249.json`. Input: `.tmp/farm742/9097559249.dem`, **60,760,549 bytes**, SHA-256 `6479cc80ddb51da5c4e7f8a98bf024509cd883cd2e83f2c083b23bb9629770eb`. Farm-enabled output: **3,599 events / 2,037,899 bytes**, SHA-256 `8bcd9ca84b345e8ff2fbaa54faa714645d9b9bda29d637aa9c2604fd42a69d9f`.

* Earliest sample **−212.933 s**, first resolved heroes **−88.267 s**, end **1,487.433 s** (24:47.433); POST_GAME tick **51,116**, continued parsing to final tick **55,464**, **27,732** ordinary packet frames. One 103-tick pause: samples at ticks 26,392 and 26,644 are only **4.967 game seconds** apart, not 8.4 seconds.
* **342** sample boundaries / **3,420** hero rows; **3,170** resolved rows, **1,585 per team**, all with X/Y, level, HP, max HP, alive and inventory. **250 unresolved pre-selection rows**, not ten dead heroes. **2,960 alive / 210 dead** resolved rows; **79,250/79,250** declared item slots resolved to a name or explicit empty sentinel. **590 hero rows after 20:00**, including all ten terminal heroes. The ten final levels and the **38** confirmed hero deaths (by victim) agree with the cached public match.
* **65 ward placements** (23 observers, 42 sentries), **54 removals** (21 observers, 33 sentries), **11** without a removal by POST_GAME. Entity handles match the cached OpenDota logs through the match end; five later sentry removals are outside gameplay. Positions/owner/both team visibility flags known on all 119 emitted transitions in this fixture. **55 placements** had a known-clear opposing visibility bit.
* Concrete fogged ward: Dire observer `669:356:4820`, owner slot 8, **(−1768.53125, −160.15625)** at **−52.333 s**, removed at **307.667 s**. In visibility word 10, bit 29 was clear in Radiant's `262184` and set in Dire's `536871040`. Cached OpenDota independently records handle `5833373`. This is replay evidence, not a ward estimate.
* **21 objectives**: 11 towers, two barracks, four couriers, one each of ancient, Roshan, Tormentor and Aegis pickup. No claims about unsupported event categories.
