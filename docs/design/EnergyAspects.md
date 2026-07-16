## Loader flow (for APPLICATION_DESIGNER / CODER)

- Target platform: .NET Core console application.
- Work unit class: `DesignMapping`, capturing all information required to
  process one mapping (mapping metadata + its dataset IDs + destination table).
- Discovery step: call the dataset-mappings endpoint first to refresh the list
  of dataset IDs (see docs/apis/energy-aspects.md).
- Partitioning: this loader runs daily → process in 1-day blocks.
- Per mapping: run the async process to populate its data; for each date block,
  merge the results into that mapping's destination table.
- Logging: log at the start of each block; on success, mark the log record
  complete and store RecordsProcessed. Before running a date block, check the
  load log and skip it if it is already recorded as successfully completed;
  otherwise process it.