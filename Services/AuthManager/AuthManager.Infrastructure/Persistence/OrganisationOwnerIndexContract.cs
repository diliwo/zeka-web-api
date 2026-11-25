using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Zeka.OwnerSqlEvidence;

namespace AuthManager.Infrastructure.Persistence;

/// <summary>Exact catalog contract for the additive active-owner invariant.</summary>
public static class OrganisationOwnerIndexContract
{
    public const string Name = "UX_OrganisationMemberships_ActiveOwner";
    public const string MigrationId = "20261124170000_OrganisationSingleActiveOwner";
    public const string Filter = "\"PermissionSetId\" = 'D8ECEEEB-B796-4D77-92A5-6A11FAB10A01' AND \"Status\" = 1";
    public const string CatalogPredicate = "((\"PermissionSetId\" = 'd8eceeeb-b796-4d77-92a5-6a11fab10a01'::uuid) AND (\"Status\" = 1))";

    public static async Task<bool> IsInstalledAsync(DbContext database, CancellationToken cancellationToken = default)
    {
        var connection = database.Database.GetDbConnection();
        var close = connection.State != ConnectionState.Open;
        if (close) await connection.OpenAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = database.Database.CurrentTransaction?.GetDbTransaction();
            command.CommandText = """
                SELECT EXISTS (
                  SELECT 1 FROM pg_catalog.pg_index i
                  JOIN pg_catalog.pg_class t ON t.oid=i.indrelid
                  JOIN pg_catalog.pg_namespace tn ON tn.oid=t.relnamespace
                  JOIN pg_catalog.pg_class x ON x.oid=i.indexrelid
                  JOIN pg_catalog.pg_namespace xn ON xn.oid=x.relnamespace
                  JOIN pg_catalog.pg_am am ON am.oid=x.relam
                  JOIN pg_catalog.pg_attribute a ON a.attrelid=t.oid AND a.attname='OrganisationId'
                  WHERE tn.nspname='public' AND t.relname='OrganisationMemberships'
                    AND xn.nspname='public' AND x.relname='UX_OrganisationMemberships_ActiveOwner'
                    AND x.relowner=t.relowner AND x.reloptions IS NULL AND am.amname='btree'
                    AND i.indisunique AND NOT i.indisprimary AND NOT i.indisexclusion
                    AND i.indisvalid AND i.indisready AND i.indislive AND i.indimmediate
                    AND NOT i.indnullsnotdistinct AND i.indnkeyatts=1 AND i.indnatts=1
                    AND i.indexprs IS NULL AND i.indkey[0]=a.attnum
                    AND pg_catalog.pg_get_expr(i.indpred,i.indrelid,false)=@predicate
                    AND NOT EXISTS (SELECT 1 FROM pg_catalog.unnest(i.indoption::smallint[]) v(value) WHERE v.value<>0)
                    AND NOT EXISTS (SELECT 1 FROM pg_catalog.unnest(i.indclass::oid[]) v(value)
                      JOIN pg_catalog.pg_opclass op ON op.oid=v.value WHERE NOT op.opcdefault)
                    AND NOT EXISTS (SELECT 1 FROM pg_catalog.unnest(i.indcollation::oid[]) v(value) WHERE v.value<>a.attcollation)
                    AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_constraint c WHERE c.conindid=x.oid))
                """;
            var parameter = command.CreateParameter();
            parameter.ParameterName = "predicate";
            parameter.Value = CatalogPredicate;
            command.Parameters.Add(parameter);
            return await OwnerSqlExecution.ExecuteScalarAsync(command, cancellationToken) is true;
        }
        finally { if (close) await connection.CloseAsync(); }
    }
}
