using System.Text.RegularExpressions;

namespace Zeka.DbMigrate;

internal static partial class CommandLine
{
    [GeneratedRegex("^[0-9]{14}_[A-Za-z0-9_ ]+$", RegexOptions.CultureInvariant)]
    private static partial Regex MigrationIdPattern();

    public static bool TryParse(string[] args, out CommandOptions? options)
    {
        options = null;
        if (args.Length != 14 || args[0] is not ("plan" or "apply")) return false;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var credentialStdin = false;
        for (var index = 1; index < args.Length; index++)
        {
            var key = args[index];
            if (key == "--credential-stdin")
            {
                if (credentialStdin) return false;
                credentialStdin = true;
                continue;
            }
            if (key is not ("--service" or "--target" or "--operation-id" or "--evidence-root"
                or "--evidence-file" or "--lock-timeout-seconds") || index + 1 >= args.Length
                || !values.TryAdd(key, args[++index])) return false;
        }
        if (!credentialStdin || values.Count != 6
            || !ServiceDescriptor.TryGet(values["--service"], out var service)
            || values["--target"] is not "latest" && !MigrationIdPattern().IsMatch(values["--target"])
            || !Guid.TryParseExact(values["--operation-id"], "D", out var operationId) || operationId == Guid.Empty
            || !int.TryParse(values["--lock-timeout-seconds"], out var seconds) || seconds is < 1 or > 60)
            return false;
        options = new CommandOptions(args[0] == "apply" ? MigrationOperation.Apply : MigrationOperation.Plan,
            service, values["--target"], operationId, values["--evidence-root"], values["--evidence-file"],
            TimeSpan.FromSeconds(seconds));
        return true;
    }
}
