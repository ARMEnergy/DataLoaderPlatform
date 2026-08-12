namespace DataLoader.CWG;

/// <summary>The five parse shapes (docs/apis/CWG.md, design §4). Each maps to one shared shape parser.</summary>
public enum CwgParseShape
{
    /// <summary>Simple tabular, one data row = one record.</summary>
    A,

    /// <summary>Wide-by-region tabular → unpivot to long.</summary>
    B,

    /// <summary>Pivoted hour×forecast-day matrix with leading title/date rows → unpivot.</summary>
    C,

    /// <summary>Stacked sub-region matrix blocks → unpivot per block.</summary>
    D,

    /// <summary>Region-row summary with title/header rows (single- or multi-block).</summary>
    E
}

/// <summary>Filename date token (design §2 / docs/apis "Date tokens").</summary>
public enum CwgDateToken
{
    /// <summary>No date token in the filename (undated "latest" file).</summary>
    None,

    /// <summary><c>{date}</c> = <c>YYYYMMDD</c>.</summary>
    Ymd,

    /// <summary><c>{datemmddyyyy}</c> = <c>MMDDYYYY</c> (zero-padded).</summary>
    Mdyyyy
}

/// <summary>How a descriptor's region axis is treated (design §2).</summary>
public enum CwgRegionKind
{
    /// <summary>No region axis (single file or in-file regions).</summary>
    None,

    /// <summary>Geography (northamerica/asia/europe) — filtered by the <c>Geographies</c> setting.</summary>
    Geography,

    /// <summary>ISO/market region — NOT filtered by <c>Geographies</c> (different axis).</summary>
    Iso
}

/// <summary>
/// How the undated "latest" work-unit resume key varies between runs (design §3.1 / §7).
/// </summary>
public enum HotKeyStrategy
{
    /// <summary>Suffix the key with the run date → one re-pull per calendar day (lighter).</summary>
    RunDate,

    /// <summary>Suffix the key with the run id → every invocation re-pulls (catches intraday overwrites).</summary>
    RunId
}
