using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Zeka.AuthOwnerProvision;

namespace Zeka.AuthOwnerProvision.Tests;

public sealed class OwnerProtocolTests
{
    private static readonly string Source = new('a', 40);
    private static readonly string Binary = new('b', 64);
    private static readonly string DatabaseSource = new('c', 40);

    [Fact]
    public void Explicit_owner_scope_is_admitted_without_database_or_credentials_access()
    {
        var input = Parse(Valid());
        Assert.Equal(input.SubjectId, OwnerProtocol.Admit(input, Binding()).Value.SubjectId);
        Assert.DoesNotContain(input.OwnerPassword, input.ToString());
        Assert.DoesNotContain(input.AuthRuntimeConnectionString, input.ToString());
    }

    [Theory]
    [InlineData("schema", "wrong")]
    [InlineData("scope", "C2")]
    [InlineData("project", "historical")]
    [InlineData("authorityKind", "VerifiedSubject")]
    [InlineData("ownerDecisionId", "wrong")]
    [InlineData("approvalCommentId", "wrong")]
    [InlineData("ownerAccount", "another-owner")]
    [InlineData("actualOwnerSid", "S-1-5-21-111-222-333-1002")]
    [InlineData("expectedOwnerSid", "")]
    [InlineData("actualOwnerProfile", "C:\\Users\\another")]
    [InlineData("normalizedEmail", "OTHER@EXAMPLE.ORG")]
    [InlineData("sourceCommit", "wrong")]
    [InlineData("binarySha256", "wrong")]
    [InlineData("databaseSourceCommit", "wrong")]
    [InlineData("databaseName", "postgres")]
    [InlineData("initializationId", "")]
    [InlineData("subjectId", "00000000-0000-0000-0000-000000000000")]
    public void Negative_header_fixture_is_rejected_without_side_effects(string field, string value)
    {
        var json = Valid(); json[field] = value;
        Assert.Throws<OwnerToolException>(() => OwnerProtocol.Admit(Parse(json), Binding()));
    }

    [Theory]
    [InlineData("Host=elsewhere;Database=zeka_m1_auth;Username=zeka_auth_runtime;Password=unit_fixture_not_live")]
    [InlineData("Host=postgres;Database=postgres;Username=zeka_auth_runtime;Password=unit_fixture_not_live")]
    [InlineData("Host=postgres;Database=zeka_m1_auth;Username=postgres;Password=unit_fixture_not_live")]
    [InlineData("Host=postgres;Database=zeka_m1_auth;Username=zeka_auth_migrator;Password=unit_fixture_not_live")]
    [InlineData("Host=postgres;Database=zeka_m1_auth;Username=zeka_auth_runtime;Password=unit_fixture_not_live;Options=-c role=postgres")]
    public void Foreign_target_or_privilege_fixture_is_rejected_without_connection(string connection)
    {
        var json = Valid(); json["authRuntimeConnectionString"] = connection;
        Assert.Throws<OwnerToolException>(() => OwnerProtocol.Admit(Parse(json), Binding()));
    }

    [Theory]
    [InlineData("agreements")]
    [InlineData("requiredAgreements")]
    [InlineData("emailVerified")]
    [InlineData("ownerAttested")]
    public void Undeclared_or_legal_agreement_input_is_not_a_C1_control(string member)
    {
        var json = Valid(); json[member] = new JsonArray();
        Assert.Throws<OwnerToolException>(() => Parse(json));
    }

    [Fact]
    public void Duplicate_members_and_oversized_input_are_rejected()
    {
        Assert.Throws<OwnerToolException>(() => OwnerProtocol.Parse(
            Encoding.UTF8.GetBytes("{\"schema\":\"one\",\"schema\":\"two\"}")));
        Assert.Throws<OwnerToolException>(() => OwnerProtocol.Parse(new byte[OwnerProtocol.MaximumInputBytes + 1]));
    }

    [Fact]
    public void Assembly_informational_version_and_hash_are_both_required()
    {
        var input = Parse(Valid());
        Assert.Throws<OwnerToolException>(() => OwnerProtocol.Admit(input, Binding() with { InformationalVersion = "unknown" }));
        Assert.Throws<OwnerToolException>(() => OwnerProtocol.Admit(input, Binding() with { BinarySha256 = new string('d', 64) }));
    }

    [Fact]
    public void Existing_database_marker_uses_initialization_source_not_new_binary_source()
    {
        var input = Parse(Valid());
        var role = JsonSerializer.Serialize(new { schema = "zeka-m1-role/v1",
            initializationId = input.InitializationId, service = "auth", kind = "runtime",
            sourceCommit = DatabaseSource });
        RuntimeAdmission.ValidateMarkers(input, role,
            $"zeka-m1-database/v1:{input.InitializationId}:auth:{DatabaseSource}");
        Assert.Throws<OwnerToolException>(() => RuntimeAdmission.ValidateMarkers(input,
            role.Replace(DatabaseSource, Source),
            $"zeka-m1-database/v1:{input.InitializationId}:auth:{DatabaseSource}"));
    }

    [Fact]
    public async Task Rejected_argv_receipt_never_echoes_secret_or_native_error()
    {
        using var input = new MemoryStream();
        using var output = new StringWriter();
        var exit = await Program.ExecuteAsync(["unit_fixture_private_value"], input, output);
        Assert.Equal(2, exit);
        Assert.DoesNotContain("unit_fixture_private_value", output.ToString());
        Assert.DoesNotContain("Exception", output.ToString());
        using var receipt = JsonDocument.Parse(output.ToString());
        Assert.Equal("ARGUMENTS_REJECTED", receipt.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Rejected_stdin_receipt_never_serializes_the_private_body()
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(Valid().ToJsonString()));
        using var output = new StringWriter();
        var exit = await Program.ExecuteAsync([], input, output);
        Assert.Equal(2, exit);
        Assert.DoesNotContain("unit_fixture_not_live", output.ToString());
        Assert.DoesNotContain("authRuntimeConnectionString", output.ToString());
        Assert.DoesNotContain("ownerPassword", output.ToString());
    }

    [Fact]
    public async Task Broken_receipt_channel_returns_fixed_exit_without_an_unhandled_exception()
    {
        using var input = new MemoryStream();
        using var output = new RejectedWriter();
        Assert.Equal(6, await Program.ExecuteAsync(["unit_fixture_private"], input, output));
    }

    private sealed class RejectedWriter : StringWriter
    {
        public override Task WriteLineAsync(string? value) =>
            Task.FromException(new IOException("unit_fixture_private_native_detail"));
    }
    private static AssemblyBinding Binding() => new(Source, "0.1.0+" + Source, Binary);
    private static OwnerInput Parse(JsonObject json) =>
        OwnerProtocol.Parse(Encoding.UTF8.GetBytes(json.ToJsonString()));
    private static JsonObject Valid() => new()
    {
        ["schema"] = OwnerProtocol.InputSchema, ["scope"] = "LocalSyntheticM1OwnerProvision",
        ["project"] = "zeka-m1", ["authorityKind"] = "OwnerAttestedLocalSynthetic",
        ["ownerDecisionId"] = OwnerProtocol.OwnerDecision, ["approvalCommentId"] = "6529021741",
        ["ownerAccount"] = OwnerProtocol.ApprovedOwner,
        ["expectedOwnerSid"] = "S-1-5-21-111-222-333-1001",
        ["actualOwnerSid"] = "S-1-5-21-111-222-333-1001",
        ["expectedOwnerProfile"] = OwnerProtocol.ApprovedProfile,
        ["actualOwnerProfile"] = OwnerProtocol.ApprovedProfile,
        ["normalizedEmail"] = OwnerProtocol.ApprovedEmail,
        ["sourceCommit"] = Source, ["binarySha256"] = Binary,
        ["databaseSourceCommit"] = DatabaseSource, ["databaseName"] = "zeka_m1_auth",
        ["initializationId"] = "11111111-2222-3333-4444-555555555555",
        ["subjectId"] = "11111111-2222-3333-4444-666666666666",
        ["displayName"] = "Local synthetic owner organisation", ["slug"] = null,
        ["idempotencyKey"] = "unit-fixture-owner-operation",
        ["processingTimeoutSeconds"] = 120, ["retentionSeconds"] = 86400,
        ["authRuntimeConnectionString"] = "Host=postgres;Port=5432;Database=zeka_m1_auth;Username=zeka_auth_runtime;Password=unit_fixture_not_live",
        ["ownerPassword"] = "unit_fixture_not_live"
    };
}
