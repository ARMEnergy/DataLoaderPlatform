using System.Data;
using DataLoader.Core.Sinks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DataLoader.Core.Tests;

/// <summary>
/// <see cref="SqlSinkBase{TRow}.CommandTimeoutSeconds"/> — the fix for the
/// Criterion <c>FinancialSeriesData</c> incident, where every TVP merge in the
/// platform ran on ADO.NET's 30-second default because nothing ever set
/// <c>SqlCommand.CommandTimeout</c>.
///
/// <para>
/// <see cref="SqlSinkBase{TRow}.CreateCommand"/> is the seam: it builds the real
/// <see cref="SqlCommand"/> <c>WriteAsync</c> executes, against an UNOPENED
/// <see cref="SqlConnection"/>. Constructing a <c>SqlConnection</c> only parses the
/// connection string — it does not touch a network or a server — so these tests read
/// <c>CommandTimeout</c> off the actual object the production code path builds,
/// without needing SQL Server to exist. That is a real assertion on the fix, not a
/// restatement of the default in test code.
/// </para>
/// </summary>
public class SqlSinkBaseTests
{
    private sealed class TestSink : SqlSinkBase<string>
    {
        private readonly int? _overrideTimeoutSeconds;

        public TestSink(int? overrideTimeoutSeconds = null) : base(NullLogger.Instance)
        {
            _overrideTimeoutSeconds = overrideTimeoutSeconds;
        }

        protected override string GetConnectionString() =>
            "Server=(local);Database=DoesNotExist;Integrated Security=SSPI;";

        protected override string StoredProcedureName => "dbo.usp_Test";
        protected override string TableValuedParameterType => "dbo.TestTableType";
        protected override DataTable BuildTable(IReadOnlyList<string> rows) => new();

        protected override int CommandTimeoutSeconds =>
            _overrideTimeoutSeconds ?? base.CommandTimeoutSeconds;

        /// <summary>The exact command WriteAsync would execute, built with no connection opened.</summary>
        public SqlCommand BuildCommand() => CreateCommand(new SqlConnection(GetConnectionString()));
    }

    [Fact]
    public void Default_command_timeout_is_300_seconds_not_the_ado_net_default_of_30()
    {
        using var cmd = new TestSink().BuildCommand();
        Assert.Equal(300, cmd.CommandTimeout);
    }

    [Fact]
    public void A_subclass_override_is_honoured()
    {
        using var cmd = new TestSink(overrideTimeoutSeconds: 900).BuildCommand();
        Assert.Equal(900, cmd.CommandTimeout);
    }
}
