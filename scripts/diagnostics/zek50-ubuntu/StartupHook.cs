using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

// The runtime requires this exact global type and method name.
public static class StartupHook
{
    private static readonly object Sync = new();
    private static readonly ConcurrentBag<IDisposable> Subscriptions = new();
    private static string? destination;
    private static int invocation;
    private static int failed;
    private static readonly HashSet<string> Categories = new(StringComparer.Ordinal)
    {
        "Npgsql.PostgresException", "Npgsql.NpgsqlException", "Docker.DotNet.DockerApiException",
        "System.TimeoutException", "System.OperationCanceledException", "System.Threading.Tasks.TaskCanceledException",
        "System.InvalidOperationException", "System.IO.IOException", "System.IO.FileNotFoundException",
        "System.UnauthorizedAccessException", "System.OutOfMemoryException", "System.TypeInitializationException",
        "System.TypeLoadException", "System.BadImageFormatException", "System.DllNotFoundException",
        "System.Net.Sockets.SocketException", "System.Net.Http.HttpRequestException",
        "System.AccessViolationException", "System.Runtime.InteropServices.SEHException"
    };

    public static void Initialize()
    {
        try
        {
            var folder = Environment.GetEnvironmentVariable("ZEKA_DIAG_SAFE_DIR");
            if (folder is null || !Path.IsPathFullyQualified(folder) || !Directory.Exists(folder)
                || !int.TryParse(Environment.GetEnvironmentVariable("ZEKA_DIAG_INVOCATION"), out invocation)
                || invocation is < 1 or > 5)
            {
                Fail();
                return;
            }
            destination = Path.Combine(folder, $"managed-{invocation}-{Environment.ProcessId}.jsonl");
            Emit("CLR_STARTED", null);
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                try { Emit("UNHANDLED_EXCEPTION", args.ExceptionObject is Exception error ? Errors(error) : null); }
                catch { Fail(); }
            };
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Emit("MANAGED_PROCESS_EXIT", new { nativeEnvironmentExit = Environment.ExitCode });
            AppDomain.CurrentDomain.AssemblyLoad += (_, args) => AssemblyObserved(args.LoadedAssembly);
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) AssemblyObserved(assembly);
            Subscriptions.Add(DiagnosticListener.AllListeners.Subscribe(new ListenerObserver()));
        }
        catch { Fail(); }
    }

    private static void AssemblyObserved(Assembly assembly)
    {
        try
        {
            var name = assembly.GetName().Name;
            if (name is "testhost" or "Microsoft.TestPlatform.TestHost") Emit("TEST_HOST_ASSEMBLY_OBSERVED", null);
            else if (name == "Infrastructure.IntegrationTests") Emit("INFRASTRUCTURE_TEST_ASSEMBLY_OBSERVED", null);
        }
        catch { Fail(); }
    }

    private static object[] Errors(Exception error)
    {
        var result = new List<object>();
        for (Exception? current = error; current is not null && result.Count < 8; current = current.InnerException)
        {
            var type = current.GetType().FullName ?? "";
            var category = Categories.Contains(type) ? type : "OTHER_EXCEPTION";
            string? sqlState = null;
            if (type == "Npgsql.PostgresException")
            {
                var value = current.GetType().GetProperty("SqlState")?.GetValue(current) as string;
                if (value is { Length: 5 } && value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'Z')) sqlState = value;
            }
            result.Add(new { category, sqlState, hresult = current.HResult });
        }
        return result.ToArray();
    }

    private static void Emit(string phase, object? observation)
    {
        try
        {
            lock (Sync)
            {
                if (destination is null) { Fail(); return; }
                var record = new { schema = 1, sourceSha = "e87f5e125566e3a03874aef601feb0da82861c06",
                    invocation, pid = Environment.ProcessId, managedThread = Environment.CurrentManagedThreadId,
                    observedAtUtc = DateTimeOffset.UtcNow, phase, observation };
                File.AppendAllText(destination, JsonSerializer.Serialize(record) + "\n");
            }
        }
        catch { Fail(); }
    }

    private static void Fail()
    {
        if (Interlocked.Exchange(ref failed, 1) != 0) return;
        try { Console.Error.WriteLine("ZEKA_DIAG_MANAGED_OBSERVER_FAILED"); } catch { }
    }

    private sealed class ListenerObserver : IObserver<DiagnosticListener>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) => Fail();
        public void OnNext(DiagnosticListener listener)
        {
            try
            {
                if (listener.Name == "Microsoft.EntityFrameworkCore")
                    Subscriptions.Add(listener.Subscribe(new MigrationObserver(), name => name.EndsWith(".MigrationApplying", StringComparison.Ordinal)));
            }
            catch { Fail(); }
        }
    }

    private sealed class MigrationObserver : IObserver<KeyValuePair<string, object?>>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) => Fail();
        public void OnNext(KeyValuePair<string, object?> value)
        {
            try
            {
                // Read public migration metadata only; never query, parameters or context data.
                var migration = value.Value?.GetType().GetProperty("Migration")?.GetValue(value.Value);
                var attribute = migration?.GetType().CustomAttributes.FirstOrDefault(a =>
                    a.AttributeType.FullName == "Microsoft.EntityFrameworkCore.Migrations.MigrationAttribute");
                var id = attribute?.ConstructorArguments.FirstOrDefault().Value as string;
                var known = id is "20260902192150_OrganisationMembershipDomain" or "20260910165430_TenantPermissionCatalogue"
                    ? id : "OTHER_MIGRATION";
                Emit("MIGRATION_BOOTSTRAP_OBSERVED", new { knownMigration = known });
            }
            catch { Fail(); }
        }
    }
}
