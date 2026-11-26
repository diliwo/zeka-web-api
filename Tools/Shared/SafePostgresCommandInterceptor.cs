using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
namespace Zeka.ToolingDiagnostics;

// EF adapter only. Read-only Npgsql owner probes compile-link the core producer without EF.
internal sealed class SafePostgresCommandInterceptor(SafePostgresDiagnosticCapture capture) : DbCommandInterceptor
{
    public override void CommandFailed(DbCommand command, CommandErrorEventData eventData) => capture.Capture(eventData.Exception);
    public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        capture.Capture(eventData.Exception);
        return Task.CompletedTask;
    }
}
