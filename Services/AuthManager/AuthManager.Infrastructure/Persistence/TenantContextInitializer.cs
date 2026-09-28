using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AuthManager.Infrastructure.Persistence;

internal static class TenantContextInitializer
{
    public static async Task InitializeAsync(
        AuthDbContext database,
        IDbContextTransaction transaction,
        Guid organisationId,
        CancellationToken cancellationToken)
    {
        if (!database.Database.IsNpgsql())
        {
            return;
        }

        await using var command = database.Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = "select pg_catalog.set_config('zeka.organisation_id', @organisation_id, true)";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "organisation_id";
        parameter.Value = organisationId.ToString("D");
        command.Parameters.Add(parameter);
        if (!Equals(await command.ExecuteScalarAsync(cancellationToken), parameter.Value))
        {
            throw new InvalidOperationException("Tenant context initialization failed.");
        }
    }
}
