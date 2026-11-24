using System.Runtime.CompilerServices;
using Zeka.OwnerSqlEvidence;
using Zeka.ToolingDiagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using AuthContext = AuthManager.Infrastructure.Persistence.AuthDbContext;
using AdminContext = AdminAreaManagement.Infrastructure.Persistence.DeploymentDbContext;
using ClientContext = ClientManagement.Infrastructure.Persistence.DeploymentDbContext;
using Zeka.LocalStack.DatabaseSetup;

var stage = "input";
var postgres = new SafePostgresDiagnosticCapture();
var bootstrapExecutions = 0;
var migrationOperations = new List<object>();
try
{
    if (args is ["--guard-checks"]) { using var checks = new CancellationTokenSource(TimeSpan.FromMinutes(3)); return await GuardChecks.RunAsync(checks.Token); }
    if (args.Length != 0) throw new InvalidOperationException("ARGUMENTS_REJECTED");
    var request = JsonSerializer.Deserialize<SetupRequest>(File.ReadAllText("/run/setup/request.json"))
        ?? throw new InvalidOperationException("REQUEST_MISSING");
    if (request.Project != "zeka-m1" || !Guid.TryParseExact(request.InitializationId, "D", out var id) || id == Guid.Empty
        || request.SourceCommit.Length != 40 || request.SourceCommit.Any(c => !Uri.IsHexDigit(c)))
        throw new InvalidOperationException("REQUEST_IDENTITY_REJECTED");
    var service = Service.For(request.Service);
    if (request.Completed && (string.IsNullOrEmpty(request.ClusterId) || request.ClusterId.Length > 20
        || request.ClusterId.Any(c => !char.IsAsciiDigit(c)) || !IsDigest(request.SchemaSha256) || !IsDigest(request.HistorySha256)))
        throw new InvalidOperationException("ESTABLISHED_REQUEST_BINDING_REJECTED");
    var migratorPassword = ReadPassword("/run/secrets/migrator-password");
    var runtimePassword = ReadPassword("/run/secrets/runtime-password");
    var adminPassword = ReadPassword("/run/secrets/admin-password");
    postgres.RegisterSecret(migratorPassword); postgres.RegisterSecret(runtimePassword); postgres.RegisterSecret(adminPassword);
    var adminCs = Connection("postgres", "postgres", adminPassword);
    var migrationCs = Connection(service.Database, service.Migrator, migratorPassword);
    var runtimeDocument = JsonDocument.Parse(File.ReadAllText("/run/secrets/runtime-config.json"));
    var runtimeCs = SafePostgresDiagnostic.WithoutErrorDetail(runtimeDocument.RootElement.GetProperty("ConnectionStrings").GetProperty(service.ConnectionKey).GetString()!);
    var runtimeBuilder = new NpgsqlConnectionStringBuilder(runtimeCs);
    if (runtimeBuilder.Host != "postgres" || runtimeBuilder.Port != 5432 || runtimeBuilder.Database != service.Database
        || runtimeBuilder.Username != service.Runtime || runtimeBuilder.Password != runtimePassword || runtimeBuilder.Pooling)
        throw new InvalidOperationException("RUNTIME_CONFIGURATION_IDENTITY_REJECTED");
    using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(5));
    var ct = limit.Token;
    stage = "administrator-authentication";
    await using var admin = new NpgsqlConnection(adminCs);
    await admin.OpenAsync(ct);
    if (!(bool)(await Scalar(admin, "select pg_try_advisory_lock(606508117326122)", ct))!)
        throw new InvalidOperationException("SETUP_LOCK_UNAVAILABLE");
    var clusterId = (string)(await Scalar(admin, "select system_identifier::text from pg_control_system()", ct))!;
    if (request.ClusterId is not null && request.ClusterId != clusterId)
        throw new InvalidOperationException("POSTGRES_INITIALIZATION_IDENTITY_DRIFT");
    stage = "database-identity";
    var databaseComment = $"zeka-m1-database/v1:{request.InitializationId}:{service.Key}:{request.SourceCommit}";
    await using (var lookup = new NpgsqlCommand("select pg_get_userbyid(datdba),coalesce(shobj_description(oid,'pg_database'),'') from pg_database where datname=@name", admin))
    {
        lookup.Parameters.AddWithValue("name", service.Database);
        await using var reader = await OwnerSqlExecution.ExecuteReaderAsync(lookup, ct);
        if (await reader.ReadAsync(ct))
        {
            if (reader.GetString(0) != "postgres" || reader.GetString(1) != databaseComment)
                throw new InvalidOperationException("DATABASE_ESTABLISHED_OR_FOREIGN");
            await OwnerSqlExecution.CompleteReaderAsync(reader, ct);
        }
        else
        {
            await OwnerSqlExecution.CompleteReaderAsync(reader, ct);
            await reader.DisposeAsync();
            if (request.Completed) throw new InvalidOperationException("ESTABLISHED_DATABASE_MISSING");
            await Execute(admin, $"CREATE DATABASE {Quote(service.Database)} OWNER postgres TEMPLATE template0", ct);
            await Execute(admin, $"COMMENT ON DATABASE {Quote(service.Database)} IS {Literal(databaseComment)}", ct);
        }
    }
    await using var databaseAdmin = new NpgsqlConnection(Connection(service.Database, "postgres", adminPassword));
    if (service.Key == "auth")
    {
        using var compiledContext = Context(service, databaseAdmin);
        var compiled = compiledContext.Database.GetMigrations().ToArray();
        if (compiled.Length < 3 || compiled[^3] != ManagedMigrationAdapter.AuthPredecessor
            || compiled[^2] != ManagedMigrationAdapter.AuthTarget || compiled[^1] != ManagedMigrationAdapter.AuthOwnerIndexTarget)
            throw new InvalidOperationException("AUTH_COMPILED_HISTORY_REJECTED");
        if (!request.Completed)
        {
            stage = "auth-fresh-state";
            await databaseAdmin.OpenAsync(ct);
            // A failed/partial earlier initialization is inspection work, not a new fresh run.
            var empty = await Scalar(databaseAdmin, """
                SELECT NOT EXISTS (SELECT 1 FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='public')
                  AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='public')
                  AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_type t JOIN pg_catalog.pg_namespace n ON n.oid=t.typnamespace WHERE n.nspname='public')
                  AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_namespace WHERE nspname NOT IN ('public','pg_catalog','information_schema','pg_toast'))
                  AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_default_acl)
                """, ct);
            if (empty is not true) throw new InvalidOperationException("AUTH_FRESH_STATE_REQUIRES_INSPECTION");
        }
    }
    stage = "role-identity-and-credentials";
    await VerifyMemberships(admin, service, request.Completed, ct);
    var createdRoles = await RoleRegistry.EnsureAsync(admin, request.InitializationId, request.SourceCommit, service.Key,
        new Dictionary<string,string> { ["migrator"] = migratorPassword, ["runtime"] = runtimePassword }, !request.Completed, ct);
    stage = "database-connection";
    if (databaseAdmin.State != System.Data.ConnectionState.Open) await databaseAdmin.OpenAsync(ct);
    // Saved credentials and role edges must match before a reconciler can mutate anything.
    await VerifyMemberships(admin, service, request.Completed, ct);
    await FreshLogin(migrationCs, service.Migrator, false, ct);
    await FreshRuntime(runtimeCs, service, ct);
    if (request.Completed)
    {
        await VerifyManifest(databaseAdmin, service, postgres, ct);
        await FreshLogin(migrationCs, service.Migrator, false, ct);
        await FreshRuntime(runtimeCs, service, ct);
    }
    else
    {
        stage = "pre-bootstrap";
        await Execute(databaseAdmin, File.ReadAllText($"/run/source/bootstrap-{service.Key}-roles.sql"), ct, bootstrapService: service.Key);
        bootstrapExecutions++;
    }

    async Task<MigrationRun> RunMigration(string operation, string target)
    {
        stage = "restricted-migrator-" + operation;
        var run = await ManagedMigrationAdapter.RunAsync(operation, service.Key, request.SourceCommit,
            migrationCs, "/run/results", ct, requestedTarget: target, expectedDatabase: service.Database);
        migrationOperations.Add(new { operationId = run.OperationId, operation, service = service.Key,
            requestedTarget = run.RequestedTarget, classification = run.Classification, resultCode = run.ResultCode,
            exit = run.Exit, atomicAuth = run.AtomicAuth, observedFinalMigration = run.ObservedFinalMigration,
            receiptFile = run.ReceiptFile });
        if (run.Exit != 0) throw new SetupFailure(operation == "plan" ? "MIGRATOR_PLAN_REJECTED" : "MIGRATOR_APPLY_REJECTED",
            run.Exit, run.OperationId, run.ReceiptFile) { PostgresDiagnostic = run.PostgresDiagnostic };
        return run;
    }

    MigrationRun plan;
    MigrationRun apply;
    if (service.Key == "auth")
    {
        if (!request.Completed)
        {
            await RunMigration("plan", ManagedMigrationAdapter.AuthPredecessor);
            var predecessor = await RunMigration("apply", ManagedMigrationAdapter.AuthPredecessor);
            if (predecessor.AtomicAuth || predecessor.ObservedFinalMigration != ManagedMigrationAdapter.AuthPredecessor)
                throw new InvalidOperationException("AUTH_PREDECESSOR_BINDING_REJECTED");
            stage = "auth-predecessor-bootstrap";
            await ReconcileAuthPredecessor(databaseAdmin, service, postgres, ct);
            bootstrapExecutions++;
        }
        if (!request.Completed)
        {
            var onboardingPlan = await RunMigration("plan", ManagedMigrationAdapter.AuthTarget);
            if (onboardingPlan.Classification != "FORWARD_APPROVED")
                throw new InvalidOperationException("AUTH_PREDECESSOR_BINDING_REJECTED");
            var onboarding = await RunMigration("apply", ManagedMigrationAdapter.AuthTarget);
            if (!onboarding.AtomicAuth || onboarding.Classification != "ATOMIC_AUTH_APPLIED")
                throw new InvalidOperationException("AUTH_ATOMIC_STATE_REJECTED");
        }
        plan = await RunMigration("plan", ManagedMigrationAdapter.AuthOwnerIndexTarget);
        if (request.Completed && !plan.IsAcknowledgedCurrent)
            throw new InvalidOperationException("ESTABLISHED_MIGRATION_HISTORY_DRIFT");
        if (!request.Completed && plan.Classification != "FORWARD_APPROVED")
            throw new InvalidOperationException("AUTH_PREDECESSOR_BINDING_REJECTED");
        apply = await RunMigration("apply", ManagedMigrationAdapter.AuthOwnerIndexTarget);
        if (!apply.AtomicAuth || (request.Completed ? !apply.IsAcknowledgedCurrent : apply.Classification != "ATOMIC_AUTH_APPLIED"))
            throw new InvalidOperationException("AUTH_ATOMIC_STATE_REJECTED");
        // No post-target bootstrap: the exact v20 then v21 executor/grant checks
        // must stand on their own and may not be repaired before verification.
    }
    else
    {
        plan = await RunMigration("plan", "latest");
        if (request.Completed && !plan.IsAcknowledgedCurrent)
            throw new InvalidOperationException("ESTABLISHED_MIGRATION_HISTORY_DRIFT");
        apply = await RunMigration("apply", "latest");
        if (request.Completed && !apply.IsAcknowledgedCurrent)
            throw new InvalidOperationException("ESTABLISHED_MIGRATION_HISTORY_DRIFT");
        if (!request.Completed)
        {
            stage = "post-bootstrap";
            await Execute(databaseAdmin, File.ReadAllText($"/run/source/bootstrap-{service.Key}-roles.sql"), ct, bootstrapService: service.Key);
            bootstrapExecutions++;
        }
    }
    stage = "security-manifest";
    await VerifyManifest(databaseAdmin, service, postgres, ct);
    stage = "runtime-password-authentication";
    await FreshRuntime(runtimeCs, service, ct);
    stage = "migrator-password-authentication";
    await FreshLogin(migrationCs, service.Migrator, false, ct);
    stage = "password-negative-control";
    var wrong = new NpgsqlConnectionStringBuilder(runtimeCs) { Password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), Pooling = false };
    var rejected = false;
    try { await using var negative = new NpgsqlConnection(wrong.ConnectionString); await negative.OpenAsync(ct); }
    catch (PostgresException e) when (e.SqlState == "28P01") { rejected = true; }
    if (!rejected) throw new InvalidOperationException("PASSWORD_AUTHENTICATION_NOT_PROVEN");
    stage = "initialization-role-comments";
    await RoleRegistry.EnsureAsync(admin, request.InitializationId, request.SourceCommit, service.Key,
        new Dictionary<string,string> { ["migrator"] = migratorPassword, ["runtime"] = runtimePassword }, false, ct);
    stage = "schema-history-snapshot";
    var history = await Rows(databaseAdmin, """select "MigrationId","ProductVersion" from public."__EFMigrationsHistory" order by "MigrationId" """, ct);
    var catalog = await Rows(databaseAdmin, """
        select 'relation',n.nspname,c.relname,c.relkind::text,pg_get_userbyid(c.relowner),
               c.relrowsecurity::text,c.relforcerowsecurity::text,coalesce(c.relacl::text,'')
        from pg_class c join pg_namespace n on n.oid=c.relnamespace where n.nspname in ('public','zeka')
        union all
        select 'column',n.nspname,c.relname,a.attname,format_type(a.atttypid,a.atttypmod),
               a.attnotnull::text,coalesce(pg_get_expr(d.adbin,d.adrelid),''),coalesce(a.attacl::text,'')
        from pg_attribute a join pg_class c on c.oid=a.attrelid join pg_namespace n on n.oid=c.relnamespace
        left join pg_attrdef d on d.adrelid=c.oid and d.adnum=a.attnum
        where n.nspname in ('public','zeka') and a.attnum>0 and not a.attisdropped
        union all
        select 'constraint',n.nspname,c.relname,t.conname,pg_get_constraintdef(t.oid),t.contype::text,'',''
        from pg_constraint t join pg_class c on c.oid=t.conrelid join pg_namespace n on n.oid=c.relnamespace where n.nspname in ('public','zeka')
        union all
        select 'function',n.nspname,p.proname,pg_get_function_identity_arguments(p.oid),pg_get_functiondef(p.oid),
               pg_get_userbyid(p.proowner),coalesce(p.proacl::text,''),''
        from pg_proc p join pg_namespace n on n.oid=p.pronamespace where n.nspname in ('public','zeka') and p.prokind='f'
        union all
        select 'policy',schemaname,tablename,policyname,coalesce(qual,''),coalesce(with_check,''),roles::text,cmd
        from pg_policies where schemaname in ('public','zeka')
        order by 1,2,3,4,5,6,7,8
        """, ct);
    if (service.Key == "auth")
        catalog.AddRange(await Rows(databaseAdmin, """
            SELECT 'schema',n.nspname,'','',pg_get_userbyid(n.nspowner),coalesce(n.nspacl::text,''),'',''
            FROM pg_catalog.pg_namespace n WHERE n.nspname IN ('public','zeka')
            UNION ALL
            SELECT 'default-acl',coalesce(n.nspname,''),r.rolname,d.defaclobjtype::text,coalesce(d.defaclacl::text,''),'','',''
            FROM pg_catalog.pg_default_acl d JOIN pg_catalog.pg_roles r ON r.oid=d.defaclrole
            LEFT JOIN pg_catalog.pg_namespace n ON n.oid=d.defaclnamespace
            UNION ALL
            SELECT 'role','',r.rolname,'',jsonb_build_object('login',r.rolcanlogin,'superuser',r.rolsuper,'bypassrls',r.rolbypassrls,
                'createdb',r.rolcreatedb,'createrole',r.rolcreaterole,'inherit',r.rolinherit,'replication',r.rolreplication,
                'connectionLimit',r.rolconnlimit,'validUntil',r.rolvaliduntil,'settings',r.rolconfig)::text,'','',''
            FROM pg_catalog.pg_roles r WHERE r.rolname IN ('zeka_auth_owner','zeka_auth_migrator','zeka_auth_runtime','zeka_auth_closure_recovery')
            UNION ALL
            SELECT 'membership','',parent.rolname,member.rolname,m.admin_option::text,m.inherit_option::text,m.set_option::text,pg_get_userbyid(m.grantor)
            FROM pg_catalog.pg_auth_members m JOIN pg_catalog.pg_roles parent ON parent.oid=m.roleid
            JOIN pg_catalog.pg_roles member ON member.oid=m.member
            WHERE parent.rolname IN ('zeka_auth_owner','zeka_auth_migrator','zeka_auth_runtime','zeka_auth_closure_recovery')
               OR member.rolname IN ('zeka_auth_owner','zeka_auth_migrator','zeka_auth_runtime','zeka_auth_closure_recovery')
            UNION ALL
            SELECT 'policy-mode',schemaname,tablename,policyname,permissive,cmd,roles::text,''
            FROM pg_catalog.pg_policies WHERE schemaname IN ('public','zeka')
            UNION ALL
            SELECT 'sequence-definition',n.nspname,c.relname,'',jsonb_build_object('type',q.seqtypid::regtype::text,
                'start',q.seqstart,'increment',q.seqincrement,'max',q.seqmax,'min',q.seqmin,'cache',q.seqcache,'cycle',q.seqcycle)::text,'','',''
            FROM pg_catalog.pg_sequence q JOIN pg_catalog.pg_class c ON c.oid=q.seqrelid
            JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname IN ('public','zeka')
            UNION ALL
            SELECT 'role-setting',coalesce(d.datname,''),coalesce(r.rolname,''),'',s.setconfig::text,'','',''
            FROM pg_catalog.pg_db_role_setting s LEFT JOIN pg_catalog.pg_database d ON d.oid=s.setdatabase
            LEFT JOIN pg_catalog.pg_roles r ON r.oid=s.setrole
            WHERE s.setdatabase=(SELECT oid FROM pg_catalog.pg_database WHERE datname=current_database())
               OR r.rolname IN ('zeka_auth_owner','zeka_auth_migrator','zeka_auth_runtime','zeka_auth_closure_recovery')
            ORDER BY 1,2,3,4,5,6,7,8
            """, ct));
    var schemaHash = Digest(catalog);
    var historyHash = Digest(history);
    if (request.Completed && (request.SchemaSha256 != schemaHash || request.HistorySha256 != historyHash))
        throw new InvalidOperationException("ESTABLISHED_SCHEMA_OR_HISTORY_DRIFT");
    var result = new {
        service = service.Key, status = request.Completed ? "ALREADY_CURRENT" : "INITIALIZED",
        sourceCommit = request.SourceCommit, initializationId = request.InitializationId, clusterId,
        database = service.Database, createdRoles, planExit = plan.Exit, planClassification = plan.Classification,
        applyExit = apply.Exit, applyClassification = apply.Classification,

        bootstrapExecutions, migrationOperations,
        manifest = "passed", runtimeAuthentication = "passed", wrongPasswordRejected = rejected,
        runtimeIdentity = service.Runtime, migratorIdentity = service.Migrator,
        schemaSha256 = schemaHash, historySha256 = historyHash, migrations = history.Count,
        finalMigration = history.LastOrDefault()?.FirstOrDefault()
    };
    File.WriteAllText("/run/results/result.json", JsonSerializer.Serialize(result));
    Console.WriteLine(JsonSerializer.Serialize(new { service=service.Key,status=request.Completed ? "ALREADY_CURRENT" : "INITIALIZED",manifest="passed",runtime="passed",negativePassword="rejected" }));
    return 0;
}
catch (Exception error)
{
    postgres.Capture(error);
    var root = error is TargetInvocationException { InnerException: not null } invocation ? invocation.InnerException! : error;
    var safeCode = root is SetupFailure failure ? failure.Code :
        root is InvalidOperationException && KnownGuard(root.Message) ? root.Message :
        "DATABASE_SETUP_REJECTED";
    var safe = new { status="BLOCKED", stage, code=safeCode, sqlState=postgres.Diagnostic?.SqlState, postgresDiagnostic=postgres.Diagnostic,
        nativeExit=(root as SetupFailure)?.NativeExit, operationId=(root as SetupFailure)?.OperationId,
        receiptFile=(root as SetupFailure)?.ReceiptFile, exceptionType=root.GetType().Name, bootstrapExecutions, migrationOperations };
    File.WriteAllText("/run/results/result.json", JsonSerializer.Serialize(safe));
    Console.WriteLine(JsonSerializer.Serialize(safe));
    return 1;
}

static bool KnownGuard(string code) => code is
    "ARGUMENTS_REJECTED" or "REQUEST_MISSING" or "REQUEST_IDENTITY_REJECTED" or
    "RUNTIME_CONFIGURATION_IDENTITY_REJECTED" or "SETUP_LOCK_UNAVAILABLE" or
    "POSTGRES_INITIALIZATION_IDENTITY_DRIFT" or "DATABASE_ESTABLISHED_OR_FOREIGN" or
    "ESTABLISHED_DATABASE_MISSING" or "ESTABLISHED_MIGRATION_HISTORY_DRIFT" or
    "PASSWORD_AUTHENTICATION_NOT_PROVEN" or "ESTABLISHED_SCHEMA_OR_HISTORY_DRIFT" or
    "SAVED_CREDENTIAL_FORMAT_REJECTED" or "FRESH_LOGIN_IDENTITY_REJECTED" or
    "MIGRATION_PROCESS_START_FAILED" or "SERVICE_REJECTED" or "ROLE_MEMBERSHIP_DRIFT" or
    "ROLE_REGISTRY_ADMIN_CONNECTION_REJECTED" or "ROLE_REGISTRY_INPUT_REJECTED" or
    "ROLE_REGISTRY_LOCK_UNAVAILABLE" or "ROLE_REGISTRY_MISSING_ESTABLISHED_ROLE" or
    "ROLE_REGISTRY_POSTCONDITION_REJECTED" or "ROLE_REGISTRY_FOREIGN_OR_DRIFTED_ROLE" or
    "ROLE_REGISTRY_OPERATION_REJECTED" or "ESTABLISHED_REQUEST_BINDING_REJECTED" or
    "AUTH_COMPILED_HISTORY_REJECTED" or "AUTH_FRESH_STATE_REQUIRES_INSPECTION" or
    "AUTH_PREDECESSOR_BINDING_REJECTED" or "AUTH_BOOTSTRAP_LOCK_UNAVAILABLE" or
    "AUTH_BOOTSTRAP_UNLOCK_UNACKNOWLEDGED" or "AUTH_ATOMIC_STATE_REJECTED";
static bool IsDigest(string? value) => value is { Length: 64 } && value.All(c => char.IsAsciiHexDigit(c));
static async Task ReconcileAuthPredecessor(NpgsqlConnection database, Service service, SafePostgresDiagnosticCapture diagnostics, CancellationToken ct)
{
    // Use the same database-scoped key as both existing migrators. Holding this
    // session lock prevents a target apply from racing predecessor-only bootstrap.
    if (await Scalar(database, """
        SELECT pg_catalog.pg_try_advisory_lock(1515870810,
          CASE WHEN oid::bigint>2147483647 THEN (oid::bigint-4294967296)::integer ELSE oid::integer END)
        FROM pg_catalog.pg_database WHERE datname=pg_catalog.current_database()
        """, ct) is not true) throw new InvalidOperationException("AUTH_BOOTSTRAP_LOCK_UNAVAILABLE");
    Exception? primaryFailure = null;
    try
    {
        using var context = Context(service, database);
        var expected = context.Database.GetMigrations().ToArray();
        if (expected.Length < 3 || expected[^3] != ManagedMigrationAdapter.AuthPredecessor || expected[^2] != ManagedMigrationAdapter.AuthTarget
            || expected[^1] != ManagedMigrationAdapter.AuthOwnerIndexTarget)
            throw new InvalidOperationException("AUTH_COMPILED_HISTORY_REJECTED");
        var before = await Rows(database, """
            SELECT "MigrationId" FROM public."__EFMigrationsHistory" ORDER BY "MigrationId"
            """, ct);
        if (!before.Select(row => row[0]).SequenceEqual(expected[..^2], StringComparer.Ordinal))
            throw new InvalidOperationException("AUTH_PREDECESSOR_BINDING_REJECTED");
        await Execute(database, File.ReadAllText("/run/source/bootstrap-auth-roles.sql"), ct, bootstrapService: "auth");
        var after = await Rows(database, """
            SELECT "MigrationId" FROM public."__EFMigrationsHistory" ORDER BY "MigrationId"
            """, ct);
        if (!after.Select(row => row[0]).SequenceEqual(expected[..^2], StringComparer.Ordinal))
            throw new InvalidOperationException("AUTH_PREDECESSOR_BINDING_REJECTED");
    }
    catch (Exception error) { diagnostics.Capture(error); primaryFailure = error; throw; }
    finally
    {
        try
        {
            using var release = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            if (await Scalar(database, """
                SELECT pg_catalog.pg_advisory_unlock(1515870810,
                  CASE WHEN oid::bigint>2147483647 THEN (oid::bigint-4294967296)::integer ELSE oid::integer END)
                FROM pg_catalog.pg_database WHERE datname=pg_catalog.current_database()
                """, release.Token) is not true) throw new InvalidOperationException("AUTH_BOOTSTRAP_UNLOCK_UNACKNOWLEDGED");
        }
        catch (Exception releaseError) { diagnostics.Capture(releaseError); if (primaryFailure is null) throw; }
    }
}
static async Task VerifyMemberships(NpgsqlConnection admin,Service service,bool completed,CancellationToken ct)
{
    var roles=new[]{"zeka_"+service.Key+"_owner",service.Migrator,service.Runtime,"zeka_"+service.Key+"_closure_recovery"};
    await using var command=new NpgsqlCommand("""
        select parent.rolname,member.rolname,m.admin_option,m.inherit_option,m.set_option
        from pg_auth_members m join pg_roles parent on parent.oid=m.roleid
        join pg_roles member on member.oid=m.member
        where parent.rolname=any(@roles) or member.rolname=any(@roles)
        """,admin);
    command.Parameters.AddWithValue("roles",roles);
    await using var reader=await OwnerSqlExecution.ExecuteReaderAsync(command, ct);
    var count=0;
    while(await reader.ReadAsync(ct))
    {
        count++;
        if(reader.GetString(0)!=roles[0]||reader.GetString(1)!=service.Migrator
            ||reader.GetBoolean(2)||reader.GetBoolean(3)||!reader.GetBoolean(4))
            throw new InvalidOperationException("ROLE_MEMBERSHIP_DRIFT");
    }
    if(count>1||(completed&&count!=1))throw new InvalidOperationException("ROLE_MEMBERSHIP_DRIFT");
    await OwnerSqlExecution.CompleteReaderAsync(reader, ct);
}
static string ReadPassword(string path)
{
    var value=File.ReadAllText(path).Trim();
    if (value.Length!=64 || value.Any(c=>!Uri.IsHexDigit(c))) throw new InvalidOperationException("SAVED_CREDENTIAL_FORMAT_REJECTED");
    return value;
}
static string Connection(string database,string user,string password) => new NpgsqlConnectionStringBuilder {
    Host="postgres",Port=5432,Database=database,Username=user,Password=password,Pooling=false,
    Timeout=5,CommandTimeout=30,SslMode=SslMode.Disable,IncludeErrorDetail=false
}.ConnectionString;
static string Quote(string value)=>new NpgsqlCommandBuilder().QuoteIdentifier(value);
static string Literal(string value)=>"'"+value.Replace("'","''")+"'";
static async Task Execute(NpgsqlConnection c,string sql,CancellationToken ct, string? bootstrapService=null, [CallerFilePath] string producerFile="", [CallerLineNumber] int producerLine=0)
{
    await using var command=new NpgsqlCommand(sql,c){CommandTimeout=60};
    if (bootstrapService is null) await OwnerSqlExecution.ExecuteNonQueryAsync(command, ct, producerFile, producerLine);
    else await OwnerSqlExecution.ExecuteBootstrapNonQueryAsync(command, ct, bootstrapService, producerFile, producerLine);
}
static async Task<object?> Scalar(NpgsqlConnection c,string sql,CancellationToken ct, [CallerFilePath] string producerFile="", [CallerLineNumber] int producerLine=0) {await using var command=new NpgsqlCommand(sql,c);return await OwnerSqlExecution.ExecuteScalarAsync(command, ct, producerFile, producerLine);}
static async Task<List<string[]>> Rows(NpgsqlConnection c,string sql,CancellationToken ct, [CallerFilePath] string producerFile="", [CallerLineNumber] int producerLine=0)
{
    await using var command=new NpgsqlCommand(sql,c);await using var reader=await OwnerSqlExecution.ExecuteReaderAsync(command, ct, producerFile, producerLine);
    var rows=new List<string[]>();
    while(await reader.ReadAsync(ct)){var row=new string[reader.FieldCount];for(var i=0;i<row.Length;i++)row[i]=reader.IsDBNull(i)?"":Convert.ToString(reader.GetValue(i),CultureInfo.InvariantCulture)!;rows.Add(row);}
    await OwnerSqlExecution.CompleteReaderAsync(reader, ct);
    return rows;
}
static string Digest(List<string[]> values)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(values)))).ToLowerInvariant();
static DbContext Context(Service s,NpgsqlConnection c,SafePostgresDiagnosticCapture? capture=null)=>s.Key switch {
    "auth"=>new AuthContext(new DbContextOptionsBuilder<AuthContext>().UseNpgsql(c).AddInterceptors(capture is null ? [] : new[] { new SafePostgresCommandInterceptor(capture) }).Options),
    "adminarea"=>new AdminContext(new DbContextOptionsBuilder<AdminContext>().UseNpgsql(c).AddInterceptors(capture is null ? [] : new[] { new SafePostgresCommandInterceptor(capture) }).Options),
    _=>new ClientContext(new DbContextOptionsBuilder<ClientContext>().UseNpgsql(c).AddInterceptors(capture is null ? [] : new[] { new SafePostgresCommandInterceptor(capture) }).Options)
};
static async Task VerifyManifest(NpgsqlConnection c,Service s,SafePostgresDiagnosticCapture capture,CancellationToken ct)
{
    await using var context=Context(s,c,capture);var assembly=context.GetType().Assembly;
    var type=assembly.GetType("Zeka.PersistenceSecurity.RlsSecurityManifestVerifier",true)!;
    var method=type.GetMethods().Single(m=>m.Name=="VerifyAsync"&&m.GetParameters().Length==3&&m.GetParameters()[1].ParameterType==typeof(Assembly));
    await (Task)method.Invoke(null,[context,assembly,ct])!;
}
static async Task FreshLogin(string cs,string role,bool runtime,CancellationToken ct)
{
    await using var c=new NpgsqlConnection(cs);await c.OpenAsync(ct);
    await using var command=new NpgsqlCommand("select session_user,current_user from pg_roles where rolname=current_user",c);
    await using var reader=await OwnerSqlExecution.ExecuteReaderAsync(command, ct);
    if(!await reader.ReadAsync(ct)||reader.GetString(0)!=role||reader.GetString(1)!=role)throw new InvalidOperationException("FRESH_LOGIN_IDENTITY_REJECTED");
    await OwnerSqlExecution.CompleteReaderAsync(reader, ct);
}
static async Task FreshRuntime(string cs,Service s,CancellationToken ct)
{
    await FreshLogin(cs,s.Runtime,true,ct);
    using var c=new NpgsqlConnection(Connection(s.Database,s.Runtime,ReadPassword("/run/secrets/runtime-password")));
    using var context=Context(s,c);var type=context.GetType().Assembly.GetType("Zeka.PersistenceSecurity.RuntimeDatabaseIdentityValidator",true)!;
    var validator=Activator.CreateInstance(type,cs,s.Runtime)!;
    await (Task)type.GetMethod("StartAsync")!.Invoke(validator,[ct])!;
}
internal sealed record SetupRequest(string Project,string Service,string InitializationId,string SourceCommit,bool Completed,string? ClusterId,string? SchemaSha256,string? HistorySha256);
internal sealed record Service(string Key,string Database,string Migrator,string Runtime,string ConnectionKey)
{
    public static Service For(string key)=>key switch {
        "auth"=>new(key,"zeka_m1_auth","zeka_auth_migrator","zeka_auth_runtime","Default"),
        "adminarea"=>new(key,"zeka_m1_adminarea","zeka_adminarea_migrator","zeka_adminarea_runtime","ClientApiConnection"),
        "client"=>new(key,"zeka_m1_client","zeka_client_migrator","zeka_client_runtime","ClientApiConnection"),
        _=>throw new InvalidOperationException("SERVICE_REJECTED")
    };
}
