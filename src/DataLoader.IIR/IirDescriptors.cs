namespace DataLoader.IIR;

/// <summary>
/// Static description of one IIR endpoint (design §2). The descriptor registry is the
/// (compile-time) discovery result — enumeration is DB-free. Everything a work-unit
/// provider, source reader and FileLog writer needs to drive one endpoint comes from
/// here; the only per-endpoint C# is the row type, its row factory and its sink.
/// </summary>
/// <param name="EndpointId">Stable id: "Plant" | "Unit" | "OfflineEvent".</param>
/// <param name="Product">Path segment: "plants" | "units" | "offlineevents".</param>
/// <param name="SummaryPath">Relative summary path with a literal <c>{ver}</c> token, e.g. <c>idb/{ver}/plants/summary</c>.</param>
/// <param name="DetailPath">Relative detail path with a literal <c>{ver}</c> token (used only by the optional §11 enrichment).</param>
/// <param name="DataArrayKey">Expected envelope array key ("plants"/"units"/"offlineEvents") — read TOLERANTLY (design §4.3).</param>
/// <param name="IdField">The id JSON field ("plantId"/"unitId"/"eventId") — PK source + enrichment id enumeration.</param>
/// <param name="IdParam">The detail query-param name ("plantId"/"unitId"/"eventId") — enrichment only.</param>
/// <param name="StampsRunDate">true only for OfflineEvent (RunDate is a PK part; design §5.4).</param>
/// <param name="StatusScoped">true only for OfflineEvent (append eventKind + eventStatusDesc filters; design §5.2).</param>
/// <remarks>
/// The target table / TVP type / merge-proc names are deliberately NOT held here — the per-endpoint
/// sink is the single source of truth for those (avoids a duplicated, never-read copy).
/// </remarks>
public sealed record IirEndpointDescriptor(
    string EndpointId,
    string Product,
    string SummaryPath,
    string DetailPath,
    string DataArrayKey,
    string IdField,
    string IdParam,
    bool StampsRunDate,
    bool StatusScoped);

/// <summary>The three concrete IIR endpoint descriptors (design §2.1) — the static discovery result.</summary>
public static class IirDescriptors
{
    public static readonly IirEndpointDescriptor Plant = new(
        "Plant", "plants",
        "idb/{ver}/plants/summary", "idb/{ver}/plants/detail",
        "plants", "plantId", "plantId",
        StampsRunDate: false, StatusScoped: false);

    public static readonly IirEndpointDescriptor Unit = new(
        "Unit", "units",
        "idb/{ver}/units/summary", "idb/{ver}/units/detail",
        "units", "unitId", "unitId",
        StampsRunDate: false, StatusScoped: false);

    public static readonly IirEndpointDescriptor OfflineEvent = new(
        "OfflineEvent", "offlineevents",
        "idb/{ver}/offlineevents/summary", "idb/{ver}/offlineevents/detail",
        "offlineEvents", "eventId", "eventId",
        StampsRunDate: true, StatusScoped: true);

    /// <summary>All three descriptors in registration order.</summary>
    public static readonly IirEndpointDescriptor[] All = { Plant, Unit, OfflineEvent };

    /// <summary>The three endpoint ids (default <see cref="IirSettings.EnabledEndpoints"/>).</summary>
    public static readonly string[] AllIds = { "Plant", "Unit", "OfflineEvent" };

    /// <summary>
    /// Registry invariants (design §2.2) — fail fast at first static access rather than mis-building a
    /// request later (CWG precedent). Exactly 3 descriptors, unique ids; StampsRunDate iff OfflineEvent;
    /// StatusScoped == StampsRunDate; non-blank product/id fields; summary path ends "/summary" and
    /// carries {ver}; detail path ends "/detail".
    /// </summary>
    static IirDescriptors()
    {
        if (All.Length != 3)
            throw new InvalidOperationException($"IIR must have exactly 3 descriptors; found {All.Length}.");

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in All)
        {
            if (!ids.Add(d.EndpointId))
                throw new InvalidOperationException($"IIR descriptor id '{d.EndpointId}' is duplicated.");

            var isOffline = string.Equals(d.EndpointId, "OfflineEvent", StringComparison.Ordinal);
            if (d.StampsRunDate != isOffline)
                throw new InvalidOperationException($"IIR descriptor '{d.EndpointId}': StampsRunDate must be true iff it is OfflineEvent.");
            if (d.StatusScoped != d.StampsRunDate)
                throw new InvalidOperationException($"IIR descriptor '{d.EndpointId}': StatusScoped must equal StampsRunDate.");

            if (string.IsNullOrWhiteSpace(d.Product) || string.IsNullOrWhiteSpace(d.IdField) || string.IsNullOrWhiteSpace(d.IdParam))
                throw new InvalidOperationException($"IIR descriptor '{d.EndpointId}': Product/IdField/IdParam must be non-blank.");
            if (!d.SummaryPath.Contains("{ver}") || !d.SummaryPath.EndsWith("/summary", StringComparison.Ordinal))
                throw new InvalidOperationException($"IIR descriptor '{d.EndpointId}': SummaryPath must contain {{ver}} and end with /summary.");
            if (!d.DetailPath.EndsWith("/detail", StringComparison.Ordinal))
                throw new InvalidOperationException($"IIR descriptor '{d.EndpointId}': DetailPath must end with /detail.");
        }
    }
}
