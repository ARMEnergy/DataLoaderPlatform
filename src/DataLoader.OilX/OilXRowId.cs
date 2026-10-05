using System.Security.Cryptography;
using System.Text;

namespace DataLoader.OilX;

/// <summary>
/// Derives <c>RowId</c> — the second half of every target table's primary key — as a
/// deterministic, RFC 4122 version-5 (SHA-1, name-based) UUID over the feed's business
/// key.
///
/// <para>
/// <b>Why it must be derived rather than generated.</b> The loader re-merges the same
/// series every run, and up to four times within a single day
/// (<c>docs/apis/OilX.md</c> §2 Behaviour 1). A random GUID would give the same series
/// a new identity every time, so <c>MERGE … ON (RunDate, RowId)</c> would never match,
/// the requested "merge the data by PKs" would silently become append-only, and the
/// tables would grow by ~2 million rows per run while every run reported success.
/// </para>
///
/// <para>
/// <b>⚠ Never use a seed-dependent hash for a persisted value.</b> .NET Core randomises
/// string hashing PER PROCESS, so <see cref="object.GetHashCode"/>,
/// <see cref="HashCode.Combine"/> and anything built on them produce a different value
/// on every run — every row would look new, forever. SHA-1 here is a fixed, documented,
/// seedless algorithm with no runtime dependency.
/// </para>
///
/// <para>
/// <b>Why SHA-1 is fine.</b> This is an identity derivation, not a security control.
/// The threat model has no adversary: the input is a vendor's own key columns, and a
/// collision would merge two series into one row. At 122 usable bits the probability of
/// any collision across the loader's whole corpus (~64M rows) is on the order of 1e-21.
/// SHA-1's known weakness is chosen-prefix COLLISION CRAFTING, which requires an
/// attacker choosing both inputs; it does not weaken the uniform distribution this
/// relies on. SHA-1 is specified by RFC 4122 for v5 UUIDs, so using it keeps the values
/// reproducible by any other tool that implements the standard.
/// </para>
///
/// <para>
/// <b>The derivation is a contract.</b> Changing the namespace, the key column set, the
/// canonical rendering (<see cref="OilXCanonical"/>) or the byte order below re-keys
/// every row: the merge stops matching and each table forks into old-derivation and
/// new-derivation copies that nothing reconciles. <c>OilXRowIdTests</c> pins exact
/// expected GUIDs for this reason — a change there is meant to be loud.
/// </para>
/// </summary>
internal static class OilXRowId
{
    /// <summary>
    /// The namespace UUID all OilX row ids are derived under. Arbitrary but FIXED, and
    /// specific to this loader so a key that happened to match another system's can
    /// never produce the same GUID.
    ///
    /// <para>⚠ Changing this value re-keys every row in every table. See the class remarks.</para>
    /// </summary>
    /// <remarks>
    /// The digits spell the loader: <c>6f696c78</c> is ASCII "oilx" and
    /// <c>456e65726779</c> is ASCII "Energy".
    /// </remarks>
    public static readonly Guid Namespace = new("6f696c78-0000-5000-8000-456e65726779");

    /// <summary>
    /// The canonical string a row's key hashes to — the key columns in
    /// <see cref="OilXFeedDescriptor.KeyColumns"/> order, rendered by
    /// <see cref="OilXCanonical"/>, prefixed by the feed id.
    ///
    /// <para>
    /// The <b>feed id prefix</b> is deliberate: without it, two feeds whose keys happen
    /// to render alike would produce the same GUID. Flow and GlobalBalance both key on
    /// mostly-text columns, and an empty-heavy key (Flow's subcountries are usually
    /// blank) makes an accidental match far less far-fetched than it sounds.
    /// </para>
    /// <para>
    /// Exposed <c>internal</c> so a test can assert the exact canonical FORM rather than
    /// just the resulting GUID — the GUID alone would let a key-order change slip
    /// through as "some other valid hash".
    /// </para>
    /// </summary>
    internal static string Canonical(OilXFeedDescriptor feed, IReadOnlyDictionary<string, object?> values)
    {
        var sb = new StringBuilder(160);
        sb.Append(feed.FeedId).Append(OilXCanonical.Separator);

        foreach (var name in feed.KeyColumns)
        {
            values.TryGetValue(name, out var value);
            OilXCanonical.Append(sb, value);
        }

        return sb.ToString();
    }

    /// <summary>Compute the row id for one row's already-converted key values.</summary>
    public static Guid Compute(OilXFeedDescriptor feed, IReadOnlyDictionary<string, object?> values) =>
        FromName(Namespace, Canonical(feed, values));

    /// <summary>
    /// RFC 4122 §4.3 name-based UUID, version 5 (SHA-1).
    ///
    /// <para>
    /// The namespace is hashed in BIG-ENDIAN ("network") byte order, which is not how
    /// .NET lays a <see cref="Guid"/> out in memory: the first three fields are
    /// little-endian on this platform. <see cref="SwapByteOrder"/> converts both ways,
    /// so the values produced here match any other RFC-4122 implementation rather than
    /// being a .NET-only dialect.
    /// </para>
    /// </summary>
    internal static Guid FromName(Guid namespaceId, string name)
    {
        var namespaceBytes = namespaceId.ToByteArray();
        SwapByteOrder(namespaceBytes);

        var nameBytes = Encoding.UTF8.GetBytes(name);

        var input = new byte[namespaceBytes.Length + nameBytes.Length];
        Buffer.BlockCopy(namespaceBytes, 0, input, 0, namespaceBytes.Length);
        Buffer.BlockCopy(nameBytes, 0, input, namespaceBytes.Length, nameBytes.Length);

        var hash = SHA1.HashData(input);

        var result = new byte[16];
        Array.Copy(hash, 0, result, 0, 16);

        // Version 5 in the high nibble of byte 6 ...
        result[6] = (byte)((result[6] & 0x0F) | 0x50);
        // ... and the RFC 4122 variant in the top two bits of byte 8.
        result[8] = (byte)((result[8] & 0x3F) | 0x80);

        SwapByteOrder(result);
        return new Guid(result);
    }

    /// <summary>
    /// Convert between .NET's mixed-endian <see cref="Guid"/> layout and RFC 4122's
    /// big-endian one, in place. Self-inverse, so the same call serves both directions.
    /// </summary>
    private static void SwapByteOrder(byte[] guid)
    {
        (guid[0], guid[3]) = (guid[3], guid[0]);
        (guid[1], guid[2]) = (guid[2], guid[1]);
        (guid[4], guid[5]) = (guid[5], guid[4]);
        (guid[6], guid[7]) = (guid[7], guid[6]);
    }
}
