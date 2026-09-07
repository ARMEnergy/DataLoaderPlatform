# EOX Live — daily FTP drop

Source documentation for the `EOX` loader. Everything here was captured from the
**live** account on **2026-09-04**: the directory census is the whole root listing,
and the layout notes come from parsing 25 real files spanning 2011‑10‑26 to
2026‑09‑04 through the loader's own reader.

There is no vendor API document. EOX publishes files and nothing else — no
schema, no changelog, no trading calendar. Every statement below is an
observation, and the ones that could change are called out as assumptions with
the check that would catch them.

---

## 1. Source

| | |
|---|---|
| Protocol | FTP (Pure-FTPd), port 21 |
| Host | `ftp.eoxlive.com` |
| User | `arm@eoxlive.com` |
| Auth | password, stored in `core.Param(LoaderName='EOX')` |
| TLS | `AUTH TLS` advertised and **verified working**, list and download, with a validating client |
| Directory | root `/` only — no subdirectories |

Server banner: `Welcome to Pure-FTPd [privsep] [TLS]`, 5000-user cap, 30-minute
idle disconnect.

### Capabilities observed (`FEAT`)

```
EPRT  IDLE  MDTM  SIZE  MFMT  REST STREAM  MLST  MLSD  AUTH TLS
```

`MLSD` and `MDTM` both present, so FluentFTP gets a true UTC modify timestamp
rather than the minute-granular local time in a plain `LIST`. That matters: the
work-unit resume key embeds that stamp.

A full `LIST` of the root returns **1.6 MB / 18,486 entries in ~1.7 s**. One
listing per run, shared by all three feeds (`EoxListingCache`).

### Root inventory (2026-09-04)

| Pattern | Files | In scope |
|---|---:|---|
| `EOD_CSV_NG_<yyyyMMdd>_1430.csv` | 3,747 | **yes** → `arm.NaturalGas` |
| `EOD_CSV_NGL_<yyyyMMdd>_1430.csv` | 3,240 | **yes** → `arm.NGL` |
| `EOD_CSV_C_<yyyyMMdd>_1430.csv` | 3,096 | **yes** → `arm.CrudeOil` |
| `EOD_NG_<yyyyMMdd>_1430.xlsx` | 2,373 | no — Excel twin |
| `EOD_C_<yyyyMMdd>_1430.xlsx` | 2,364 | no — Excel twin |
| `EOD_NG_<yyyyMMdd>_1430.xls` | 1,491 | no — Excel twin |
| `EOD_NGL_<yyyyMMdd>_1430.xlsx` | 1,294 | no — Excel twin |
| `EOD_C_<yyyyMMdd>_1430.xls` | 733 | no — Excel twin |
| `EOD_CSV_20YR_NG_<yyyyMMdd>_1430.csv` | 63 | no — monthly 20-year curve, different series |
| `EOD_20YR_NG_<yyyyMMdd>_1430.xlsx` | 63 | no |
| `EOD_CSV_C_<yyyyMMdd>_1430 (<HOST>'s conflicted copy <date>).csv` | 19 | **no — see below** |
| `EOD_NG__1430.xls`, `eod_ng_<date>_1430.xls` | 2 | no — malformed one-offs |

⚠ **The "conflicted copy" files are a real trap.** They are Dropbox-style
collision artefacts left in the same directory, and they share both the feed
prefix *and* the date:

```
EOD_CSV_C_20210622_1430 (EOXHOUMD11's conflicted copy 2021-06-22).csv
```

A glob like `EOD_CSV_C_*.csv` would load them as if they were the real file. The
loader instead **constructs the exact expected name** from the curve date and
looks it up in the listing, so nothing but the canonical file can ever match.
That also excludes the Excel twins and the 20YR series without a second rule.

### Coverage

| Feed | First file | Last file | Files |
|---|---|---|---:|
| NaturalGas | 2011-10-26 | 2026-09-04 | 3,747 |
| NGL | 2013-10-21 | 2026-09-04 | 3,240 |
| CrudeOil | 2014-05-19 | 2026-09-04 | 3,096 |

Nothing is ever removed — the drop is the full history. Publication is on trading
days; weekends and holidays simply have no file, which the loader records as
`NotAvailable` rather than treating as an error.

The `_1430` token is 14:30 US Central, EOX's end-of-day snap. **Every one of the
10,083 in-scope files carries `1430` and no other value** — it is a constant, not
a pattern.

---

## 2. CSV dialect

Verified over all 25 sampled files:

* comma-delimited, **CRLF**, exactly one header row, trailing newline present;
* **no quoting anywhere** — not one `"` byte in any file;
* no embedded commas or newlines in any value;
* **no empty cells at all** — every column is populated on every row;
* every `Mid`/`Bid`/`Ask`/`FP` cell parses as a number; negatives occur and are
  legitimate (`Canada_WCS` trades at a discount to WTI);
* no leading or trailing whitespace on any value.

The loader's parser still implements full RFC 4180 quoting. That is not
defensiveness for its own sake: the TVP binds **by position**, so an embedded
comma handled naively would shift every following column one place left and
write plausible garbage instead of failing.

---

## 3. Layout, and the header drift that shapes the loader

### 3.1 Headers, per feed, as published today

```
CrudeOil    Line,Code,Curve_Date,Locat._Code,Contract_Term,Contract_Name,
            Contract_Begin,Contract_End,Time_Key,Location,Mid,Bid,Ask            (13)

NaturalGas  Line,Data_Code,Curve_Date,Region,Market,Market_Code,Contract_Name,
            Contract_Term,Contract_Begin,Contract_End,Time_Key,Mid,Bid,Ask,FP    (15)

NGL         Line,Data_Code,Curve_Date,Locat._Code,Contract_Term,Contract_Name,
            Contract_Begin,Contract_End,Time_Key,Location,Mid,Bid,Ask            (13)
```

Note `Locat._Code` — with the period. And note that CrudeOil heads its code
column `Code` while the other two head theirs `Data_Code`.

### 3.2 ⚠ Headers are NOT stable across the history

This is why the loader maps columns **by name, never by position**, and why each
column may name more than one accepted header:

| Drift | Seen in | Effect |
|---|---|---|
| `Number` instead of `Line` | CrudeOil and NGL, 2014 | column 1 |
| `Code` instead of `Data_Code` | NGL, 2014 | column 2 |
| **no `FP` column at all** | NaturalGas, up to ~2016 | 14 fields, not 15 |

The 2014 NGL file carries *both* renames at once. A loader that knew only the
modern spellings would reject every file older than the rename — silently losing
years of history on any backfill.

`FP` is the only column allowed to be *absent*: `EoxColumn.HeaderOptional` marks
it, and a file without it loads with `FP` NULL. Every other missing column fails
the file loudly, because a guess would corrupt rows rather than fail them.

### 3.3 ⚠ Date formats differ by feed, and NaturalGas uses a two-digit year

| Feed | `Curve_Date`, `Contract_Begin`, `Contract_End` |
|---|---|
| CrudeOil | `yyyy-MM-dd` |
| NGL | `yyyy-MM-dd` |
| **NaturalGas** | **`MM/dd/yy`** |

Stable across the whole 2011‑2026 history — no file mixes them.

The two-digit year is a **dated bug waiting to happen**. .NET's default
`TwoDigitYearMax` is 2049, so `ParseExact("06/30/50", "MM/dd/yy")` returns
**1950**. NaturalGas publishes contract *ends* in this format and the tenor grows
every year: the 2026 files already reach 2036. The loader widens `M/d/yy` to
`M/d/20yy` explicitly (`EoxCsv.ExpandTwoDigitYear`) before parsing, and pins the
behaviour with a test that also proves the framework really would have said 1950.
EOX's history starts in 2011 and contains no 19xx date anywhere, so mapping
`00..99` to 2000..2099 is unambiguously right here.

### 3.4 Value shapes

| Column | Observed | Table type |
|---|---|---|
| `Line` / `Number` | `1`…`34048`, max 5 chars | `VARCHAR(64)` |
| `Code` / `Data_Code` | `20260904_A_MB01`, max 16 chars | `VARCHAR(128)` |
| `Locat._Code` | 1–2 chars, 140 distinct (CrudeOil 2026) | `VARCHAR(8)` |
| `Market_Code` | 2 chars, 224 distinct (NaturalGas 2026) | `VARCHAR(8)` |
| `Time_Key` | `M01`, `M100`, `MB01`, `C01`, `S01`, `W01`; max 4 chars | `VARCHAR(16)` |
| `Contract_Term` | `Month`, `Calendar`, `Summer`, `Winter` | `VARCHAR(32)` |
| `Contract_Name` | `Sep_2026`, `Calendar_2026`; max 13 chars | `VARCHAR(64)` |
| `Location` | max 64 chars | `VARCHAR(128)` |
| `Region` | max 16 chars | `VARCHAR(64)` |
| `Market` | max 30 chars | `VARCHAR(128)` |
| `Mid`/`Bid`/`Ask`/`FP` | decimals, may be negative | `FLOAT` |

`Time_Key` widened over time: `MB1` in 2022 became `MB01` by 2026. Both fit.

### 3.5 Row counts per file (live, parsed through the loader)

| Feed | 2014 | 2020 | 2026 |
|---|---:|---:|---:|
| CrudeOil | 5,550 | 16,761 | 20,838 |
| NaturalGas | 9,450 | 28,424 | 34,048 |
| NGL | 810 | 3,648 | 4,560 |

About **59,400 rows and ~7 MB per trading day** across the three feeds.

---

## 4. Keys — and the two assumptions the design rests on

Primary keys, as specified by the requester:

```
arm.CrudeOil    (CurveDate, LocationCode, TimeKey)
arm.NaturalGas  (CurveDate, MarketCode,   TimeKey)
arm.NGL         (CurveDate, LocationCode, TimeKey)
```

**Assumption 1 — the key is unique within a file.** Verified: across all 25
sampled files, from 810 to 34,048 rows each, the number of distinct keys equals
the number of rows exactly. Zero collisions.

**Assumption 2 — `Curve_Date` always equals the date in the file name.** Verified
on every row of every sampled file. This is load-bearing: `CurveDate` leads every
primary key, so if it holds, two *different* files write **disjoint** key sets and
cannot race each other no matter how the work units interleave.

Neither assumption is taken on trust at run time:

* the reader counts and warns on any row whose `CurveDate` disagrees with its file
  name (the row is still loaded — the file's own value is authoritative);
* `arm.usp_ValidateLoad` reports the same thing from the loaded table
  (`CurveDateDoesNotMatchFileName`, expected 0);
* the merge procs guard every UPDATE with `src.FileName >= ISNULL(tgt.FileName,'')`
  so that if assumption 2 ever fails, the outcome degrades to a deterministic
  "newest file wins" instead of an arrival-order race.

Within one feed the names are `<fixed prefix><zero-padded yyyyMMdd><fixed suffix>`,
so lexicographic order **is** chronological order — which is what lets `FileName`
serve as the ordering guard without adding a column to the requester's DDL.

---

## 5. Change detection

EOX revises by **overwriting a file in place** under the same name. There is no
revision marker inside the data and no separate revision feed.

The FTP listing gives `MDTM` and `SIZE` for free — one request covers the whole
window — so the loader embeds the server-reported last-modified stamp and byte
size in the work-unit resume key. A republished file therefore gets a new key and
reloads at **any** age; an unchanged file is skipped however often the loader
runs. See `docs/design/EOX.md` §3.1 for how that composes with the hot/settled
window.

---

## 6. Verification log

| Date | What was checked | Result |
|---|---|---|
| 2026-09-04 | FTP login, plain | works |
| 2026-09-04 | FTP login + download, explicit TLS, validating client | works, byte-identical file |
| 2026-09-04 | full root `LIST` | 18,486 entries, 1.6 MB, ~1.7 s |
| 2026-09-04 | filename census over the whole root | table in §1 |
| 2026-09-04 | 25 files downloaded, 2011‑10‑26 → 2026‑09‑04 | all three feeds, 7 sample years |
| 2026-09-04 | header census over those 25 | drift table in §3.2 |
| 2026-09-04 | quoting / empty cells / numeric validity | none / none / all numeric |
| 2026-09-04 | PK uniqueness within each file | 0 collisions in all 25 |
| 2026-09-04 | `Curve_Date` vs file-name date | 0 mismatches in all 25 |
| 2026-09-04 | all 25 parsed through `EoxSourceReader.Parse` | 0 rows dropped, 0 degraded cells, 0 warnings |

**Not verified:** the SQL has never been deployed. No `EOX` database exists yet,
so nothing here proves the merge procs behave as intended against a real server —
only that they parse (ScriptDom, TSql160) and that their text matches the
contract the tests assert.
