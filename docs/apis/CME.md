# CME settlement bulletins — source contract

Everything here was verified live against the account on **2026-09-07**, over 16
downloaded bulletins spanning all 8 feeds and 2 trade dates (**757,360 data
rows**). Nothing in this document is reconstructed or inferred from vendor
documentation — there is none for this drop.

## 1. Connection

| | |
|---|---|
| Protocol | **SSH SFTP** (not FTP/FTPS) |
| Host | `sftp.cmeprod.datahex.rozettatech.com` |
| Port | 22 |
| Auth | username + password |
| Credentials | `Loaders:CME:SftpUsername` / `SftpPassword`, both shipping as the `"SEE_DB"` sentinel and resolved from `core.Param`. Never logged. |
| Client | SSH.NET 2024.2.0, pinned to match `DataLoader.Platts` |

### 1.1 ⚠ The server rejects absolute paths

This is the single most surprising fact about the drop and the one most likely to
break a future change. The server answers directory listings only for paths
**relative to the login directory**:

```
ListDirectory(".")            -> OK, 7 entries
ListDirectory("BAS_STLAGS")   -> OK
ListDirectory("./BAS_STLAGS") -> OK
ListDirectory("/BAS_STLAGS")  -> SftpPathNotFoundException("no such file")
ListDirectory("/BAS_STLAGS/") -> SftpPathNotFoundException("no such file")
```

Both the leading slash and a trailing slash are rejected.

The trap: SSH.NET's own `SftpFile.FullName` reports `/BAS_STLAGS` — **exactly the
form the server refuses**. Feeding a listing's `FullName` back in is therefore
the natural implementation and the broken one, and it fails on the *second*
directory level rather than the first, so it would survive a shallow smoke test.
`CmeSftpFileSystem` never propagates `FullName`; it builds child paths by
appending leaf names to a relative parent, and normalises any leading slash away.

## 2. Directory layout

```
<PRODUCT>_<EXCHANGE> / EOD_<EXCHANGE> / yyyy / MM / dd / <EXCHANGE>_yyyyMMdd.txt
BAS_STLAGS           / EOD_STLAGS     / 2026 / 09 / 04 / STLAGS_20260904.txt
```

Five directory levels, files at the bottom. A full walk is ~120 listings and
takes ~17 s over one SSH session (one session for the whole walk; per-listing
connections would be minutes of handshakes).

The **feed-level** folder is split on its **first** underscore, per the
requester's rule: in `EOD_STLAGS`, `EOD` is the ProductCode and `STLAGS` is the
ExchangeCode. Only the feed level is split — the *product* level folder
`BAS_STLNYMEX_STLCPC` contains **two** feed folders, so splitting that name would
be wrong:

| Product folder | Feed folder | ProductCode | ExchangeCode |
|---|---|---|---|
| `BAS_STLAGS` | `EOD_STLAGS` | EOD | STLAGS |
| `BAS_STLALT` | `EOD_STLALT` | EOD | STLALT |
| `BAS_STLCOMEX` | `EOD_STLCOMEX` | EOD | STLCOMEX |
| `BAS_STLCUR` | `EOD_STLCUR` | EOD | STLCUR |
| `BAS_STLEQT` | `EOD_STLEQT` | EOD | STLEQT |
| `BAS_STLINT` | `EOD_STLINT` | EOD | STLINT |
| `BAS_STLNYMEX_STLCPC` | `EOD_STLNYMEX` | EOD | STLNYMEX |
| `BAS_STLNYMEX_STLCPC` | `EOD_STLCPC` | EOD | STLCPC |

**Window:** a rolling 10 business days per feed (2026-08-24 … 2026-09-04 when
measured; the 29th/30th weekend is simply absent). 80 files, 3.5–13 MB each.
Because a weekend has no folder at all, an absent date is an absence of work, not
a gap to record — which is why the loader enumerates what exists instead of
constructing candidate dates.

## 3. File format

Fixed-width text, **LF** line endings, exactly **one** 3-line header per file (no
per-page repeats — confirmed, not assumed).

```
        FINAL POST-CLEARING PRICES AS OF 09/04/2026 10:35 PM (CDT)
MTH/                       -------  DAILY  ------                                  PT                         -------  PRIOR  DAY  -------
STRIKE            OPEN         HIGH          LOW         LAST         SETT         CHGE  ACTUAL VOL           SETT         VOL         INT
0CJ TEST PLATINUM FUTURE
SEP26             ----         ----         ----         ----       1821.0         -7.4                     1828.4
0PO OCT26 TEST PLATINUM OPTION CALL
1540            294.00       294.00       294.00       294.00         ----         ----                       ----
TOTAL                                                                                    ACTUAL VOL                     VOLUME    OPEN INT
TOTAL                                                                                                                                  201
```

Line 1 comes in two dialects, both live: `FINAL PRE-CLEARING PRICES AS OF …` and
`FINAL POST-CLEARING PRICES AS OF …`. The header timestamp normally matches the
file-name date, but the loader takes the trade date from the **file name** (and
cross-checks it against the `yyyy/MM/dd` folders) because that is deterministic.

Line 3's volume column is labelled `ACTUAL VOL` in some feeds and `EST.VOL` in
others — both correct, so the loader pins the geometry, not the label text.

### 3.1 ⚠ Fields are POSITIONAL, not whitespace-delimited

A missing value is sometimes the placeholder `----` and sometimes simply
**blank**, *in the same file*:

```
SEP26             ----         ----         ----         ----       1821.0         -7.4                     1828.4
```

That row has ten measure columns but only eight whitespace-delimited tokens —
ACTUAL VOL, PRIOR VOL and PRIOR INT are blank. Splitting on whitespace slides
`1828.4` two columns left, loading a **prior-day settle as a volume**. Every
field must be taken by character position.

### 3.2 Column geometry

Established from a character-occupancy histogram over 727,042 live data rows: the
never-occupied columns are the field separators, and the widest observed value
fixes each field's left edge. Values are **right-aligned**, so a field grows
leftward.

| Field | Span | Numeric right edge | Indicator col |
|---|---|---|---|
| MTH/STRIKE label | 1–13 | — | — |
| OPEN | 14–22 | 22 | — |
| HIGH | 23–36 | 35 | **36** |
| LOW | 37–49 | 48 | **49** |
| LAST | 50–62 | 61 | **62** |
| SETT | 63–74 | 74 | — |
| PT CHGE | 75–87 | 87 | — |
| ACTUAL VOL / EST.VOL | 88–99 | 99 | — |
| PRIOR DAY SETT | 100–114 | 114 | — |
| PRIOR DAY VOL | 115–126 | 126 | — |
| PRIOR DAY INT | 127–138 | 138 | — |

⚠ The A/B indicator sits **one character past** the numeric right edge. A slice
that stops at the numeric edge parses every number correctly and drops every
indicator — leaving all three indicator columns NULL on every row while the load
still looks perfect. (This bug was made and caught during the build.)

An indicator appeared on exactly **HIGH (`B`), LOW (`A`), LAST (`A`/`B`)** across
all 757,360 rows — precisely the three indicator columns the supplied DDL
provides, so the DDL and the data agree.

### 3.3 Line taxonomy

Every line in all 16 files falls into one of these. There were **no**
unclassified lines once the two-line header was handled.

| Kind | Recognised by | Count |
|---|---|---|
| Report header | contains `PRICES AS OF` | 16 |
| `MTH/` banner | starts `MTH/` | 16 |
| Column header | starts `STRIKE`, contains `OPEN` | 16 |
| Product header | not a data row | 11,290 |
| Data row | label is one token; all measure fields blank / `----` / `CAB` / numeric | 757,360 |
| Section total | label is `TOTAL` | 6,394 |
| Header continuation | follows a `<br />`-only header | 2 |

### 3.4 Product headers

```
0CJ TEST PLATINUM FUTURE                                              -> future, no month
1D AUG26 RBOB Gasoline BALMO Futures                                  -> future, month on header
0PO OCT26 TEST PLATINUM OPTION CALL                                   -> option, C
7A OCT26 Crude Oil Financial Calendar Spread Option (One Month) PUT   -> option, P
KYP11 <br />                                                          -> description on the NEXT line
```

- Symbol is the first whitespace token (max observed length **5**).
- `CALL`/`PUT` is always the **trailing whole word** and always upper case (all
  651 occurrences). It is stripped from the description and becomes `C`/`P`.
  Matching must be word-wise: a substring match would misread a description
  containing e.g. `OUTPUT`.
- A leading `MMMYY` token is the contract month and is stripped from the
  description.
- **Options take their contract month from the header; futures from each row's
  label.** Every one of the 701,341 option rows had a header month.

### 3.5 ⚠ Two mutually exclusive futures shapes

| Shape | Header | Row label | Rows/day |
|---|---|---|---|
| Normal | no month | `MMMYY` | 47,192 |
| BALMO / event | **has** a month | day of month, `01`–`31` | 8,827 |

Across the whole sample **zero** sections had both a header month and `MMMYY`
rows, so a row's label alone decides. BALMO rows appear only in STLCPC and
STLEQT. The loader **does not load them** — see `docs/design/CME.md` §2.1.

### 3.6 Value forms

| Form | Meaning | Handling |
|---|---|---|
| blank, `----` | no value | NULL |
| `9.92B` | value + A/B indicator | 9.92 + `B` |
| `CAB` | **cabinet price** — nominal minimum for deep-OTM options | NULL, counted (80,917 occurrences) |
| `491'4` | tick notation | converted per feed (below) |
| `+.1600`, `-.0289`, `.03000` | signed, no integer part | parsed as-is |

`CAB` is the **only** non-numeric price token in the whole sample. It appears in
SETT and PRIOR SETT (and rarely OPEN/HIGH/LOW/LAST) across STLAGS, STLCUR,
STLEQT and STLINT. The DDL has no marker column, so it becomes NULL — never 0,
which would read as a real price of zero.

### 3.7 ⚠ Tick notation is per-FEED

The denominator is a property of the **feed**, not of the value. It was
determined empirically: the maximum fractional numerator observed for each
fraction width pins it beyond doubt.

| Feed | Widths | Max numerator | Denominator | Example |
|---|---|---|---|---|
| `STLAGS` | 1 | **7** | **8** (eighths) | `491'4` = 491.5 |
| `STLINT` | 1, 2, 3 | 0 / **63** / **635** | **64** (64ths) | `'635` = 63.5/64 |
| all others | — | — | none | — |

A width-3 fraction carries a **tenth** of a denominator-th in its last digit
(`'635` is 63.5 sixty-fourths, not 635 of anything). Reading `'4` as 4/64 in a
grain bulletin, or `'32` as 32/8 in a rates bulletin, silently corrupts the
price — which is why a width the feed's convention does not allow is stored NULL
and counted rather than converted under a guessed denominator.

Signs apply to the whole magnitude: `-7'2` = −7.25, `+'6` = +0.75.

### 3.8 Observed value ranges

All comfortably inside the supplied column types.

| | Observed | Column |
|---|---|---|
| ProductSymbol | ≤ 5 chars | `varchar(50)` |
| Option ProductDescription | ≤ 92 chars | `varchar(250)` |
| Future ProductDescription | ≤ 90 chars | `varchar(2000)` |
| ContractYear | 2014 … 2099 | `smallint` |
| Strike | −550000.00 … 700000.00 | `decimal(18,8)` |
| PutCall | `C`, `P` | `varchar(50)` |
| Indicators | `A`, `B` | `char(1)` |

Both ends of the year range are legitimate and were checked: the 2014 contracts
belong to `YIE 30-Year Eris SOFR Swap Futures`, whose listed effective dates
reach back years, and `DEC99` is a far-dated test contract under
`0BT TEST BITCOIN FUTURES`. A 1900s reading is impossible in a 2026 bulletin, so
two-digit years map to `2000 + yy` with no pivot heuristic.

### 3.9 Known data artifacts

- **`TOTAL` lines** (6,394) are section aggregates. 3,197 of them parse perfectly
  as data rows, so they would load as facts with a bogus label unless the label
  is checked first.
- **`KYP11 <br />`** — a literal HTML fragment inside a CME product name pushes
  the description onto the following line. Two occurrences (one per STLEQT
  file); the sibling `KYP13` is on one line. `<br />` is the only HTML artifact
  in the entire sample.
- Many products are prefixed `TEST` (`0CJ TEST PLATINUM FUTURE`). These are real
  rows in the real feed and are loaded as-is; filtering them was not requested.

## 4. Key uniqueness

Verified on live data against the supplied primary keys:

| Table | Rows/day | Duplicate keys |
|---|---|---|
| `arm.STLBASIC_Option` | 701,341 (16 files) | **0** |
| `arm.STLBASIC_Future` | 47,192 (16 files) | **0** |

Because `ExchangeCode`, `ProductCode` and `TradeDate` all sit in both keys, and
one bulletin carries exactly one (exchange, product, trade date), two different
files write **disjoint** key sets — so no cross-file ordering guard is needed.
