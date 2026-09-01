# Argus Media — daily FTP drop

Field reference for the `DataLoader.Argus` loader.

**Status: VERIFIED LIVE** — every column name, width, key, date format and row
count below was measured against the real drop on **2026-08-27**. Nothing here is
reconstructed from vendor documentation. Where a statement is an *inference*
rather than an observation it is marked **(inferred)**.

---

## 1. Source

| | |
|---|---|
| Protocol | **plain FTP** (not FTPS, not SFTP) |
| Host | `ftp.argusmedia.com` |
| Port | `21` |
| Mode | passive |
| Auth | username + password |
| Encoding | ASCII; read as Latin-1 so a stray high byte degrades instead of failing the file |
| Line endings | **CRLF** |
| Quoting | **RFC 4180** — see §3 |

**Credentials** are referenced only by config setting name:
`Loaders:Argus:Username` / `Loaders:Argus:Password`. Both ship as the `"SEE_DB"`
sentinel and resolve from `core.Param(LoaderName='Argus', ...)` at run time. They
are never logged and never appear in `dlp.FileLog.RequestPath`, which stores
`ftp://host:port/path` only.

### Capabilities observed
- `LIST` returns Unix-style long listings with a per-file timestamp and size.
- No paging, no rate limit encountered. Largest in-scope file is 10.5 MB.
- The account is **read-only** — the loader never moves or deletes a file.

### Directory inventory (root)

```
DOCUMENTATION/   reference data — 18 files (16 CSV + 2 JPG)
DCRDEUS/         daily price time series — 25 files
FWDNGIV/         natural-gas implied volatility — 12 files   <-- OUT OF SCOPE
```

`FWDNGIV` is **not loaded**. It was not requested and has no target table. It is
recorded here only so a future reader knows it exists.

---

## 2. Scope

| Folder | Files loaded | Target |
|---|---|---|
| `DOCUMENTATION` | 15 of 16 CSVs | 15 `dlp.*` reference tables |
| `DCRDEUS` | every `<yyyyMMdd><suffix>.csv` | `dlp.TimeSeriesDetail` + `dlp.TimeSeriesDetailHistory` |

**Deliberately excluded**, with reasons:

| File | Why excluded |
|---|---|
| `DOCUMENTATION/latestDoc.csv` | 56 MB, ~218 000 rows, 27 columns. A fully denormalized join of Modules x Codes x Quotes x Category — **contains no fact the other tables do not already hold.** Excluded by explicit decision, not oversight. |
| `DOCUMENTATION/DocRelation.jpg`, `Relations.jpg` | Images (ER diagrams), not data. |
| `DCRDEUS/latest*.csv`, `DCRDEUS/previous*.csv` | Byte-identical **aliases** of the newest / second-newest dated files (verified: `latestdhc.csv` and `20260827dhc.csv` are both 33 302 bytes with the same timestamp). Loading them would double-merge the same rows. |
| `DCRDEUS/7667.csv` | Ad-hoc correction dump with no date in its name, so it carries no ordering guard. Its content **is** in scope shape-wise (same 10-column header) — see §5.4; it is the only observed source of `Record Status = 'C'`. |
| `FWDNGIV/**` | Not requested. |

The exclusions are enforced by one regex, `FileNameDatePattern` (§5.1) — not by a
deny-list — so a *new* dated module file is picked up automatically while all four
alias/ad-hoc shapes stay out.

---

## 3. CSV dialect

Verified across all 16 DOCUMENTATION CSVs and all sampled DCRDEUS files:

- Comma-delimited, **CRLF**, exactly **one header row**.
- **RFC 4180 quoting is in use.** 487 lines of `latestQuotes.csv`, 4 of
  `latestNewsCategory.csv` and a handful elsewhere wrap a field in `"..."`.
  The observed trigger is a value with **leading or trailing whitespace**
  (`"Nymex Gasoline RFG "`, `"Argus NPKs "`, `" thousand pounds"`).
- **No embedded commas or newlines were observed inside a quoted field** — every
  line in every file splits to exactly the header's field count on a naive comma
  split. The loader still uses a **quote-aware parser**: the TVP binds by
  position, so a future embedded comma would silently shift every column one place
  left and corrupt rows rather than fail them.
- Every value is **trimmed** after unquoting. This is load-bearing: the quoted
  values carry trailing spaces, and several of those columns are primary-key
  components.
- Blank cell -> `NULL`.

### Date format

**Every date column in every file is `dd-MMM-yyyy`** with English three-letter
month abbreviations (`26-Aug-2026`). Verified exhaustively over `latestQuotes`
(301 506 date cells), `latestModuleDetails`, `latestHoliday` and
`latestUnitCodeConv`: **zero** cells deviate. Parsed with `InvariantCulture` so a
machine's regional settings cannot reinterpret it.

`latestModules.LocalTime` is the one **time** column, `HH:mm:ss` -> `time(0)`.

---

## 4. DOCUMENTATION — file inventory and keys

All 15 loaded files are **full snapshots**, republished daily (`Aug 27 15:00` for
all 14 live ones; `latestRVP_Code_reference.csv` and the two JPGs were last touched
`Mar 13 13:52`). They are overwritten in place, so the loader merges by key rather
than appending.

| File | Rows | Header (verbatim) | Merge key — **verified unique** |
|---|---:|---|---|
| `latestCategory.csv` | 69 526 | `Code,DisplayName,Category` | (Code, Category) |
| `latestCodes.csv` | 69 049 | `Code,DisplayName,DeliveryMode,Unit,Frequency,Specification` | (Code) |
| `latestModuleDetails.csv` | 177 639 | `Module,Code,TimeStampID,PriceTypeID,ContinuousForwardPeriod,StartDateInModule,EndDateInModule` | (Module, Code, TimeStampID, PriceTypeID, ContinuousForwardPeriod) |
| `latestModules.csv` | 200 | `Module,Path,FileName,Description,Folder,Time,LocalTime,LocalTimeZone` | (Module) |
| `latestPricetype.csv` | 22 | `PriceTypeID,Description` | (PriceTypeID) |
| `latestQuotes.csv` | 150 752 | `Code,ContinuousForwardPeriod,Timing,ForwardPeriodDescription,TimestampID,PriceTypeID,DifferentialBasis,DifferentialBasisTiming,StartDate,EndDate,OldCode,DecimalPlaces` | see §4.1 — **full replace** |
| `latestTimestamp.csv` | 32 | `TimestampID,Description` | (TimestampID) |
| `latestTiming.csv` | 31 | `TimingId,Description,MinForwardPeriod,MaxForwardPeriod,ForwardPeriodDescription` | (TimingId) |
| `latestUnits.csv` | 172 | `UNIT_ID,DESCRIPTION,UNIT_DETAILS` | (UNIT_ID) |
| `latestUnitCodeConv.csv` | 54 303 | `UnitID,BaseUnitID,ValidFrom,ValidTo,CodeID,Ratio` | (UnitID, BaseUnitID, ValidFrom, CodeID) |
| `latestHolidayRegion.csv` | 63 | `HolidayRegionID,HolidayRegionDescription` | (HolidayRegionID) |
| `latestHoliday.csv` | 9 253 | `HolidayRegionID,HolidayDate` | (HolidayRegionID, HolidayDate) — **has duplicates**, see below |
| `latestQuoteHolidayRegion.csv` | 133 998 | `Code,ContinuousForwardPeriod,TimeStampID,PriceTypeID,HolidayRegionID1,HolidayRegionID2,HolidayRegionID3` | (Code, ContinuousForwardPeriod, TimeStampID, PriceTypeID) |
| `latestNewsCategory.csv` | 1 048 | `CATEGORY_TYPE,CATEGORY_ID,PARENT_ID,DESCRIPTION,ACTIVE` | (CATEGORY_TYPE, CATEGORY_ID) |
| `latestRVP_Code_reference.csv` | 198 | `CODE_ID,RVP_CODE_ID` | (CODE_ID, RVP_CODE_ID) |

Every key above was tested against the full live file: **0 duplicate groups**,
except:

> **`latestHoliday.csv` contains 11 exact whole-row duplicates** (e.g. `2,01-Jan-2024`
> appears twice). They are byte-identical, so de-duplication on the key is lossless.
> The loader de-duplicates in the merge; it is not an error.

### 4.1 `latestQuotes.csv` — why it is a full replace

The natural key **(Code, ContinuousForwardPeriod, TimestampID, PriceTypeID) is NOT
unique**: 2 313 duplicate groups. The rows are **validity-window versions** of a
quote definition:

```
PA0000005,0,month,month value,0,3,Brent dated,,01-Apr-1996,20-Sep-2002,NOCODE,2
PA0000005,0,month,month value,0,3,BFO dated,,23-Sep-2002,31-May-2007,NOCODE,2
PA0000005,0,month,month value,0,3,North Sea Dated,,01-Jun-2007,,NOCODE,2
```

Adding `StartDate` makes it unique (**0 duplicate groups over all 150 752 rows**,
and `StartDate` is never blank), so `(Code, ContinuousForwardPeriod, TimestampID,
PriceTypeID, StartDate)` **is** a valid key.

The table is nevertheless loaded by **full replace** (delete-and-reload in one
transaction), by explicit decision: `dlp.QuoteLookup` is specified with no PRIMARY
KEY and every column nullable, and a replace keeps the table an exact mirror of the
snapshot — including *removals*, which a merge would never apply. See design §4.2
for the empty-file and short-file safety guards this makes necessary.

### 4.2 Column notes

- `latestCodes.Specification` is a **6th column** present in the source. It is
  loaded into `dlp.CodeLookup.Specification` (max observed width 33).
- `latestNewsCategory.CATEGORY_ID` reaches **10 000 004 936** — this **overflows
  `INT`**. Both `CATEGORY_ID` and `PARENT_ID` must be `BIGINT`.
- `latestUnitCodeConv.Ratio` has up to **17 decimal places** and 5 integer digits
  (`0.00334112930170398`); modelled `DECIMAL(28,17)`. No scientific notation observed.
- `latestUnitCodeConv.CodeID` is an **integer** (1 ... 6 999 999), *not* a
  `PAxxxxxxx` string like `Code` elsewhere. Different domain, despite the similar
  name.
- `latestUnitCodeConv.ValidTo` is blank on 42 577 of 54 303 rows (open-ended) -> `NULL`.
- `latestQuoteHolidayRegion.HolidayRegionID2` is blank on 44 rows -> `NULL`.
  `...ID1` and `...ID3` are never blank.
- `latestUnits.UNIT_DETAILS` is blank on 1 row -> `NULL`.
- `latestNewsCategory.ACTIVE` is `Y` or `N`. `CATEGORY_TYPE` is one of
  `Content stream`, `News Category`, `News Region`, `News context`.
- Header spelling is **inconsistent across files** and does not match the target
  columns. The loader maps by header **name**, never by position:

  | Source header | Target column | Files |
  |---|---|---|
  | `TimeStampID` | `TimestampTypeID` | `latestModuleDetails`, `latestQuoteHolidayRegion` |
  | `TimestampID` | `TimestampTypeID` | `latestQuotes`, `latestTimestamp` |
  | `TS Type` | `TimestampTypeID` | DCRDEUS files |
  | `StartDateInModule` / `EndDateInModule` | `StartDate` / `EndDate` | `latestModuleDetails` |
  | `UNIT_ID` / `DESCRIPTION` / `UNIT_DETAILS` | `UnitID` / `Description` / `UnitDetails` | `latestUnits` |
  | `CATEGORY_TYPE` / `CATEGORY_ID` / `PARENT_ID` / `DESCRIPTION` / `ACTIVE` | `CategoryType` / `CategoryID` / `ParentID` / `Description` / `Active` | `latestNewsCategory` |
  | `CODE_ID` / `RVP_CODE_ID` | `CodeID` / `RvpCodeID` | `latestRVP_Code_reference` |

  Note also that `latestCategory.csv` publishes its columns in the order
  `Code, DisplayName, Category` while the target table is `(Code, Category,
  DisplayName)` — a **position-based** load would silently swap the two 500-char
  columns and corrupt the primary key.

---

## 5. DCRDEUS — daily time series

### 5.1 File naming

```
20260814dhc.csv   20260814dhca.csv   ...   20260827dhc.csv   20260827dhca.csv
latestdhc.csv     latestdhca.csv     previousdhc.csv        previousdhca.csv     7667.csv
```

Pattern: **`<yyyyMMdd><suffix>.csv`**, matched by
`^(\d{8})([A-Za-z][A-Za-z0-9]*)\.csv$`. Group 1 is the publication date (the merge
ordering guard); group 2 is the module suffix.

The drop holds a **rolling ~2 weeks** (10 publication days on 2026-08-27, covering
08-14 -> 08-27, weekdays only — 08-15/16 and 08-22/23 are weekends).

### 5.2 `Module` — always the uppercased suffix

**`Module` is stored as `UPPER(<file-name suffix>)`, always** — `dhc` -> `DHC`,
`dhca` -> `DHCA`. This is a decision, not an inference; see design §3.3.

It is exact for both suffixes DCRDEUS publishes today, which `latestModules.csv`
confirms:

```
Module,Path,FileName,Description,Folder,Time,LocalTime,LocalTimeZone
DHC,DATA\DCRDEUS,dhc,Argus US crude,\DCRDEUS,19:30:00 EST,18:30:00,Central Standard Time
DHCA,DATA\DCRDEUS,dhca,Argus US crude - 17:00 section (Houston time),\DCRDEUS,18:00:00 EST,17:00:00,Central Standard Time
```

> **Recorded limitation.** The strict authority for a module name is
> `latestModules.FileName`, and across the 200 modules it differs from
> `LOWER(Module)` in **124 cases** — `Module=DAMCOAL` has `FileName=dcm`,
> `DADR`/`dusem`, `DAPI10`/`dcm2`. So uppercasing is correct for `dhc`/`dhca` but is
> not a general rule: if Argus ever drops a THIRD file type into `DCRDEUS`, the
> derived `Module` may not exist in `dlp.ModuleLookup`.
>
> The safety net is `dlp.usp_ValidateLoad`'s **`FactModulesNotInModuleLookup`**
> check, which reports any `Module` in the fact table with no matching lookup row.
> It expects 0; a non-zero result means a new suffix has appeared and its module
> name needs confirming against `latestModules.FileName`.

### 5.3 Layout

Header, all files, verbatim:

```
Code,TS Type,PT Code,Date,Value,Fwd Period,Diff Base Roll,Year,Cont Fwd,Record Status
PA0045347,2,6,26-Aug-2026,6.02,0,10,2026,0,N
```

| # | Header | Target column | Type | Observed |
|--:|---|---|---|---|
| 1 | `Code` | `Code` | varchar(50) | max len 9, always `PAxxxxxxx` |
| 2 | `TS Type` | `TimestampTypeID` | smallint | 0, 2, 6, 9 |
| 3 | `PT Code` | `PriceTypeID` | smallint | 1,2,3,4,5,6,7,8,10,20,29,49 |
| 4 | `Date` | `Date` | date | `dd-MMM-yyyy` |
| 5 | `Value` | `Value` | decimal(18,6) | -24.5 ... 575 000; max 6 int digits, max scale 5 |
| 6 | `Fwd Period` | `FwdPeriod` | smallint | 0 ... 2031 |
| 7 | `Diff Base Roll` | `DiffBaseRoll` | smallint | 0 ... 12 |
| 8 | `Year` | `Year` | smallint | 2026 ... 2031 |
| 9 | `Cont Fwd` | `ContFwd` | smallint | 0 ... 6 |
| 10 | `Record Status` | `RecordStatus` | char(1) | `N`, `C` |

**No cell was blank in any sampled file** (10 columns x 1 512 rows). The nullable
columns are modelled defensively, not because a NULL was seen.

**Three columns have no source and are derived** (design §3.3):

| Column | Derived from |
|---|---|
| `Module` | the file-name suffix (§5.2) |
| `RecordStatusDate` | the file-name date `yyyyMMdd` — **(inferred)**, there is no such column in the feed. This is the date Argus *published* that status. |
| `SourcePath` | the remote path, e.g. `/DCRDEUS/20260827dhc.csv` |

### 5.4 `Record Status`

| Value | Meaning **(inferred)** | Seen in |
|---|---|---|
| `N` | new / original publication | every dated file (1 510 of 1 510 rows) |
| `C` | corrected | `7667.csv` only (40 of 470 rows) |

`RecordStatus` is what makes `dlp.TimeSeriesDetailHistory` a history: it is in that
table's PK but not in `dlp.TimeSeriesDetail`'s, so an `N` row and a later `C`
correction of the same quote both survive in history while the current table keeps
one row.

> **Caveat:** no `C` row has been observed in a *dated* file, so the correction path
> is exercised only by `7667.csv`, which the loader excludes. The behaviour is
> implemented and unit-tested but **has not been observed end to end against live
> dated data.**

### 5.5 Each file carries TWO dates, and files overlap

This is the single most important behaviour for merge correctness.

```
20260826dhc.csv   ->   19 rows dated 25-Aug  +  635 rows dated 26-Aug
20260827dhc.csv   ->   19 rows dated 26-Aug  +  679 rows dated 27-Aug
20260826dhca.csv  ->  402 rows dated 25-Aug  +  414 rows dated 26-Aug
20260827dhca.csv  ->  402 rows dated 26-Aug  +  410 rows dated 27-Aug
```

Because every run re-reads every dated file, **the same primary key arrives from
more than one file in a single run**, and the two modules behave differently:

- **`dhca` restates.** Of the 414 `26-Aug` keys in the `0826` file, **386 reappear**
  in the `0827` file. Compared cell-by-cell, **0 of the 386 differ** — today the
  restatement is a byte-identical repeat.
- **`dhc` appends.** The 19 `26-Aug` rows in the `0827` file have **zero key
  overlap** with the 635 `26-Aug` rows in the `0826` file. They are *late arrivals*
  for that date, not restatements.

Within a single file, the 5-part key `(Code, TS Type, PT Code, Cont Fwd, Date)` is
**unique — 0 duplicates** in every file checked.

**Consequence:** work units run concurrently, so the same key can be merged from two
files in either order. Correctness cannot rest on ordering; it rests on the merge
proc's guard `src.SourceFileDate >= tgt.RecordStatusDate`, which makes the outcome
independent of arrival order. See design §4.3.

### 5.6 Referential integrity with DOCUMENTATION

Checked live, all clean:

- All **267** distinct `Code` values in `20260827dhc.csv` exist in `latestCodes.csv`.
- Both `DHC` and `DHCA` exist in `latestModules.csv` and in `latestModuleDetails.csv`
  (1 475 and 567 rows).
- Every `TS Type` used is in `latestTimestamp.csv`; every `PT Code` used is in
  `latestPricetype.csv`.

No enforcing foreign keys are created. The two feeds load independently and either
may run first; an FK would make the fact load fail on a stale lookup snapshot. The
relationships are asserted by `dlp.usp_ValidateLoad` instead (design §5).

---

## 6. Change-detection

Per-file `LIST` timestamp + size drive the work-unit resume key
(`argus:<folder>:<name>:<lastModifiedUtc>:<size>`), so:

- A DOCUMENTATION `latest*.csv` is overwritten daily -> new stamp -> reprocessed daily.
- A DCRDEUS dated file that has not changed since the last run is **skipped** by
  `core.LoadLog` — the merge would be a no-op, so the database outcome is identical
  either way. `ForceReprocess: true` appends the run date to the key and disables
  the skip.

---

## 7. Verification log

| What | When | How |
|---|---|---|
| Directory inventory, all 3 folders | 2026-08-27 | `curl` FTP `LIST` |
| All 16 DOCUMENTATION CSVs downloaded (small in full; the 6 large keyed ones in full) | 2026-08-27 | `curl` |
| Header, field-count, quoting, date-format uniformity | 2026-08-27 | full-file scans |
| Every merge key tested for uniqueness | 2026-08-27 | full-file `sort` / `uniq -d` |
| Max field widths for every column of every loaded file | 2026-08-27 | full-file scan |
| DCRDEUS overlap / restatement analysis | 2026-08-27 | 5 files, key-level `comm` |
| Lookup-id and code referential integrity | 2026-08-27 | `comm` against lookups |

**Not verified:** SQL deployment, merge behaviour against a real database, and the
`Record Status = 'C'` correction path in a dated file. This loader is **build-only**.
