using System.Diagnostics;
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
    var migratorPassword = ReadPassword("/run/secrets/migrator-password");
    var runtimePassword = ReadPassword("/run/secrets/runtime-password");
    var adminPassword = ReadPassword("/run/secrets/admin-password");
    var adminCs = Connection("postgres", "postgres", adminPassword);
    var migrationCs = Connection(service.Database, service.Migrator, migratorPassword);
    var runtimeDocument = JsonDocument.Parse(File.ReadAllText("/run/secrets/runtime-config.json"));
    var runtimeCs = runtimeDocument.RootElement.GetProperty("ConnectionStrings").GetProperty(service.ConnectionKey).GetString()!;
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
        await using var reader = await lookup.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            if (reader.GetString(0) != "postgres" || reader.GetString(1) != databaseComment)
                throw new InvalidOperationException("DATABASE_ESTABLISHED_OR_FOREIGN");
        }
        else
        {
            await reader.DisposeAsync();
            if (request.Completed) throw new InvalidOperationException("ESTABLISHED_DATABASE_MISSING");
            await Execute(admin, $"CREATE DATABASE {Quote(service.Database)} OWNER postgres TEMPLATE template0", ct);
            await Execute(admin, $"COMMENT ON DATABASE {Quote(service.Database)} IS {Literal(databaseComment)}", ct);
        }
    }
    stage = "role-identity-and-credentials";
    await VerifyMemberships(admin, service, request.Completed, ct);
    var createdRoles = await RoleRegistry.EnsureAsync(admin, request.InitializationId, request.SourceCommit, service.Key,
        new Dictionary<string,string> { ["migrator"] = migratorPassword, ["runtime"] = runtimePassword }, !request.Completed, ct);
    stage = "database-connection";
    await using var databaseAdmin = new NpgsqlConnection(Connection(service.Database, "postgres", adminPassword));
    await databaseAdmin.OpenAsync(ct);
    // Saved credentials and role edges must match before a reconciler can mutate anything.
    await VerifyMemberships(admin, service, request.Completed, ct);
    await FreshLogin(migrationCs, service.Migrator, false, ct);
    await FreshRuntime(runtimeCs, service, ct);
    if (request.Completed)
    {
        await VerifyManifest(databaseAdmin, service, ct);
        await FreshLogin(migrationCs, service.Migrator, false, ct);
        await FreshRuntime(runtimeCs, service, ct);
    }
    else
    {
        stage = "pre-bootstrap";
        await Execute(databaseAdmin, File.ReadAllText($"/run/source/bootstrap-{service.Key}-roles.sql"), ct);
    }
    stage = "restricted-migrator-plan";
    var plan = await Migrate("plan", service, migrationCs, ct);
    if (plan.Exit != 0) throw new SetupFailure("MIGRATOR_PLAN_REJECTED", plan.Exit);
    if (request.Completed && plan.Classification != "ALREADY_CURRENT")
        throw new InvalidOperationException("ESTABLISHED_MIGRATION_HISTORY_DRIFT");
    stage = "restricted-migrator-apply";
    var apply = await Migrate("apply", service, migrationCs, ct);
    if (apply.Exit != 0) throw new SetupFailure("MIGRATOR_APPLY_REJECTED", apply.Exit);
    if (request.Completed && apply.Classification != "ALREADY_CURRENT")
        throw new InvalidOperationException("ESTABLISHED_MIGRATION_HISTORY_DRIFT");
    if (!request.Completed)
    {
        stage = "post-bootstrap";
        await Execute(databaseAdmin, File.ReadAllText($"/run/source/bootstrap-{service.Key}-roles.sql"), ct);
    }
    stage = "security-manifest";
    await VerifyManifest(databaseAdmin, service, ct);
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
    var schemaHash = Digest(catalog);
    var historyHash = Digest(history);
    if (request.Completed && (request.SchemaSha256 != schemaHash || request.HistorySha256 != historyHash))
        throw new InvalidOperationException("ESTABLISHED_SCHEMA_OR_HISTORY_DRIFT");
    var result = new {
        service = service.Key, status = request.Completed ? "ALREADY_CURRENT" : "INITIALIZED",
        sourceCommit = request.SourceCommit, initializationId = request.InitializationId, clusterId,
        database = service.Database, createdRoles, planExit = plan.Exit, planClassification = plan.Classification,
        applyExit = apply.Exit, applyClassification = apply.Classification,

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
    var root = error is TargetInvocationException { InnerException: not null } invocation ? invocation.InnerException! : error;
    var safeCode = root is SetupFailure failure ? failure.Code :
        root is InvalidOperationException && KnownGuard(root.Message) ? root.Message :
        "DATABASE_SETUP_REJECTED";
    var safe = new { status="BLOCKED", stage, code=safeCode, sqlState=(root as PostgresException)?.SqlState,
        nativeExit=(root as SetupFailure)?.NativeExit, exceptionType=root.GetType().Name };
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
    "ROLE_REGISTRY_OPERATION_REJECTED";
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
    await using var reader=await command.ExecuteReaderAsync(ct);
    var count=0;
    while(await reader.ReadAsync(ct))
    {
        count++;
        if(reader.GetString(0)!=roles[0]||reader.GetString(1)!=service.Migrator
            ||reader.GetBoolean(2)||reader.GetBoolean(3)||!reader.GetBoolean(4))
            throw new InvalidOperationException("ROLE_MEMBERSHIP_DRIFT");
    }
    if(count>1||(completed&&count!=1))throw new InvalidOperationException("ROLE_MEMBERSHIP_DRIFT");
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
static async Task Execute(NpgsqlConnection c,string sql,CancellationToken ct) {await using var command=new NpgsqlCommand(sql,c){CommandTimeout=60};await command.ExecuteNonQueryAsync(ct);}
static async Task<object?> Scalar(NpgsqlConnection c,string sql,CancellationToken ct) {await using var command=new NpgsqlCommand(sql,c);return await command.ExecuteScalarAsync(ct);}
static async Task<List<string[]>> Rows(NpgsqlConnection c,string sql,CancellationToken ct)
{
    await using var command=new NpgsqlCommand(sql,c);await using var reader=await command.ExecuteReaderAsync(ct);
    var rows=new List<string[]>();
    while(await reader.ReadAsync(ct)){var row=new string[reader.FieldCount];for(var i=0;i<row.Length;i++)row[i]=reader.IsDBNull(i)?"":Convert.ToString(reader.GetValue(i),CultureInfo.InvariantCulture)!;rows.Add(row);}
    return rows;
}
static string Digest(List<string[]> values)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(values)))).ToLowerInvariant();
static DbContext Context(Service s,NpgsqlConnection c)=>s.Key switch {
    "auth"=>new AuthContext(new DbContextOptionsBuilder<AuthContext>().UseNpgsql(c).Options),
    "adminarea"=>new AdminContext(new DbContextOptionsBuilder<AdminContext>().UseNpgsql(c).Options),
    _=>new ClientContext(new DbContextOptionsBuilder<ClientContext>().UseNpgsql(c).Options)
};
static async Task VerifyManifest(NpgsqlConnection c,Service s,CancellationToken ct)
{
    await using var context=Context(s,c);var assembly=context.GetType().Assembly;
    var type=assembly.GetType("Zeka.PersistenceSecurity.RlsSecurityManifestVerifier",true)!;
    var method=type.GetMethods().Single(m=>m.Name=="VerifyAsync"&&m.GetParameters().Length==3&&m.GetParameters()[1].ParameterType==typeof(Assembly));
    await (Task)method.Invoke(null,[context,assembly,ct])!;
}
static async Task FreshLogin(string cs,string role,bool runtime,CancellationToken ct)
{
    await using var c=new NpgsqlConnection(cs);await c.OpenAsync(ct);
    await using var command=new NpgsqlCommand("select session_user,current_user from pg_roles where rolname=current_user",c);
    await using var reader=await command.ExecuteReaderAsync(ct);
    if(!await reader.ReadAsync(ct)||reader.GetString(0)!=role||reader.GetString(1)!=role)throw new InvalidOperationException("FRESH_LOGIN_IDENTITY_REJECTED");
}
static async Task FreshRuntime(string cs,Service s,CancellationToken ct)
{
    await FreshLogin(cs,s.Runtime,true,ct);
    using var c=new NpgsqlConnection(Connection(s.Database,s.Runtime,ReadPassword("/run/secrets/runtime-password")));
    using var context=Context(s,c);var type=context.GetType().Assembly.GetType("Zeka.PersistenceSecurity.RuntimeDatabaseIdentityValidator",true)!;
    var validator=Activator.CreateInstance(type,cs,s.Runtime)!;
    await (Task)type.GetMethod("StartAsync")!.Invoke(validator,[ct])!;
}
static async Task<MigrationRun> Migrate(string operation,Service s,string credential,CancellationToken ct)
{
    var identifier=Guid.NewGuid().ToString("D");var file=operation+"-"+identifier+".json";
    var info=new ProcessStartInfo("dotnet"){UseShellExecute=false,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
    foreach(var value in new[]{"exec","--depsfile","./zeka-local-db-setup.deps.json","--runtimeconfig","./zeka-local-db-setup.runtimeconfig.json","./zeka-db-migrate.dll",operation,"--service",s.Key,"--target","latest","--operation-id",identifier,"--evidence-root","/run/results","--evidence-file",file,"--lock-timeout-seconds","10","--credential-stdin"})info.ArgumentList.Add(value);
    using var process=Process.Start(info)??throw new InvalidOperationException("MIGRATION_PROCESS_START_FAILED");
    var stdout=process.StandardOutput.ReadToEndAsync(ct);var stderr=process.StandardError.ReadToEndAsync(ct);
    await process.StandardInput.WriteLineAsync(credential);process.StandardInput.Close();
    await process.WaitForExitAsync(ct);await stdout;await stderr;
    using var result=JsonDocument.Parse(File.ReadAllText("/run/results/"+file));var root=result.RootElement;
    return new MigrationRun(process.ExitCode,root.GetProperty("classification").GetString()!,
        root.GetProperty("phases").GetProperty("migration").GetProperty("status").GetString()=="passed",
        root.GetProperty("phases").GetProperty("manifestVerification").GetProperty("status").GetString()=="failed");
}
internal sealed record SetupRequest(string Project,string Service,string InitializationId,string SourceCommit,bool Completed,string? ClusterId,string? SchemaSha256,string? HistorySha256);
internal sealed record MigrationRun(int Exit,string Classification,bool MigrationPassed,bool ManifestFailed);
internal sealed class SetupFailure(string code,int nativeExit):Exception {public string Code{get;}=code;public int NativeExit{get;}=nativeExit;}
internal sealed record Service(string Key,string Database,string Migrator,string Runtime,string ConnectionKey)
{
    public static Service For(string key)=>key switch {
        "auth"=>new(key,"zeka_m1_auth","zeka_auth_migrator","zeka_auth_runtime","Default"),
        "adminarea"=>new(key,"zeka_m1_adminarea","zeka_adminarea_migrator","zeka_adminarea_runtime","ClientApiConnection"),
        "client"=>new(key,"zeka_m1_client","zeka_client_migrator","zeka_client_runtime","ClientApiConnection"),
        _=>throw new InvalidOperationException("SERVICE_REJECTED")
    };
}
