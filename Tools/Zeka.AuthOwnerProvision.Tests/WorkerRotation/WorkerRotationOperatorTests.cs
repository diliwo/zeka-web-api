using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace Zeka.AuthOwnerProvision.Tests.WorkerRotation;

public sealed class WorkerRotationOperatorTests
{
    [Fact]
    public async Task ForwardPairOperatorRequiresBothDeliveriesBeforeRevokingOthers()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "Deployments", "local-stack")))
            root = root.Parent;
        Assert.NotNull(root);
        var script = Path.Combine(root!.FullName, "Tools", "Zeka.AuthOwnerProvision.Tests", "WorkerRotation", "Test-WorkerRotation.ps1");
        // The script scans public source before reading/executing it. Pure
        // DefinitionsOnly ports never touch secrets, Docker, SQL or Auth.
        var powershell = OperatingSystem.IsWindows() ? @"C:\Program Files\PowerShell\7\pwsh.exe" : "pwsh";
        var start = new ProcessStartInfo(powershell) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root.FullName };
        start.ArgumentList.Add("-NoLogo"); start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-File"); start.ArgumentList.Add(script);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException) { process.Kill(); throw new TimeoutException("PURE_OPERATOR_TEST_DEADLINE"); }
        var output = await stdout; _ = await stderr; // Never echo arbitrary captured output.
        Assert.True(process.ExitCode == 0, "Pure operator harness failed; inspect sanitized diagnostics.");
        using var json = JsonDocument.Parse(output.Trim());
        Assert.Equal(0, json.RootElement.GetProperty("failed").GetInt32());
        Assert.True(json.RootElement.GetProperty("passed").GetInt32() >= 35);
        Assert.Equal("NOT_RUN", json.RootElement.GetProperty("runtime").GetString());
        Assert.Equal("NOT_RUN", json.RootElement.GetProperty("provider").GetString());
        Assert.Equal("NOT_RUN", json.RootElement.GetProperty("adoption").GetString());
        Assert.Equal("FAIL_UNCLASSIFIED", json.RootElement.GetProperty("historicalObservability").GetProperty("original").GetString());
    }
}
