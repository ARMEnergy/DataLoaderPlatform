namespace DataLoader.Core.Abstractions;

/// <summary>
/// Writes a batch of target rows to the loader's destination.
///
/// Most implementations are SQL sinks built on
/// <see cref="Sinks.SqlSinkBase{TRow}"/>, which uses Table-Valued Parameters
/// for fast bulk merges. But a sink can be anything — Parquet, Kafka,
/// another HTTP API.
///
/// <para>
/// The sink returns the number of records actually persisted, which is
/// recorded in the load log for that work unit.
/// </para>
/// </summary>
public interface ISink<TRow>
{
    Task<int> WriteAsync(IReadOnlyList<TRow> rows, CancellationToken cancellationToken);
}
