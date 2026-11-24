using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.Mail;
using AuthManager.Application.Onboarding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using AuthManager.Infrastructure.Onboarding;
using AuthManager.Infrastructure.Persistence.Entities;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using Microsoft.AspNetCore.Builder;
using Xunit.Abstractions;

namespace Infrastructure.IntegrationTests;

public sealed class VerificationMailpitTests(ITestOutputHelper output)
{
    private const string Mailpit = "axllent/mailpit@sha256:e436eeb6cbcc14d94ff22a2695f170a6122409b5173aa98f8a6e1c8d94ace631";
    private const string Runner = "mcr.microsoft.com/dotnet/sdk@sha256:4cdea2a99ba7f4d8a48ebe250ec01db119d2102c141f85411a576be7bac5eb23";
    private const string WorkerMarker = "ZEKA_ZEK27_MAILPIT_WITNESS";
    private const string KeyLocator = "ZEKA_ZEK27_VERIFICATION_KEY_FILE";
    private const string ChildKey = "/tmp/zeka-verification-fixture-key";

    [Fact]
    public async Task Real_private_smtp_delivery_captures_two_messages_with_the_same_generation_token()
    {
        if (OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable(WorkerMarker) == "1")
        {
            await ExercisePrivateSinkAsync();
            return;
        }

        using var key = RegistrationVerificationKeyFixture.Create(Environment.GetEnvironmentVariable(KeyLocator));
        var keyFile = key.Path;

        var id = Guid.NewGuid().ToString("N");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(110));
        await using var network = new NetworkBuilder()
            .WithName("zeka-zek27-mailpit-" + id)
            .WithLabel("com.zeka.fixture", "zek27-mailpit")
            .WithCreateParameterModifier(spec => spec.Internal = true)
            .WithCleanUp(false)
            .Build();
        await network.CreateAsync(deadline.Token);
        try
        {
            await using var mailpit = new ContainerBuilder(Mailpit)
                .WithName("zeka-zek27-mailpit-" + id)
                .WithLabel("com.zeka.fixture", "zek27-mailpit")
                .WithNetwork(network).WithNetworkAliases("mailpit")
                .WithAutoRemove(true)
                .WithCleanUp(false)
                .Build();
            await mailpit.StartAsync(deadline.Token);
            try
            {
                await using var runner = new ContainerBuilder(Runner)
                    .WithName("zeka-zek27-mailpit-witness-" + id)
                    .WithLabel("com.zeka.fixture", "zek27-mailpit")
                    .WithNetwork(network)
                    .WithEnvironment(WorkerMarker, "1")
                    .WithEnvironment(KeyLocator, ChildKey)
                    .WithEnvironment("DOTNET_CLI_TELEMETRY_OPTOUT", "1")
                    .WithEnvironment("DOTNET_NOLOGO", "1")
                    .WithBindMount(AppContext.BaseDirectory, "/suite", AccessMode.ReadOnly)
                    .WithResourceMapping(FilePath.Of(keyFile), FilePath.Of(ChildKey), 0, 0, UnixFileModes.UserRead)
                    .WithCommand("dotnet", "vstest", "/suite/Infrastructure.IntegrationTests.dll",
                        "/TestCaseFilter:FullyQualifiedName~VerificationMailpitTests.Real_private_smtp")
                    .WithCleanUp(false)
                    .Build();
                await runner.StartAsync(deadline.Token);
                try
                {
                    var exit = await runner.GetExitCodeAsync(deadline.Token);
                    var (stdout, stderr) = await runner.GetLogsAsync(timestampsEnabled: false, ct: deadline.Token);
                    var lines = (stdout + "\n" + stderr).Split('\n', StringSplitOptions.TrimEntries);
                    foreach (var line in lines.Where(line => line.StartsWith("Passed!", StringComparison.Ordinal)
                                                         || line.StartsWith("ZEK27_", StringComparison.Ordinal)))
                        output.WriteLine(line);
                    if (exit != 0)
                    {
                        var diagnostic = lines.FirstOrDefault(line => line.StartsWith("ZEK27_", StringComparison.Ordinal));
                        throw new InvalidOperationException(diagnostic ?? "ZEK27_SAME_NETWORK_MAILPIT_WITNESS_FAILED");
                    }
                    var reportedOnePass = lines.Any(line => line.StartsWith("Passed!", StringComparison.Ordinal)
                        && System.Text.RegularExpressions.Regex.IsMatch(line,
                            @"Passed:\s+1,\s+Skipped:\s+0,\s+Total:\s+1(?:,|$)", System.Text.RegularExpressions.RegexOptions.None,
                            TimeSpan.FromMilliseconds(100)));
                    Assert.True(reportedOnePass, "The SMTP runner did not report exactly one passing test.");
                }
                finally
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    await runner.StopAsync(cleanup.Token);
                    await RemoveExitedRunnerAsync(runner.Id, cleanup.Token);
                }
            }
            finally { await mailpit.StopAsync(); }
        }
        finally { await network.DeleteAsync(); }
    }


    private static async Task RemoveExitedRunnerAsync(string id, CancellationToken cancellationToken)
    {
        if (id.Length != 64 || !id.All(Uri.IsHexDigit))
            throw new InvalidOperationException("ZEK27_RUNNER_IDENTITY_NOT_ADMITTED");
        var status = await RunDockerAsync(cancellationToken, "inspect", "--format",
            "{{.Id}}|{{.State.Status}}|{{index .Config.Labels \"com.zeka.fixture\"}}", id);
        if (status != id + "|exited|zek27-mailpit")
            throw new InvalidOperationException("ZEK27_RUNNER_CLEANUP_NOT_ADMITTED");
        _ = await RunDockerAsync(cancellationToken, "rm", "-v", id);
    }

    private static async Task<string> RunDockerAsync(CancellationToken cancellationToken, params string[] arguments)
    {
        var start = new ProcessStartInfo("docker")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("ZEK27_NATIVE_DOCKER_START_FAILED");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        _ = await stderr;
        if (process.ExitCode != 0)
            throw new InvalidOperationException("ZEK27_NATIVE_DOCKER_CLEANUP_FAILED");
        return (await stdout).Trim();
    }

    private static async Task ExercisePrivateSinkAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "LocalStack" });
        builder.Configuration["OnboardingRegistration:Enabled"] = "true";
        builder.Configuration["OnboardingRegistration:VerificationMailpitEnabled"] = "true";
        builder.Configuration["OnboardingRegistration:Deployment"] = "zek27-mailpit-fixture";
        builder.Configuration["OnboardingRegistration:ActiveKeyId"] = "fixture-v1";
        builder.Configuration["OnboardingRegistration:VerificationKeyFiles:fixture-v1"] = ChildKey;
        builder.Configuration["OnboardingRegistration:VerificationPagePath"] = "/onboarding/verify-email";
        builder.Configuration["BrowserSession:Origin"] = "https://app.example.invalid";
        using var options = RegistrationOptions.Read(builder.Configuration, builder.Environment);
        var tokens = new VerificationTokenService(options);
        var now = DateTimeOffset.UtcNow;
        var challenge = new EmailVerificationChallenge
        {
            Id = Guid.NewGuid(), UserId = Guid.NewGuid(), Generation = 1, KeyId = options.ActiveKeyId,
            AddressBinding = VerificationTokenService.AddressBinding("FICTIONAL@EXAMPLE.INVALID"),
            StampBinding = VerificationTokenService.StampBinding(Guid.NewGuid().ToString("N")),
            CreatedAtUtc = now, ExpiresAtUtc = now.AddHours(24), TokenHash = string.Empty
        };
        var firstToken = tokens.Create(challenge)!;
        challenge.TokenHash = VerificationTokenService.TokenHash(firstToken);
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false })
        {
            BaseAddress = new Uri("http://mailpit:8025"),
            Timeout = TimeSpan.FromSeconds(2)
        };
        await MailpitReadyAsync(http);
        var sender = new LocalStackMailpitDelivery(options);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(16));
        await sender.SendAsync("fictional@example.invalid", firstToken, deadline.Token);
        var first = await ReadLatestAsync(http, deadline.Token);
        var replay = tokens.Create(challenge)!;
        await sender.SendAsync("fictional@example.invalid", replay, deadline.Token);
        var second = await ReadLatestAsync(http, deadline.Token);

        Assert.True(first.Id is not null && second.Id is not null && first.Id != second.Id);
        Assert.True(firstToken == replay);
        var expectedLink = "https://app.example.invalid/onboarding/verify-email#token=" + firstToken;
        Assert.True(first.Text?.Contains(expectedLink, StringComparison.Ordinal) == true);
        Assert.True(first.Text == second.Text);
        Assert.True(tokens.Matches(challenge, replay));
    }

    private static async Task MailpitReadyAsync(HttpClient client)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        while (!deadline.IsCancellationRequested)
        {
            try
            {
                using var check = await client.GetAsync("/api/v1/messages", deadline.Token);
                if (check.IsSuccessStatusCode) return;
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException) { }
            try { await Task.Delay(TimeSpan.FromMilliseconds(300), deadline.Token); }
            catch (OperationCanceledException) { break; }
        }
        throw new InvalidOperationException("ZEK27_MAILPIT_INTERNAL_API_NOT_READY");
    }

    private static async Task<(string? Id, string? Text)> ReadLatestAsync(HttpClient client, CancellationToken ct)
    {
        using var response = await client.GetFromJsonAsync<JsonDocument>("/api/v1/message/latest", ct);
        return (response!.RootElement.GetProperty("ID").GetString(), response.RootElement.GetProperty("Text").GetString());
    }
}

internal sealed class Zek27OwnerMail : IAsyncDisposable
{
    private const string VerificationLink = "https://app.example.invalid/onboarding/verify-email#token=";
    private readonly HttpClient client = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
    {
        BaseAddress = new Uri("http://127.0.0.1:18025"), Timeout = TimeSpan.FromSeconds(3)
    };
    private readonly string recipient;
    private bool admitted;

    private Zek27OwnerMail(string recipient) => this.recipient = recipient;

    public static async Task<Zek27OwnerMail> OpenScopedAsync(string recipient)
    {
        var parts = recipient.Split('@');
        if (parts.Length != 2 || parts[1] != "example.invalid" || !parts[0].StartsWith("fictional-", StringComparison.Ordinal)
            || !Guid.TryParseExact(parts[0]["fictional-".Length..], "N", out _))
            throw new InvalidOperationException("ZEK27_MAIL_RECIPIENT_NOT_ADMITTED");

        var mailbox = new Zek27OwnerMail(recipient);
        try
        {
            if ((await mailbox.FindAsync(CancellationToken.None)).Length != 0)
                throw new InvalidOperationException("ZEK27_MAIL_RECIPIENT_NOT_EMPTY");
            mailbox.admitted = true;
            return mailbox;
        }
        catch
        {
            mailbox.client.Dispose();
            throw;
        }
    }

    public async Task<string> WaitForTokenAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!deadline.IsCancellationRequested)
        {
            var ids = await FindAsync(deadline.Token);
            if (ids.Length > 1)
                throw new InvalidOperationException("ZEK27_MAIL_DELIVERY_AMBIGUOUS");
            if (ids.Length == 1)
            {
                using var response = await client.GetAsync("/api/v1/message/" + Uri.EscapeDataString(ids[0]), deadline.Token);
                if (response.StatusCode != System.Net.HttpStatusCode.OK)
                    throw new InvalidOperationException("ZEK27_MAIL_FETCH_FAILED");
                using var message = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: deadline.Token)
                    ?? throw new InvalidOperationException("ZEK27_MAIL_FETCH_INVALID");
                var item = message.RootElement;
                if (!MatchesRecipient(item) || item.GetProperty("Subject").GetString() != "Verify your Zeka email address")
                    throw new InvalidOperationException("ZEK27_MAIL_MESSAGE_NOT_CORRELATED");

                const string prefix = "Open this link to verify your email address: " + VerificationLink;
                var body = item.GetProperty("Text").GetString()?.Trim();
                if (body is null || !body.StartsWith(prefix, StringComparison.Ordinal))
                    throw new InvalidOperationException("ZEK27_MAIL_VERIFICATION_LINK_INVALID");
                var token = body[prefix.Length..];
                if (!VerificationTokenService.TryRead(token, out _))
                    throw new InvalidOperationException("ZEK27_MAIL_VERIFICATION_TOKEN_INVALID");
                return token;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(200), deadline.Token);
        }
        throw new InvalidOperationException("ZEK27_MAIL_DELIVERY_DEADLINE");
    }

    private async Task<string[]> FindAsync(CancellationToken cancellationToken)
    {
        var search = Uri.EscapeDataString($"to:\"{recipient}\"");
        using var response = await client.GetAsync("/api/v1/search?query=" + search + "&limit=10", cancellationToken);
        if (response.StatusCode != System.Net.HttpStatusCode.OK)
            throw new InvalidOperationException("ZEK27_MAIL_SCOPED_SEARCH_FAILED");
        using var document = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("ZEK27_MAIL_SCOPED_SEARCH_INVALID");
        var result = document.RootElement.GetProperty("messages");
        if (result.ValueKind == JsonValueKind.Null && document.RootElement.GetProperty("count").GetInt32() == 0)
            return [];
        var ids = new List<string>();
        foreach (var message in result.EnumerateArray())
        {
            if (!MatchesRecipient(message))
                throw new InvalidOperationException("ZEK27_MAIL_SCOPED_SEARCH_RETURNED_FOREIGN_RECIPIENT");
            var id = message.GetProperty("ID").GetString();
            if (string.IsNullOrEmpty(id) || id.Length > 128 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
                throw new InvalidOperationException("ZEK27_MAIL_ID_INVALID");
            ids.Add(id);
        }
        if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Count)
            throw new InvalidOperationException("ZEK27_MAIL_IDS_AMBIGUOUS");
        return ids.ToArray();
    }

    private bool MatchesRecipient(JsonElement message)
    {
        var to = message.GetProperty("To");
        return to.GetArrayLength() == 1 && string.Equals(to[0].GetProperty("Address").GetString(), recipient, StringComparison.Ordinal);
    }

    public async ValueTask DisposeAsync()
    {
        using (client)
        {
            if (!admitted) return;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var ids = await FindAsync(deadline.Token);
            if (ids.Length == 0) return;
            using var delete = new HttpRequestMessage(HttpMethod.Delete, "/api/v1/messages");
            delete.Content = JsonContent.Create(new { IDs = ids }, options: new JsonSerializerOptions());
            using var response = await client.SendAsync(delete, deadline.Token);
            if (!response.IsSuccessStatusCode || (await FindAsync(deadline.Token)).Length != 0)
                throw new InvalidOperationException("ZEK27_MAIL_SCOPED_CLEANUP_FAILED");
        }
    }
}

[Collection(RegistrationPostgreSqlCollection.Name)]
public sealed class VerificationMailDispatchPostgreSqlTests(RegistrationPostgreSqlFixture fixture)
{
    [Fact]
    public async Task Unknown_transport_outcome_retries_the_same_token_without_touching_the_shared_outbox()
    {
        await using var host = await fixture.PrepareAsync();
        var now = host.Clock.GetUtcNow();
        await using (var db = host.Read())
        {
            var provisioning = OutboxMessage.Create("organisation-provisioned", 1, "{\"probe\":\"provisioning\"}",
                now, now, "fixture-provision", null);
            var lifecycle = OutboxMessage.Create("lifecycle-closure", 1, "{\"probe\":\"lifecycle\"}",
                now, now, "fixture-life", null);
            lifecycle.AcquireLease(Guid.NewGuid(), now.AddMinutes(1));
            var unknown = OutboxMessage.Create("unknown", 77, "{\"probe\":\"unknown\"}",
                now, now, "fixture-unknown", null);
            unknown.MarkFailed(now, now.AddMinutes(2), "FIXTURE_STATE", 1);
            db.OutboxMessages.AddRange(provisioning, lifecycle, unknown);
            await db.SaveChangesAsync();
        }

        var before = await SharedSnapshotAsync(host);
        await host.Run(service => service.RegisterAsync(Input(), CancellationToken.None));
        var transport = new TrackingDelivery(failFirst: true);
        await DispatchAsync(host, transport);
        await using (var db = host.Read())
        {
            var pending = await db.VerificationMailOutboxMessages.SingleAsync();
            Assert.Equal(VerificationMailState.RetryScheduled, pending.Status);
            Assert.Null(pending.DeliveredAtUtc);
            Assert.Equal("SMTP_OUTCOME_UNCONFIRMED", pending.Outcome);
        }

        host.Clock.Advance(TimeSpan.FromSeconds(11));
        await DispatchAsync(host, transport);
        await using (var db = host.Read())
        {
            var delivered = await db.VerificationMailOutboxMessages.SingleAsync();
            Assert.Equal(VerificationMailState.Delivered, delivered.Status);
            Assert.Equal(2, delivered.Attempts);
            Assert.NotNull(delivered.DeliveredAtUtc);
            Assert.Equal(now.AddHours(24), (await db.EmailVerificationChallenges.SingleAsync()).ExpiresAtUtc);
        }
        Assert.Equal(2, transport.Tokens.Count);
        Assert.True(transport.Tokens[0] == transport.Tokens[1]);
        Assert.True(before == await SharedSnapshotAsync(host));
    }

    [Fact]
    public async Task SMTP_acceptance_during_resend_stays_delivered_and_its_old_challenge_is_invalid()
    {
        await using var host = await fixture.PrepareAsync();
        var input = Input();
        await host.Run(service => service.RegisterAsync(input, CancellationToken.None));
        var initial = await host.CurrentToken();
        host.Clock.Advance(TimeSpan.FromSeconds(61));

        var transport = new TrackingDelivery();
        transport.BeforeAcceptance = async () =>
        {
            Assert.Equal(RegistrationOutcome.Accepted,
                await host.Run(service => service.ResendAsync(input.Email!, CancellationToken.None)));
        };
        await DispatchAsync(host, transport);
        await using var db = host.Read();
        var sent = await db.VerificationMailOutboxMessages.SingleAsync(x => x.Status == VerificationMailState.Delivered);
        Assert.True(sent.ObsoleteAtAcceptance);
        Assert.True(initial == Assert.Single(transport.Tokens));
        Assert.Equal(RegistrationOutcome.Invalid,
            (await host.Run(service => service.ExchangeAsync(initial, CancellationToken.None))).Outcome);
    }

    private static RegistrationRequest Input() => new()
    {
        Email = $"fictional-{Guid.NewGuid():N}@example.invalid", FirstName = "Ada", LastName = "Witness",
        Password = $"Aa7!{Guid.NewGuid():N}"
    };

    private static async Task<string> SharedSnapshotAsync(RegistrationPostgreSqlFixture.RegistrationFixtureHost host)
    {
        await using var db = host.Read();
        return JsonSerializer.Serialize(await db.OutboxMessages.AsNoTracking().OrderBy(row => row.Id).ToListAsync());
    }

    private static async Task DispatchAsync(RegistrationPostgreSqlFixture.RegistrationFixtureHost host, TrackingDelivery delivery)
    {
        await using var db = host.Read();
        var dispatcher = new VerificationMailDispatcher(db, host.Services.GetRequiredService<VerificationTokenService>(),
            delivery, host.Clock);
        Assert.True(await dispatcher.DispatchOneAsync(CancellationToken.None));
    }

    private sealed class TrackingDelivery(bool failFirst = false) : IVerificationMailDelivery
    {
        public List<string> Tokens { get; } = [];
        public Func<Task>? BeforeAcceptance { get; set; }

        public async Task SendAsync(string recipient, string token, CancellationToken cancellationToken)
        {
            Tokens.Add(token);
            if (BeforeAcceptance is not null) await BeforeAcceptance();
            if (failFirst && Tokens.Count == 1) throw new SmtpException("Synthetic unknown delivery outcome");
        }
    }
}
