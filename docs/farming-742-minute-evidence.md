# 7.42 minute-coach evidence: what did the carry actually last-hit?

[Optional prompt asset](../data/farming-742-minute-priors.json) · [verified replay pack](../data/farming-742-expanded.json) · [acquisition audit](../data/farming-742-expanded-audit.json) · [frozen tracker minutes](../data/farming-742-tempo.json)

**16 completed replays, 13 focal accounts, 240 classified game-clock minutes.** These are the original 8 Luna and 8 Troll Warlord replays, not 16 new games. The larger tracker cohort supports last-hit pace, not the lane/camp labels. No player or match identifiers, coordinates, event sequences, or runtime changes are in the prompt asset.

## What to pass to the coach

Select **one** row for the same patch and hero where `fromMinute <= m < toMinuteExclusive`; otherwise omit the prior. `m = 9` means `[09:00,10:00)`. At 10:00 a recap of the *preceding* minute uses `m = 9`, not 10. The four categories are counts of **minutes**, not creeps, camps cleared, votes by distinct players, or actions to take.

| Hero | Minutes | Replays / accounts | Matched minutes | Lane only | Own camp only | Both | Neither | Replays with ≥1 both minute |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| Luna | 5–10 | 8 / 6 | 40 | 8 | 5 | 26 | 1 | 8/8 |
| Luna | 10–15 | 8 / 6 | 40 | 6 | 7 | 27 | 0 | 8/8 |
| Luna | 15–20 | 8 / 6 | 40 | 6 | 11 | 21 | 2 | 7/8 |
| Troll Warlord | 5–10 | 8 / 7 | 40 | 21 | 1 | 16 | 2 | 6/8 |
| Troll Warlord | 10–15 | 8 / 7 | 40 | 7 | 7 | 25 | 1 | 8/8 |
| Troll Warlord | 15–20 | 8 / 7 | 40 | 9 | 5 | 25 | 1 | 8/8 |

Useful wording: “In eight Luna replays, 26 of 40 minutes between 5:00 and 10:00 included both a body lane last hit and a body last hit from an own-side camp.” For Troll in that band, lane-only was more common (21/40) than both (16/40); by 10–15 both occurred in 25/40. **Do not turn those counts into an instruction to leave lane.** They do not say a wave or camp is available, which came first, where the hero stood, or what this player can earn. The five minutes per game are correlated; the same replays recur across bands.

## What counts, what misses

- **Exposure:** one vote per focal match per completed 60-second interval `[60m,60(m+1))`. Start/terminal frames and endpoint checks establish coverage; don't pad short, missing or unverified replays. Don't select for being alive, farming, winning, or having an item. `replayMatches` / `replayAccounts` count distinct contributors to the row; `matchesWithBoth` counts games, once each. `laneOnly + campOnly + both + neither = matchedMinutes`; `matchedMinutes + ambiguousMinutesExcluded = completeMinutes`.
- **Lane:** at least one confirmed hero-**body** last hit on an **enemy lane creep**. Denies do not qualify. This includes `lane:?`: the combat log establishes the victim family, even when the specific spawn lane cannot be resolved (28 such body kills). A pulled creep still counts as a lane creep. No lane location is inferred.
- **Camp:** at least one confirmed body kill of a neutral assigned to a known **own-side spawn camp** on this replay map (`good_*` for Radiant, `evil_*` for Dire; 14 each). Assignment is from same-tick, same-unit death records resolving to one spawner, never nearest hero/camp or death coordinates. Own spawn side does **not** mean current territory or safety. Ignore illusions (42 filtered lane/neutral deaths) and opposing-spawn body neutral kills (148) over the six rows.
- **Ambiguity:** an unresolved neutral spawner or unknown actor is not a positive. Exclude the minute if resolving it could change either presence flag; otherwise keep it. There were **97** unlocated body neutral kills in **22** minutes (`minutesWithUnlocatedBodyNeutral`, not a kill count). All 22 already had a confirmed own-camp body kill. No unknown actors in these bands, so **0 ambiguous minutes excluded**, and 240/240 complete minutes classified. This does not mean all kills were located.
- **Neither** means no qualifying recorded kill, not idle or zero income. Of the seven such minutes, five had no packed lane/neutral kill event; two included opposing-camp body kills. Other unit types, heroes, summons, damage, assists and gold counters are not labels. A single hit in each family is enough for `both`; simultaneous hits require no ordering.
- **Paths require different evidence.** The [replay route audit](farming-742-expanded.md) separately matched kill times to nearby, living body-trace samples, breaking chains at deaths, gaps or ambiguous locations. For example, bottom-origin creep → `good_2` → bottom-origin creep occurred in 3/4 Radiant Luna replays during 5–10. That's an observed kill order in those replays, not a continuous walk, a destination, or a path reconstructed from these counts.

## Why 105 tracker matches do not give 105 replays

All counts here are for **November 22, 2026 UTC**. [Valve's 7.42 patch](https://www.dota2.com/patches/7.42) is dated November 18, 08:00 UTC in its public patch-list feed (rechecked against the frozen hash). Dota2ProTracker's [Luna](https://dota2protracker.com/hero/Luna?section=matches) and [Troll Warlord](https://dota2protracker.com/hero/Troll%20Warlord?section=matches) listings were filtered **per row** to 7.42, ranked lobby 7, position 1, positive account ID, **match-average** MMR ≥7000. These are tracked accounts, not random ladder games or necessarily professional matches.

| Source layer | Luna | Troll | What was verified |
|---|---:|---:|---|
| Previously frozen two-snapshot union | 65 | 36 | 101 unique matches; minute series clock checked against the 16 replays at 64 five-minute boundaries (63 exact, one off by one LH). |
| Bounded refresh, **new match IDs only** | +4 | +0 | Two hero listings, no pagination. Each new completed scoreboard matched the listing on hero/role/account, side, outcome, duration, MMR, final LH and the full minute series. Same publisher, **one new account** across all four; 2 games per side, 3 wins. |
| Union after refresh | **69** | **36** | 105 unique matches, 16 / 13 accounts. Four short Luna games have no 20:00 reading. |
| Verified Valve replays, **subset** of union | **8** | **8** | Same 16 match IDs and DEM hashes in fixed and expanded packs. All reached 20:00. 4 games per side and 6 wins per hero. |

The frozen replay audit recorded 85 failed acquisitions (65 OpenDota HTTP 520, 20 timeouts). Its saved completed-match page check found no direct DEM links for those 85. This pass probed public metadata for **three** missing matches (one newly listed): one 520, two timeouts; one additional public replay-list lookup timed out. All four new completed D2PT scoreboards had null playback fields and no direct DEM URL. **Zero new DEMs downloaded or admitted.** That's a limit of this bounded check, not proof that the replays never existed. We did not retry all 85, guess a replay URL, use credentials, or read live/observer feeds.

The earlier replay selection took the four highest match-average MMR **distinct accounts per hero and side** (accounts can repeat across sides): Luna 7843–8335, Troll 7814–8270. No verified replay below 7814. The 89 other tracker matches add **no** kill mix, path, visibility or safety evidence. The [shadow evaluation](farming-742-shadow.md) reuses these same 16 games; its predictions are not additional observations.

### What the extra matches *can* check (not part of the JSON)

Paired increase in cumulative last hits from 10:00 to 20:00, including controlled units. Each cell is **median [25th, 75th percentile]; completed matches / accounts**. Quantiles are linearly interpolated across per-match differences, not differences of column medians. Four short games are omitted, not assigned zero.

| Tracker cohort | Luna | Troll |
|---|---|---|
| All eligible, including refresh | 152 [134, 176]; 65 / 16 | 131 [114, 146.25]; 36 / 13 |
| Exclude all 16 replay match IDs | 149 [134, 176]; 57 / 12 | 131 [114, 140.5]; 28 / 9 |
| Exclude **every focal account** in those 16 replays | 147 [134, 170.5]; 42 / 10 | 132 [114, 144.5]; 23 / 6 |

In the full union, first taking each account's median and giving each account one vote gives **152.5 Luna / 126 Troll**. The four new Luna games alone had a median gain of 148 but are one account, not four independent confirmations. These are overlapping sensitivity checks, not test-set accuracy, farming targets or hero comparisons at matched game state. None can separate lane from neutral or body from illusion.

Refresh receipts: at ~07:59 UTC the `hero-matches` responses contained 108 / 45 rows, of which 66 / 36 were eligible. The refreshed Luna listing retained 62 frozen eligible IDs unchanged on the compared fields, added four, and omitted three old IDs; union by match, don't replace or double-count. Raw response SHA-256: Luna `e757a27fef0c0db590886edafe793fc82cb2c3a496764c3c076e5c4a08a415cb`, Troll `8fde280cd3e7e2f26412756c026a9e47a448a945d08e627def7982bf0b976614`. The four saved scoreboard byte hashes, sorted as lowercase hex and joined with LF without final newline, hash to `f6496614fe30a341240458ecc8064dd0dc93cdd3bfa23b9cefb02a27e86b3a13`. Raw new Luna evidence stays in the local `.tmp/minute-evidence-742/` cache, not in the prompt asset.

## Recount without the network

The JSON pins the expanded pack and audit by SHA-256. This independent check reads the **fixed** event pack: the same matches through a different published artifact, not new evidence. Run as Python from the repository root (no third-party packages or `.tmp` needed).

```python
import hashlib, json
from pathlib import Path

def raw(name): return Path(f"data/farming-742-{name}.json").read_bytes()
def sha(name): return hashlib.sha256(raw(name)).hexdigest()
p, f, audit = (json.loads(raw(k)) for k in ("minute-priors", "routes", "expanded-audit"))
assert p["patch"] == f["patch"]["version"] == "7.42"
assert p["source"]["expandedSha256"] == audit["studySha256"] == sha("expanded")
assert p["source"]["auditSha256"] == sha("expanded-audit") and audit["originalSha256"] == sha("routes")
assert {r["row"]["match_id"] for r in audit["rows"] if r["status"] == "verified"} == {m["id"] for m in f["matches"]}
assert [(b["hero"], b["fromMinute"], b["toMinuteExclusive"]) for b in p["bands"]] == [
    (h, a, a+5) for h in ("luna", "troll_warlord") for a in (5, 10, 15)]
camps = {c["id"] for c in f["map"]["camps"]}
for b in p["bands"]:
    tally = [0]*4  # neither, laneOnly, campOnly, both
    full = miss = unlocated = 0
    matches, accounts, mixed = set(), set(), set()
    for m in (m for m in f["matches"] if m["hero"] == b["hero"]):
        own = {c for c in camps if c.startswith("good_" if m["team"] == "radiant" else "evil_")}
        for t in range(b["fromMinute"], b["toMinuteExclusive"]):
            if 60*(t+1) > m["duration"]: continue
            full += 1
            ev = [(s, a) for sec, s, a, *_ in m["events"] if 60*t <= sec < 60*(t+1) and a != "illusion"]
            lane = any(s.startswith("lane:") and a == "body" for s, a in ev)
            camp = any(s in own and a == "body" for s, a in ev)
            unlocated += ("?", "body") in ev
            if ((not lane and any(s.startswith("lane:") and a == "?" for s, a in ev)) or
                (not camp and any(s == "?" or (s in own and a == "?") for s, a in ev))):
                miss += 1
                continue
            tally[int(lane) + 2*int(camp)] += 1
            matches.add(m["id"]); accounts.add(m["accountId"])
            if lane and camp: mixed.add(m["id"])
    assert tally == [b[k] for k in ("neither", "laneOnly", "campOnly", "both")]
    assert sum(tally) == b["matchedMinutes"] == full - miss
    assert [full, miss, unlocated] == [b[k] for k in ("completeMinutes", "ambiguousMinutesExcluded", "minutesWithUnlocatedBodyNeutral")]
    assert [len(matches), len(accounts), len(mixed)] == [b[k] for k in ("replayMatches", "replayAccounts", "matchesWithBoth")]
print("Six bands checked.")
```

With the original raw cache available, `node scripts/replays/pack-expanded-742.ts --check --no-cache` also checks replay/archive/metadata digests, build and postgame completeness, all ten terminal last-hit counters and the packed event stream. No game client or collection is required for either check.
