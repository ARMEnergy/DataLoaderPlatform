# Energy Aspects — database structure

Database spec for the Energy Aspects loader. Read by DATABASE_DEVELOPER.
Field-level types come from the api-documentation-expert field reference
(see docs/apis/energy-aspects.md for the API side).

## Mappings tables (parent / child)
- PARENT table: one row per mapping, holding mapping-level metadata
  (mapping name, plus any other mapping-level fields from the mappings endpoint).
- CHILD table: one row per dataset ID belonging to a mapping, linked to the
  parent by FOREIGN KEY.

## Dataset tables
- One table per required dataset (see the dataset list in
  docs/apis/energy-aspects.md), modelled on the api-documentation-expert field
  reference, relating back to the mappings tables where appropriate.

## Load logging
- Load-log table capturing one row per table-load run, with at least:
  - MappingId  (FOREIGN KEY to the mappings PARENT table)
  - DateFrom   (load parameter)
  - DateTo     (load parameter)
  - IsComplete BIT
  - RecordsProcessed INT
  - Status NVARCHAR(20) and ErrorMessage NVARCHAR(1000) NULL, so an
    in-progress or failed run is distinguishable from a completed one
  (Plus the standard Id / DateCreated columns from the standing conventions.)
- Stored procedures:
  - usp_LoadLog_Insert — creates a new load-log record, returns the new Id.
  - usp_LoadLog_Update — updates an existing record by Id (completion,
    record count, status/error).