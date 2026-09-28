using System.Runtime.InteropServices;
using System.Text.Json;
using Xunit;

namespace Zeka.DbMigrate.Tests;

[Trait("Issue", "46")]
[Trait("Evidence", "PlatformConformance")]
public sealed class EvidenceOutputDurabilityTests
{
    [Fact]
    public async Task Publication_synchronizes_containing_directory_after_atomic_rename()
    {
        Assert.True(OperatingSystem.IsLinux(), "The evidence publisher requires Linux filesystem primitives.");
        var root = NewRoot();
        var finalPath = Path.Combine(root, "result.json");
        var synchronizeCalls = 0;

        var output = EvidenceOutput.Create(root, "result.json", descriptor =>
        {
            synchronizeCalls++;
            Assert.Equal(root, new DirectoryInfo($"/proc/self/fd/{descriptor}").LinkTarget);
            Assert.True(File.Exists(finalPath));
            using var document = JsonDocument.Parse(File.ReadAllText(finalPath));
            Assert.Equal("test-operation", document.RootElement.GetProperty("operation").GetString());
            return 0;
        });

        await output.PublishAsync(Evidence());

        Assert.Equal(1, synchronizeCalls);
        Assert.True(File.Exists(finalPath));
        Assert.Empty(Directory.GetFiles(root, ".*.tmp"));
    }

    [Fact]
    public async Task Publication_fails_closed_when_containing_directory_synchronization_fails()
    {
        Assert.True(OperatingSystem.IsLinux(), "The evidence publisher requires Linux filesystem primitives.");
        var root = NewRoot();
        var finalPath = Path.Combine(root, "result.json");

        var output = EvidenceOutput.Create(root, "result.json", _ =>
        {
            Assert.True(File.Exists(finalPath));
            Marshal.SetLastPInvokeError(5);
            return -1;
        });

        await Assert.ThrowsAsync<EvidencePathException>(() => output.PublishAsync(Evidence()));

        Assert.True(File.Exists(finalPath));
        Assert.Empty(Directory.GetFiles(root, ".*.tmp"));
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "zeka-evidence-durability-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static MigrationEvidence Evidence()
    {
        var phases = new PhaseResults(new PhaseResult("not_started"), new PhaseResult("not_started"),
            new PhaseResult("not_started"), new PhaseResult("not_started"));
        var roles = new RoleAssertions("expected-session", null, "expected-current", null, false, false, false);
        return new MigrationEvidence("1", "test", "test-sha", Guid.NewGuid(), "test-operation", "auth",
            "AuthManagement", "latest", null, null, null, "TEST", "TEST", false, roles, phases,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, []);
    }
}
