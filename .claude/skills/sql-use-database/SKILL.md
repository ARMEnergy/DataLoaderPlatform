# sql-use-database

Purpose: Guarantee every SQL script states the database it targets, so it cannot
silently create or drop objects in whatever database the connection happens to
be pointing at.

## Trigger
Use before writing or editing ANY `.sql` file — loader scripts, `sql/Core/`,
hotfixes, migrations, comparison scripts, drop scripts, one-off investigation
scripts. No exceptions.

## Rule

Every `.sql` file MUST contain, before its first statement that touches a
database object:

```sql
USE <DatabaseName>;
GO
```

The database name is written **literally** — there is no templating or token
substitution anywhere in this repo. `USE {{DatabaseName}}` is a placeholder in
this document only; a literal `{{...}}` would fail to parse, and these scripts
are run by hand in SSMS.

## Finding the right database name

Read it from `src/DataLoader.Host/appsettings.json` — never assume it matches the
loader name:

- Loader scripts: `Loaders:<Loader>:ConnectionString` → the `Database=` value.
- `sql/Core/`: `Platform:LoadLogConnectionString` → `DataLoaderPlatform`.

Known traps:
- **CME → `CMEGroup`** (loader name and database name differ).
- Core is **`DataLoaderPlatform`**, not `Core`.

## Placement

Order within the file:

1. Header comment block (what the script does, which server/database).
2. `IF NOT EXISTS (SELECT 1 FROM sys.databases ...) CREATE DATABASE <Name>;` +
   `GO` — only in a `001_*` schema script that is allowed to create it.
3. `USE <DatabaseName>;` + `GO`
4. Everything else.

`USE` must be followed by `GO`: it only takes effect for subsequent batches, so
DDL in the same batch still runs against the previous database. Follow the AGSI
posture in `sql/AGSI/001_CreateAgsiSchema.sql`.

## Why this matters here

These scripts are executed manually in SSMS against `ARMH-OPSDB01`, a server that
hosts every loader's database side by side, several of which contain
identically-named objects. `sql/CWG/999_DropCwgObjects.sql` drops `arm.`-prefixed
tables whose `dbo.` twins hold ~95M live rows. A missing `USE` means the script
runs wherever the editor's dropdown was last left — which is how a drop script
aimed at one database lands in another.

## Checks

1. The file contains `USE <Database>;` followed by `GO`.
2. The name matches the `Database=` value in the relevant connection string.
3. No statement touching a database object precedes it (a `CREATE DATABASE`
   guard may).
4. The file still parses — SQL is not compiled by build or test, so parse-check
   with ScriptDom (`TSql160Parser`); there is no `sqlcmd`/`sqlpackage` on this box.

Audit the whole tree with:

```bash
for f in $(find sql -name "*.sql" | sort); do
  grep -qiE "^\s*USE\s+" "$f" || echo "MISSING $f"
done
```

## Output template
- Files written/edited:
- Database targeted (and where the name came from):
- USE present + followed by GO: yes/no
- ScriptDom parse: pass/fail

## Escalation rules
- If the target database cannot be resolved from `appsettings.json`, stop and ask
  — do not guess from the folder name.
- If a script must span databases, say so explicitly and state the `USE` before
  each section rather than relying on three-part names.
