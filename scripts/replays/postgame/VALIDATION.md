# Release check — 2026-11-22, Windows x64

## Runtime v2: replay-only farm samples (wire schema remains 1)

`.tmp/postgame-replay-v2/PostgameReplay.exe`, manifest SHA-256 **`BFE9801AFB125616B5C9208DA9CBD47A3BE48F0A59172C74DA7279980629D783`**. Both runtime trees passed a full 619-file manifest check. The only changed runtime payload versus preserved v1 is `timeline.py`. Local QA output `.tmp/postgame-qa/9097559249.farm-v2.json`: **3,599 events, 2,037,899 bytes**, SHA-256 **`8bcd9ca84b345e8ff2fbaa54faa714645d9b9bda29d637aa9c2604fd42a69d9f`**. Removing just the optional `economy` objects gives the original v1 document.

* All six fields (`lastHits`, `netWorth`, `totalEarnedGold`, `creepKillGold`, `denies`, `currentXP`) were reported on **3,170** samples each (1,585 Radiant, 1,585 Dire); absent on **250** preselection rows. Pregame through 24:47.433, including 590 post-20-minute hero rows; ward, kill, objective and clock evidence unchanged.
* A second full DEM parse read the two team arrays in the opposite direction, by their recorded player IDs **before the next packet's deltas**. **19,020/19,020** readings matched, including hero `m_iCurrentXP` against independent team `m_iTotalEarnedXP` on this build. No inferences about XP resets on other builds.
* Original `extract.py` facts: all **40/40** terminal shared counters match exactly by hero, side and slot. Across 2,990 aligned player/ticks, `lastHits`, `totalEarnedGold`, and `creepKillGold` always fall within the adjacent original one-second, pre-delta readings. Net worth does so for 2,981; nine transient packet-end readings fall outside those two snapshots. All nine matched the direct packet-boundary check; no smoothing or replacement.
* Cached OpenDota: all ten terminal last-hit, deny, net-worth and creep-gold (`gold_reasons["13"]`) values match. Every one of **250** player/minute observations of each `lh_t`, `dn_t`, `gold_t`, `xp_t` falls between its bounding replay samples. Terminal `total_gold` and `total_xp` are **not** substituted: e.g. Radiant slot 0 had **6,762 earned, 6,111 net worth, 9,342 raw XP** in the DEM; cached `gold=1821`, `total_gold=6741`, `total_xp=9318`. Spendable gold is not exported.

```text
5 real replay workflow tests: OK (including full cached Download, cancellation and corrupt postgame tail)
3 farm evidence/legacy byte-parity tests: OK
9 Python contract/limits tests: OK
Imported 3599 real events, 3170 farm rows; live-only read stayed empty. Legacy fixture checked: True; sparse and invalid counter cases passed.
```

**Fresh public Download, v2 (2026-11-22, parent integration run):** the production portable `PostgameReplay.exe --match-id 9097559249 --download` with manifest pin **`BFE9801AFB125616B5C9208DA9CBD47A3BE48F0A59172C74DA7279980629D783`** succeeded against the public services. It published **3,599 events / 2,037,899 bytes**, SHA-256 **`8bcd9ca84b345e8ff2fbaa54faa714645d9b9bda29d637aa9c2604fd42a69d9f`**, identical to the independently verified local DEM and cached-archive outputs. This is a full network transfer, not the earlier four-byte availability probe. The fixture preserves both versions' runtime pins and timeline digests. Availability was demonstrated for this match, not guaranteed for others or for future requests.

## Preserved runtime v1

**Subsequent full network check (same date):** the pinned portable `PostgameReplay.exe --match-id 9097559249 --download` was also run against real public services, without a metadata fixture or cached archive. It fetched the Valve archive (HTTP transport), decompressed and parsed it, and published 3,599 events. Its 1,680,889-byte output had SHA-256 `ac3b302073a67efd0abb7e247c5c88a7f847dbc56555bb45b2f7447937f1ef14`, identical to the independently processed local DEM. Availability is per match, not a guarantee of future downloads.

**Artifact:** `.tmp/postgame-replay-v1/PostgameReplay.exe` **plus its siblings**. A second offline build staged at `.tmp/postgame-replay-cli/` produced the same `sha256.json`: **`5BDE37D09CC25292EE78246DDB4FA56129F541A13BF1A7A0E41A5847C9E2DBF6`**. No WPF package was written by this build. Economy and skills are **not** exported by v1.

**Network vs. cached:** At **08:50:31 UTC**, for completed match `9097559249`, the real public OpenDota request returned **HTTP 200** and passed the production ID/result/URL gate. With no credentials or redirects, `replay442.valve.net` answered a bounded range GET with **HTTP 206**, `Content-Range: bytes 0-3/32777475`, Zstandard magic `28b52ffd`. Two requests; only **4 archive bytes** accepted. The archive was being served at that time, over unauthenticated HTTP. This was **not** a fresh full online transfer. The complete Download workflow was independently exercised using the cached public metadata and full 32,777,475-byte archive; local DEM, local compressed copy, and cached Download produced byte-identical JSON.

| Real DEM evidence | Observed |
|---|---|
| Identity / completion | Internal 64-bit ID `9097559249`, build 6986, Dire winner, POST_GAME tick 51,116; full parse through final FileInfo at tick 55,464, 27,732 packet frames. SHA-256 of DEM `6479cc80ddb51da5c4e7f8a98bf024509cd883cd2e83f2c083b23bb9629770eb`. |
| Game-clock coverage | −212.933 s through **24:47.433**, with one 103-tick pause excluded from elapsed game time. First resolved heroes at −88.267 s. |
| Heroes | Ten selected players, 5+5. 342 sample boundaries; **3,170** rows with position/level/items/alive/HP (**1,585 per team**), plus 250 unknown preselection rows. 79,250 known item slots; 2,960 alive and 210 dead rows. **590 rows after 20:00**. Not every tick/entity. |
| Wards | 23 observer + 42 sentry placements. **55 of 65 placements hidden to the opponent** by an observed team visibility bit. 54 recorded removals; 11 without a recorded removal by POST_GAME. Positions/owner/both visibility flags known on all 119 transitions. All placement/removal handles through game end agree with the cached OpenDota logs. |
| Fog example | Dire observer `669:356:4820`, slot 8, at `(−1768.53125, −160.15625)`, −52.333 s; removed at 307.667 s. Team word 10 bit 29: clear for Radiant, set for Dire. Not an inference from proximity. |
| Core events | 38 confirmed hero deaths match independent per-victim totals. 21 objectives: 11 towers, 2 barracks, 4 couriers, ancient, Roshan, Tormentor, Aegis pickup. |
| Importable result | **3,599 events, 1,680,889 bytes**; timeline SHA-256 `ac3b302073a67efd0abb7e247c5c88a7f847dbc56555bb45b2f7447937f1ef14`. Local QA copy: `.tmp/postgame-qa/9097559249.timeline.json`. |

Selected release-test output:

```text
test_all_map_real_postgame ... ok
test_cached_download_and_real_decompression_same_bytes ... ok
test_cancel_during_real_parse ... ok
test_corrupt_packet_after_post_game_rejected ... ok
test_local_magic_sniffed_archive_same_bytes ... ok
Ran 5 tests in 103.426s — OK
Imported 3599 real replay events; live-only read stayed empty.
```

Also passed: 8 Python contract/limits tests, 3 Node acquisition tests, strict TypeScript checking of the helper/tests, and verifying all 619 manifest members in both staged builds.

The portable runs used an empty PATH, unusable PYTHONHOME/PYTHONPATH and an invalid inherited NODE_OPTIONS; the isolated runtimes still worked. Cancellation and the corrupt postgame tail left no import candidate or job scratch. Rerun commands, limits and per-kind sampling are in [README.md](README.md); the small checked-in [fixture](tests/fixtures/9097559249.json) contains the quantitative assertions, not the replay or player metadata.
