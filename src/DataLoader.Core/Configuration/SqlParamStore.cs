using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace DataLoader.Core.Configuration;

/// <summary>
/// SQL Server implementation of <see cref="ISeeDbParamStore"/>. Reads the
/// platform connection string from <c>Platform:LoadLogConnectionString</c>
/// (environment-variable overrides are already merged into
/// <see cref="IConfiguration"/>) and resolves a value via
/// <c>core.usp_GetParam</c>.
///
/// Synchronous ADO.NET is intentional: resolution happens once, at startup,
/// inside <see cref="SeeDbSettingsResolver{TSettings}"/>. The resolved value is
/// a secret and is never logged.
/// </summary>
public sealed class SqlParamStore : ISeeDbParamStore
{
    private const string LoadLogConnectionStringKey = "Platform:LoadLogConnectionString";

    private readonly IConfiguration _configuration;

    public SqlParamStore(IConfiguration configuration) => _configuration = configuration;

    public string? GetParam(string loaderName, string paramName)
    {
        var connectionString = _configuration[LoadLogConnectionStringKey];
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"'{LoadLogConnectionStringKey}' is not configured; it is required to resolve SEE_DB loader settings via core.usp_GetParam.");
        }

        using var connection = new SqlConnection(connectionString);
        connection.Open();

        using var command = new SqlCommand("core.usp_GetParam", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.Add(new SqlParameter("@LoaderName", SqlDbType.VarChar, 150) { Value = loaderName });
        command.Parameters.Add(new SqlParameter("@ParamName", SqlDbType.VarChar, 150) { Value = paramName });

        var result = command.ExecuteScalar();
        return result is null or DBNull ? null : Convert.ToString(result);
    }
}
