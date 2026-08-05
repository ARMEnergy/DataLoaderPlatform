using DataLoader.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace DataLoader.Vulcan;

/// <summary>
/// Yields exactly one work unit for one Vulcan table: reads the stored watermark,
/// builds the incremental (or full-backfill) query, and stamps the run date.
/// </summary>
public sealed class VulcanWorkUnitProvider : IWorkUnitProvider<VulcanWorkUnit>
{
    private readonly VulcanTableSpec _spec;
    private readonly IVulcanWatermarkStore _watermarks;
    private readonly ILogger _logger;

    public VulcanWorkUnitProvider(VulcanTableSpec spec, IVulcanWatermarkStore watermarks, ILogger logger)
    {
        _spec = spec;
        _watermarks = watermarks;
        _logger = logger;
    }

    public async Task<IReadOnlyList<VulcanWorkUnit>> GetWorkUnitsAsync(LoaderRunContext context)
    {
        var watermark = await _watermarks.GetAsync(_spec.TableId, context.CancellationToken).ConfigureAwait(false);
        var query = VulcanQueryBuilder.Build(_spec, watermark);

        _logger.LogInformation(
            "[{Table}] {Mode} load (watermark={Watermark})",
            _spec.TableId, watermark is null ? "full backfill" : "incremental",
            watermark?.ToString("yyyy-MM-dd") ?? "none");

        return new[]
        {
            new VulcanWorkUnit
            {
                TableId = _spec.TableId,
                RunDate = DateOnly.FromDateTime(context.StartedAtUtc),
                Query = query,
                Spec = _spec,
                Watermark = watermark
            }
        };
    }
}
