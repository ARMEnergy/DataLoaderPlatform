# OPIS — LP daily price report (FTP)

Field reference for the `DataLoader.OPIS` loader.
**Status: VERIFIED LIVE** — every statement below was observed against the real
drop on 2026-08-21 (24 files, 4 835 data rows), not reconstructed from vendor docs.

---

## 1. Source

| | |
|---|---|
| Protocol | **plain FTP** (not FTPS, not SFTP) |
| Host | `ftp.opisnet.com` |
| Port | `21` |
| Mode | passive |
| Auth | username + password |
| Directory | `/` (the drop root — no sub-folders) |
| Encoding | ASCII; read as Latin-1 so a stray high byte degrades instead of failing |
| Line endings | **CRLF** |

**Credentials** are referenced only by config setting name:
`Loaders:OPIS:Username` and `Loaders:OPIS:Password`. Both ship as the `"SEE_DB"`
sentinel and resolve from `core.Param(LoaderName='OPIS', …)` at run time. They are
never logged and never appear in `arm.FileLog.RequestPath`, which stores
`ftp://host:port/path` only.

### Capabilities observed
- `LIST` returns Unix-style long listings **with a timestamp and size** per file.
- `MDTM` is supported (`curl -I` returns `Last-Modified: Thu, 20 Aug 2026 21:26:02 GMT`),
  so the per-file modification stamp is reliable — this is what the work-unit
  resume key embeds.
- No paging, no rate limit encountered. Files are ~14 KB each.

---

## 2. File inventory

One file per publication day, named **`<yyyyMMdd>LP.csv`**:

```
20260720LP.csv  20260721LP.csv  …  20260819LP.csv  20260820LP.csv
```

- **Weekdays only** — no Saturday/Sunday files. A gap is normal, not an error.
- The drop holds a **rolling ~1 month** (24 files on 2026-08-21, covering 07-20 → 08-20).
- The date in the file name is the *publication* date; it is not always the only
  date *inside* the file (see §4).

---

## 3. Record layout

Comma-delimited, **one header row**, exactly **10 fields**, no quoting observed.
Values are space-padded to fixed widths — text pads right, numbers pad left.

```
Price,Mkt_Prod,Date,Low,High,Avg,Country,Unit,Timing,Freq
I,LOS ANGELES PRO     ,08/20/26, 85.8750, 89.8750, 87.8750,US,GAL,A,D
I,LOS ANGELES NBT     ,08/20/26,132.0000,132.2500,132.1250,US,GAL,A,D
I,MT BEL NT BSKT      ,07/20/26,        ,        , 73.0588,US,GAL,A,D
```

| # | Field | Meaning | Observed | Null? | SQL type |
|---|-------|---------|----------|-------|----------|
| 0 | `Price` | **Record status code, not a number.** `I` = initial quote, `U` = updated/revision of a previously published row. | `I` ×4830, `U` ×5 | never | `VARCHAR(10)` |
| 1 | `Mkt_Prod` | Market + product label. **Right-space-padded** — must be trimmed, it is a key column. | 76 distinct, max 20 chars trimmed | never | `VARCHAR(50)` |
| 2 | `Date` | Report date, **`MM/dd/yy` — two-digit year**. | `08/20/26` | never | `DATE` |
| 3 | `Low` | Low price. **Blank on basket rows.** | max 3 int digits, 4 dp | **24 blanks** | `DECIMAL(12,4)` |
| 4 | `High` | High price. **Blank on basket rows.** | max 3 int digits, 4 dp | **24 blanks** | `DECIMAL(12,4)` |
| 5 | `Avg` | Average price. | always present | none observed | `DECIMAL(12,4)` |
| 6 | `Country` | Country code. | `US` only | never | `VARCHAR(10)` |
| 7 | `Unit` | Price unit. | `GAL` only | never | `VARCHAR(10)` |
| 8 | `Timing` | Timing/assessment window code. **Key column.** | `A`, `O`, `P` | never | `VARCHAR(10)` |
| 9 | `Freq` | Publication frequency. | `D` only | never | `VARCHAR(10)` |

Column widths were sized with headroom: `Country`/`Unit`/`Timing`/`Freq`/`Price`
are 1–3 characters today but are modelled `VARCHAR(10)` so a new code does not
need a schema change. `Mkt_Prod` is 20 today, modelled `VARCHAR(50)`.

### Type notes
- `Low`/`High`/`Avg` are `DECIMAL(12,4)`, **not `FLOAT`** — these are prices and
  must round-trip exactly. Observed maximum magnitude is 3 integer digits with
  4 decimal places, so `DECIMAL(12,4)` leaves ample room.
- Blank `Low`/`High` are **legitimate NULLs**, not parse failures: basket rows
  (e.g. `MT BEL NT BSKT`) publish only an average. All 24 occurrences in the
  observed month are that one product, one per file.

---

## 4. Revisions — the load-bearing finding

**A file may contain rows dated earlier than the file itself**, republished under
`Price='U'` with **changed values**. Five of the 24 files do this:

| File | Dates it contains |
|------|-------------------|
| `20260730LP.csv` | `07/30/26` |
| `20260803LP.csv` | `07/30/26`, `08/03/26` |
| `20260804LP.csv` | `07/30/26`, `08/04/26` |
| … through `20260807LP.csv` | |

The revised row, in full:

| Source file | Price | Mkt_Prod | Date | Timing | Low | High | Avg |
|---|---|---|---|---|---|---|---|
| `20260730LP.csv` | `I` | SARNIA PRO | 07/30/26 | `O` | 81.2500 | **81.5000** | **81.3750** |
| `20260803LP.csv` … `20260807LP.csv` | `U` | SARNIA PRO | 07/30/26 | `O` | 81.2500 | **82.0000** | **81.6250** |

This is what makes `Price` a meaningful fourth key column on
`arm.LPReportHistory`: the `I` and `U` records are the same market/product/date/
timing but different prices, so keying history on
`(Mkt_Prod, Date, Timing, Price)` preserves both, while `arm.LPReport` keyed on
`(Mkt_Prod, Date, Timing)` keeps only the superseding `U`.

The same `U` row is republished unchanged in five consecutive files — so merging
is naturally idempotent.

### Uniqueness verified
- **No duplicate `(Mkt_Prod, Date, Timing)` within any single file** — the
  `arm.LPReport` PK is safe at file grain.
- **No duplicate `(Mkt_Prod, Date, Timing, Price)` within any single file.**
- Across the whole month, **no `(Price, Mkt_Prod, Date, Timing)` key ever carries
  two different `Low/High/Avg` value sets** — republication never contradicts
  itself, so a last-wins merge cannot lose data.

---

## 5. Parsing rules the loader applies

1. Skip the header row and blank lines.
2. Split on `,` (quote-aware, though the feed never quotes — an embedded comma
   would otherwise shift every column, and the TVP binds by position).
3. **Trim every field.** `Mkt_Prod` is a key column and arrives padded.
4. Parse `Date` with `InvariantCulture` against `MM/dd/yy`, `M/d/yy`,
   `MM/dd/yyyy`, `M/d/yyyy`, `yyyy-MM-dd` in that order. `TwoDigitYearMax` (2049)
   maps `26` → 2026.
5. Blank `Low`/`High`/`Avg` → NULL. A *non-numeric* value also becomes NULL but
   is counted and logged.
6. **Drop and count** a row that cannot be keyed: wrong field count, blank
   `Price`/`Mkt_Prod`/`Timing`, or an unparseable `Date`. One bad line must not
   cost the other ~200 rows in the file. Over the live month: **0 rows dropped**.

---

## 6. Open questions for a longer observation window

None blocking; all were resolved against live data. Worth re-checking after a
few months of production running:

- Does `Country` ever leave `US`, or `Unit` leave `GAL`, or `Freq` leave `D`?
  (All are `VARCHAR(10)`, so a new code loads fine — it just widens the domain.)
- Are there `Price` codes beyond `I` and `U`? A third code would simply create a
  third history row for that key, which the design already handles.
- Does `Avg` ever arrive blank? Modelled NULL-able already.
- Does the drop ever exceed one month, or add non-`LP` report files? The
  `FilePattern` (`*LP.csv`) and `DaysBack` settings cover both.
