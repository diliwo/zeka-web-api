using System.Data.Common;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using AuthManager.API;
using AuthManager.API.Endpoints;
using AuthManager.Application.Onboarding;
using AuthManager.Application.Authentication;
using AuthManager.Infrastructure;
using AuthManager.Infrastructure.Authentication;
using AuthManager.Infrastructure.Identity;
using AuthManager.Infrastructure.Identity.Models;
using AuthManager.Infrastructure.Onboarding;
using AuthManager.Infrastructure.Persistence;
using AuthManager.Infrastructure.Persistence.Entities;
using AuthManager.Core.Enums;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;
using Zeka.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Infrastructure.IntegrationTests;

[Collection(RegistrationPostgreSqlCollection.Name)]
public sealed class RegistrationTransportTests(RegistrationPostgreSqlFixture fixture, ITestOutputHelper output)
{
    private const string Origin = "https://app.example.invalid";
    private const string SessionPath = "/api/v1/browser-session";
    private const string RegisterPath = SessionPath + "/onboarding/registrations";

    [Fact]
    public async Task Untrusted_forwarding_bad_origin_and_bad_antiforgery_never_enter_persistence()
    {
        await using var database = await fixture.PrepareAsync();
        await using var host = await Transport.StartAsync(fixture.CreateBuilder(database.Runtime, database.Clock));
        using var first = host.NewBrowser();
        using var second = host.NewBrowser();
        var csrf = await CsrfAsync(first);
        _ = await CsrfAsync(second);
        var input = Input();

        using (var denied = await SendAsync(first, RegisterPath, JsonContent.Create(input), null, Origin))
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using (var denied = await SendAsync(second, RegisterPath, JsonContent.Create(input), csrf, Origin))
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using (var denied = await SendAsync(first, RegisterPath, JsonContent.Create(input), csrf, null))
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using (var denied = await SendAsync(first, RegisterPath, JsonContent.Create(input), csrf, "https://other.example.invalid"))
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using (var denied = await SendAsync(first, RegisterPath, JsonContent.Create(input), csrf, Origin, multipleOrigins: true))
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using (var denied = await SendAsync(first, RegisterPath, new FormUrlEncodedContent([new("email", input.Email!)]), csrf, Origin))
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        using var withoutCookie = host.NewBrowser(cookies: false);
        using (var denied = await SendAsync(withoutCookie, RegisterPath, JsonContent.Create(input), csrf, Origin))
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var direct = host.NewBrowser(direct: true);
        using (var denied = await SendAsync(direct, RegisterPath, JsonContent.Create(input), csrf, Origin, spoof: true))
            Assert.Equal(HttpStatusCode.ServiceUnavailable, denied.StatusCode);
        using (var gone = await SendAsync(direct, "/auth/register", JsonContent.Create(input), null, null))
        {
            Assert.Equal(HttpStatusCode.Gone, gone.StatusCode);
            Assert.True(gone.Headers.CacheControl?.NoStore == true);
            Assert.Null(gone.Headers.Location);
        }
        using var cleartextLegacy = host.NewCleartextLegacyProbe();
        using (var gone = await SendAsync(cleartextLegacy, "/auth/register", JsonContent.Create(new { probe = true }), null, null))
        {
            Assert.Equal(HttpStatusCode.Gone, gone.StatusCode);
            Assert.True(gone.Headers.CacheControl?.NoStore == true);
            Assert.Null(gone.Headers.Location);
        }
        await using var db = database.Read();
        Assert.Equal(0, await db.Users.CountAsync());
        Assert.Equal(0, await db.VerificationMailOutboxMessages.CountAsync());
        Assert.Equal(0, await db.OnboardingGrants.CountAsync());
    }

    [Fact]
    public async Task Proxied_registration_and_exchange_emit_only_a_verified_scoped_cookie()
    {
        await using var database = await fixture.PrepareAsync();
        var builder = fixture.CreateBuilder(database.Runtime, database.Clock);
        var sample = new RegistrationTiming();
        builder.Services.AddScoped<RegistrationService>();
        builder.Services.AddScoped<IRegistrationService>(services =>
            new MeasuredRegistration(services.GetRequiredService<RegistrationService>(), sample));
        var started = Stopwatch.GetTimestamp();
        await using var host = await Transport.StartAsync(builder);
        await host.AwaitReadinessAsync();
        var readiness = Stopwatch.GetElapsedTime(started);
        using var browser = host.NewBrowser();
        var csrf = await CsrfAsync(browser);
        var input = Input();

        string accepted;
        var posted = Stopwatch.GetTimestamp();
        using (var registration = await SendAsync(browser, RegisterPath, JsonContent.Create(input), csrf, Origin))
        {
            var total = Stopwatch.GetElapsedTime(posted);
            var work = sample.FirstWork ?? throw new InvalidOperationException("REGISTRATION_WORK_TIMING_MISSING");
            output.WriteLine(FormattableString.Invariant(
                $"REGISTRATION_TIMING readiness_ms={readiness.TotalMilliseconds:F3} work_ms={work.TotalMilliseconds:F3} response_ms={total.TotalMilliseconds:F3}"));
            Assert.Equal(HttpStatusCode.Accepted, registration.StatusCode);
            Assert.True(registration.Headers.CacheControl?.NoStore == true);
            Assert.False(registration.Headers.Contains("Set-Cookie"));
            Assert.Null(registration.Headers.Location);
            accepted = await registration.Content.ReadAsStringAsync();
            using var json = JsonDocument.Parse(accepted);
            Assert.Equal("If the address can be registered, verification instructions will be sent.",
                json.RootElement.GetProperty("message").GetString());
        }
        using (var duplicate = await SendAsync(browser, RegisterPath, JsonContent.Create(input), csrf, Origin))
        {
            Assert.Equal(HttpStatusCode.Accepted, duplicate.StatusCode);
            Assert.Equal(accepted, await duplicate.Content.ReadAsStringAsync());
        }

        var token = await database.CurrentToken();
        const string exchangePath = RegisterPath + "/email-verification/exchange";
        string issuedCookie;
        using (var exchange = await SendAsync(browser, exchangePath, JsonContent.Create(new { token }), csrf, Origin))
        {
            Assert.Equal(HttpStatusCode.NoContent, exchange.StatusCode);
            Assert.True(exchange.Headers.TryGetValues("Set-Cookie", out var headers));
            var cookie = Assert.Single(headers!);
            issuedCookie = cookie.Split(';')[0];
            Assert.True(cookie.StartsWith("__Secure-ZekaOnboarding=", StringComparison.Ordinal)
                && cookie.Contains("httponly", StringComparison.OrdinalIgnoreCase)
                && cookie.Contains("; secure", StringComparison.OrdinalIgnoreCase)
                && cookie.Contains("samesite=strict", StringComparison.OrdinalIgnoreCase)
                && cookie.Contains("path=/api/v1/browser-session/onboarding;", StringComparison.OrdinalIgnoreCase)
                && !cookie.Contains("domain=", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(string.Empty, await exchange.Content.ReadAsStringAsync());
        }
        using (var replay = await SendAsync(browser, exchangePath, JsonContent.Create(new { token }), csrf, Origin))
        {
            Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
            Assert.False(replay.Headers.Contains("Set-Cookie"));
        }
        using (var restore = await SendAsync(browser, SessionPath + "/restore", JsonContent.Create(new { }), csrf, Origin,
            onboardingCookie: issuedCookie))
            Assert.Equal(HttpStatusCode.Unauthorized, restore.StatusCode);
        using (var signIn = await SendAsync(browser, SessionPath + "/sign-in", JsonContent.Create(new { }), csrf, Origin,
            onboardingCookie: issuedCookie))
            Assert.Equal(HttpStatusCode.Unauthorized, signIn.StatusCode);
        await using var db = database.Read();
        Assert.Equal(1, await db.OnboardingGrants.CountAsync());
        Assert.Equal(0, await db.BrowserSessionFamilies.CountAsync());
        Assert.Equal(0, await db.OrganisationMemberships.CountAsync());
    }


    [Fact]
    public async Task Public_registration_envelopes_and_hash_work_match_across_account_states()
    {
        await using var database = await fixture.PrepareAsync();
        var builder = fixture.CreateBuilder(database.Runtime, database.Clock);
        builder.Services.AddSingleton<CountingHasher>();
        builder.Services.AddSingleton<IPasswordHasher<User>>(services => services.GetRequiredService<CountingHasher>());
        await using var host = await Transport.StartAsync(builder);
        await host.AwaitReadinessAsync();
        using var browser = host.NewBrowser();
        var csrf = await CsrfAsync(browser);
        var cases = new UserStatus?[] { null, UserStatus.PendingVerification, UserStatus.Active, UserStatus.Suspended, UserStatus.Deleted };
        string? expected = null;
        for (var index = 0; index < 3; index++)
        {
            database.Clock.Advance(TimeSpan.FromMinutes(10));
            foreach (var status in cases)
            {
                var input = Input();
                var priorHash = status is { } known ? await SeedAsync(database, input, known) : null;
                var count = host.HashCount;
                var start = Stopwatch.GetTimestamp();
                using var response = await SendAsync(browser, RegisterPath, JsonContent.Create(input), csrf, Origin);
                var elapsed = Stopwatch.GetElapsedTime(start);
                Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
                Assert.True(response.Headers.CacheControl?.NoStore == true);
                Assert.False(response.Headers.Contains("Set-Cookie"));
                Assert.Null(response.Headers.Location);
                var body = await response.Content.ReadAsStringAsync();
                expected ??= body;
                Assert.Equal(expected, body);
                Assert.Equal(count + 1, host.HashCount);
                if (status is not null)
                {
                    await using var read = database.Read();
                    var existing = await read.Users.AsNoTracking().SingleAsync(user => user.Email == input.Email);
                    Assert.True(existing.PasswordHash == priorHash && existing.Status == status
                        && existing.FirstName == "Existing" && existing.LastName == "Fixture");
                }
                output.WriteLine($"STATE={status?.ToString() ?? "Absent"} ELAPSED_MS={elapsed.TotalMilliseconds:F0}");
            }
        }
    }

    [Fact]
    public async Task Registration_remains_closed_while_readiness_is_held_or_failing_and_recovers()
    {
        await using var database = await fixture.PrepareAsync();
        var known = Input();
        var absent = Input();
        await SeedAsync(database, known, UserStatus.Active);
        var gate = new InitializationBarrier();
        await using var host = await Transport.StartAsync(fixture.CreateBuilder(database.Runtime, database.Clock, gate));
        await gate.FirstAttempt.Task.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(HttpStatusCode.OK, await host.HealthAsync("/health/live"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, await host.HealthAsync("/health/ready"));
        using var browser = host.NewBrowser();
        var csrf = await CsrfAsync(browser);

        using (var first = await SendAsync(browser, RegisterPath, JsonContent.Create(known), csrf, Origin))
        using (var second = await SendAsync(browser, RegisterPath, JsonContent.Create(absent), csrf, Origin))
            await AssertSameUnavailableAsync(first, second);

        gate.RejectFirst.SetResult();
        await gate.RecoveryAttempt.Task.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, await host.HealthAsync("/health/ready"));
        gate.AllowRecovery.SetResult();
        await host.AwaitReadinessAsync();

        using var response = await SendAsync(browser, RegisterPath, JsonContent.Create(absent), csrf, Origin);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await using var read = database.Read();
        Assert.Equal(2, await read.Users.CountAsync());
        Assert.Equal(1, await read.OnboardingOperationReceipts.CountAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Missing_PostgreSQL_database_or_schema_leaves_liveness_up_and_registration_closed(bool missingDatabase)
    {
        await using var database = await fixture.PrepareAsync();
        var runtime = missingDatabase
            ? new NpgsqlConnectionStringBuilder(database.Runtime) { Database = $"absent_{Guid.NewGuid():N}" }.ConnectionString
            : await fixture.CreateEmptyDatabaseRuntimeAsync();
        var refusal = new DependencyFailure(missingDatabase ? "3D000" : "42P01");
        await using var host = await Transport.StartAsync(
            fixture.CreateBuilder(runtime, database.Clock, new FailedConnection(refusal), new FailedCommand(refusal)));
        await refusal.Observed.Task.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(HttpStatusCode.OK, await host.HealthAsync("/health/live"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, await host.HealthAsync("/health/ready"));
        using var browser = host.NewBrowser();
        var csrf = await CsrfAsync(browser);
        using var response = await SendAsync(browser, RegisterPath, JsonContent.Create(Input()), csrf, Origin);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore == true);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Deadline_failure_is_uniform_and_canceled_exchange_does_not_issue_a_cookie()
    {
        await using var database = await fixture.PrepareAsync();
        var known = Input();
        var original = await SeedAsync(database, known, UserStatus.Active);
        var registerDeadline = new TransactionDeadline();
        var builder = fixture.CreateBuilder(database.Runtime, database.Clock, registerDeadline);
        builder.Services.AddSingleton<CountingHasher>();
        builder.Services.AddSingleton<IPasswordHasher<User>>(services => services.GetRequiredService<CountingHasher>());
        await using var host = await Transport.StartAsync(builder);
        await host.AwaitReadinessAsync();
        using var browser = host.NewBrowser();
        var csrf = await CsrfAsync(browser);
        var absent = Input();

        registerDeadline.HoldBeginning = true;
        using (var first = await SendAsync(browser, RegisterPath, JsonContent.Create(known), csrf, Origin))
        using (var second = await SendAsync(browser, RegisterPath, JsonContent.Create(absent), csrf, Origin))
            await AssertSameUnavailableAsync(first, second);
        Assert.Equal(2, host.HashCount);
        Assert.Equal(2, registerDeadline.BlockedBegins);
        await using (var read = database.Read())
        {
            var identity = await read.Users.AsNoTracking().SingleAsync();
            Assert.True(identity.PasswordHash == original && identity.Status == UserStatus.Active
                && identity.FirstName == "Existing" && identity.LastName == "Fixture");
            Assert.Equal(0, await read.EmailVerificationChallenges.CountAsync());
            Assert.Equal(0, await read.VerificationMailOutboxMessages.CountAsync());
            Assert.Equal(0, await read.OnboardingOperationReceipts.CountAsync());
        }

        registerDeadline.HoldBeginning = false;
        using (var register = await SendAsync(browser, RegisterPath, JsonContent.Create(absent), csrf, Origin))
            Assert.Equal(HttpStatusCode.Accepted, register.StatusCode);
        var token = await database.CurrentToken();
        const string exchange = RegisterPath + "/email-verification/exchange";
        registerDeadline.HoldCommitAcknowledgement = true;
        using (var response = await SendAsync(browser, exchange, JsonContent.Create(new { token }), csrf, Origin))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.False(response.Headers.Contains("Set-Cookie"));
        }
        Assert.Equal(1, registerDeadline.BlockedAcknowledgements);
        registerDeadline.HoldCommitAcknowledgement = false;
        using (var replay = await SendAsync(browser, exchange, JsonContent.Create(new { token }), csrf, Origin))
        {
            Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
            Assert.False(replay.Headers.Contains("Set-Cookie"));
        }
        await using var final = database.Read();
        Assert.Equal(1, await final.OnboardingGrants.CountAsync());
        Assert.Equal(2, await final.OnboardingOperationReceipts.CountAsync());
        Assert.Equal(0, await final.BrowserSessionFamilies.CountAsync());
    }

    private const string IssuerKeyFileEnvironment = "ZEKA_ZEK27_RELEASE_ISSUER_FILE";
    private const string IssuerSpkiEnvironment = "ZEKA_ZEK27_RELEASE_ISSUER_SPKI";
    private const string ReleaseResultsEnvironment = "ZEKA_ZEK27_RELEASE_EVIDENCE";
    private const string SampleEnvironment = "ZEKA_ZEK27_RELEASE_COLD_SAMPLE";
    private const string ReleaseAudience = "zeka-test-api";
    private const string ReleaseKeyId = "zek27-validation-20261117";
    private const string ExchangePath = RegisterPath + "/email-verification/exchange";
    private static readonly UserStatus?[] ReleaseStates = [null, UserStatus.PendingVerification, UserStatus.Active, UserStatus.Suspended, UserStatus.Deleted];
    private static readonly Queue<DateTimeOffset> AllReleasePosts = new();

    private static bool releaseCaseFailed;

    private static async Task RunApprovedCaseAsync(Func<Task> run)
    {
        if (releaseCaseFailed)
            throw new InvalidOperationException("ZEK27_PREVIOUS_CASE_STOPPED");

        try { await run(); }
        catch
        {
            releaseCaseFailed = true;
            throw;
        }
    }

    // These owner-native release witnesses are NOT_EXECUTED in default CI.
    // Build explicitly with -p:ZekaOperatorWitnesses=true, then select the exact
    // FullyQualifiedName via dotnet test --no-build --no-restore after admitting
    // the real owner Mailpit, issuer files, evidence destination and run budget.
#if ZEKA_OPERATOR_WITNESSES
    [Fact]
    public Task Registration_release_cold_and_warm_account_states_are_observable()
#else
    internal Task Registration_release_cold_and_warm_account_states_are_observable()
#endif
        => RunApprovedCaseAsync(ObserveColdAndWarmStatesAsync);

    private async Task ObserveColdAndWarmStatesAsync()
    {
        var child = Environment.GetEnvironmentVariable(SampleEnvironment);
        if (child is not null)
        {
            if (!int.TryParse(child, out var index) || index < 0 || index >= ReleaseStates.Length)
                throw new InvalidOperationException("ZEK27_INVALID_COLD_SAMPLE_INDEX");
            await ObserveReleaseSampleAsync(index);
            return;
        }

        var results = new List<ReleaseSample>();
        for (var index = 0; index < ReleaseStates.Length; index++)
        {
            await ReservePostsAsync(3, "cold-sample");
            try { results.Add(await RunColdProcessAsync(index)); }
            finally { ClosePostReservation(3); }
        }

        foreach (var sample in results)
        {
            Assert.Equal(results[0].Cold.Envelope, sample.Cold.Envelope);
            Assert.Equal(results[0].Cold.Envelope, sample.Warm.Envelope);
            Assert.Equal(results[0].Failed.Envelope, sample.Failed.Envelope);
        }
        Assert.Equal(ReleaseStates.Length, results.Select(r => r.ApplicationPid).Distinct().Count());
    }

    private async Task<ReleaseSample> RunColdProcessAsync(int index)
    {
        var evidence = RequiredPath(ReleaseResultsEnvironment, directory: true);
        var assembly = typeof(RegistrationTransportTests).Assembly.Location;
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true, WorkingDirectory = evidence
        };
        start.ArgumentList.Add("vstest");
        start.ArgumentList.Add(assembly);
        start.ArgumentList.Add("/TestCaseFilter:FullyQualifiedName=Infrastructure.IntegrationTests.RegistrationTransportTests.Registration_release_cold_and_warm_account_states_are_observable");
        start.ArgumentList.Add("/ResultsDirectory:" + evidence);
        start.ArgumentList.Add($"/Logger:trx;LogFileName=cold-sample-{index}.trx");
        start.Environment[SampleEnvironment] = index.ToString(System.Globalization.CultureInfo.InvariantCulture);

        using var deadline = ReleaseDeadline(TimeSpan.FromMinutes(2));
        using var process = Process.Start(start) ?? throw new InvalidOperationException("ZEK27_COLD_PROCESS_START_FAILED");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new InvalidOperationException("ZEK27_COLD_PROCESS_DEADLINE");
        }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(evidence, $"cold-sample-{index}.stdout.log"), await stdout);
            await File.WriteAllTextAsync(Path.Combine(evidence, $"cold-sample-{index}.stderr.log"), await stderr);
            output.WriteLine($"ZEK27_COLD_PROCESS index={index} runner_pid={process.Id} exit={process.ExitCode}");
        }

        if (process.ExitCode != 0) throw new InvalidOperationException("ZEK27_COLD_PROCESS_FAILED");
        var receipt = JsonSerializer.Deserialize<ReleaseSample>(await File.ReadAllTextAsync(
            Path.Combine(evidence, $"cold-sample-{index}.json")))
            ?? throw new InvalidOperationException("ZEK27_COLD_RECEIPT_MISSING");
        Assert.Equal(index, receipt.Index);
        Assert.True(receipt.ApplicationPid != Environment.ProcessId);
        output.WriteLine("ZEK27_COLD_RECEIPT " + JsonSerializer.Serialize(receipt));
        return receipt;
    }

    private async Task ObserveReleaseSampleAsync(int index)
    {
        output.WriteLine($"ZEK27_SAMPLE_START index={index}");
        var schemaStart = Stopwatch.GetTimestamp();
        await using var database = await fixture.PrepareAsync();
        var schemaMs = Stopwatch.GetElapsedTime(schemaStart).TotalMilliseconds;
        var state = ReleaseStates[index];
        var inputs = new[] { Input(), Input(), Input() };
        var previousHashes = new string?[inputs.Length];
        if (state is { } known)
        {
            for (var i = 0; i < inputs.Length; i++)
                previousHashes[i] = await SeedAsync(database, inputs[i], known);
        }

        var timing = new RegistrationTiming();
        var failure = new TransactionDeadline();
        var builder = CreateReleaseBuilder(database.Runtime, mail: false, interceptors: [failure]);
        builder.Services.AddSingleton<CountingHasher>();
        builder.Services.AddSingleton<IPasswordHasher<User>>(provider => provider.GetRequiredService<CountingHasher>());
        builder.Services.AddScoped<RegistrationService>();
        builder.Services.AddScoped<IRegistrationService>(provider =>
            new MeasuredRegistration(provider.GetRequiredService<RegistrationService>(), timing));

        var appStart = Stopwatch.GetTimestamp();
        await using var host = await Transport.StartAsync(builder, release: true);
        await AdmitIssuerAsync(host);
        await host.AwaitReadinessAsync();
        var readyMs = Stopwatch.GetElapsedTime(appStart).TotalMilliseconds;
        using var browser = host.NewBrowser();
        var csrf = await CsrfAsync(browser);
        var observations = new List<ReleaseRequest>(3);
        for (var i = 0; i < inputs.Length; i++)
        {
            failure.HoldBeginning = i == 2;
            var sentAt = Stopwatch.GetTimestamp();
            var hashes = host.HashCount;
            using var response = await SendAsync(browser, RegisterPath, JsonContent.Create(inputs[i]), csrf, Origin);
            var envelope = await ReadEnvelopeAsync(response);
            var responseMs = Stopwatch.GetElapsedTime(sentAt).TotalMilliseconds;
            var work = timing.LastWork ?? throw new InvalidOperationException("ZEK27_REGISTRATION_WORK_NOT_OBSERVED");
            if (i == 2)
            {
                Assert.True(failure.BlockedBegins == 1, "ZEK27_DEADLINE_INTERCEPTOR_NOT_ENTERED");
                output.WriteLine($"ZEK27_DEADLINE_INTERCEPTOR_ENTERED index={index} count={failure.BlockedBegins}");
            }

            Assert.Equal(i == 2 ? 503 : 202, envelope.Status);
            Assert.Equal(i == 2, envelope.BodyBytes == 0);
            Assert.True(envelope.NoStore && !envelope.LocationPresent && !envelope.CredentialPresent);
            Assert.Equal(1, host.HashCount - hashes);
            observations.Add(new(envelope, work.TotalMilliseconds, responseMs));
        }
        Assert.Equal(1, failure.BlockedBegins);

        await using var db = database.Read();
        for (var i = 0; i < inputs.Length; i++)
        {
            var email = inputs[i].Email;
            var identity = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Email == email);
            if (state is { } expected)
                Assert.True(identity is not null && identity.Status == expected && identity.PasswordHash == previousHashes[i]
                    && identity.FirstName == "Existing" && identity.LastName == "Fixture");
            else if (i == 2)
                Assert.Null(identity);
            else
                Assert.True(identity is { Status: UserStatus.PendingVerification });
        }
        Assert.Equal(state is null ? 2 : 0, await db.OnboardingOperationReceipts.CountAsync());
        Assert.Equal(0, await db.OnboardingGrants.CountAsync());

        var record = new ReleaseSample(index, Environment.ProcessId, state?.ToString() ?? "Absent", schemaMs,
            readyMs, observations[0], observations[1], observations[2]);
        var path = Path.Combine(RequiredPath(ReleaseResultsEnvironment, directory: true), $"cold-sample-{index}.json");
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        await JsonSerializer.SerializeAsync(file, record);
        output.WriteLine("ZEK27_SAMPLE " + JsonSerializer.Serialize(record));
    }

    // Owner-native release witness: explicit opt-in and real admitted inputs required.
#if ZEKA_OPERATOR_WITNESSES
    [Fact]
    public Task Registration_mail_verification_and_restricted_credential_denial_are_composed()
#else
    internal Task Registration_mail_verification_and_restricted_credential_denial_are_composed()
#endif
        => RunApprovedCaseAsync(VerifyComposedMailAndDenialAsync);

    private async Task VerifyComposedMailAndDenialAsync()
    {
        await using var database = await fixture.PrepareAsync();
        var input = Input();
        await using var mailbox = await Zek27OwnerMail.OpenScopedAsync(input.Email!);
        var builder = CreateReleaseBuilder(database.Runtime, mail: true);
        await using var host = await Transport.StartAsync(builder, release: true, tenantEndpoints: true);
        await AdmitIssuerAsync(host);
        await host.AwaitReadinessAsync();
        Assert.True(host.VerificationWorkerRegistered);
        using var browser = host.NewBrowser();
        var csrf = await CsrfAsync(browser);
        await ReservePostsAsync(3, "composed");
        try
        {
            using (var registration = await SendAsync(browser, RegisterPath, JsonContent.Create(input), csrf, Origin))
            {
                var response = await ReadEnvelopeAsync(registration);
                Assert.Equal(202, response.Status);
                Assert.True(response.NoStore && !response.LocationPresent && !response.CredentialPresent);
            }

            Guid userId;
            Guid challengeId;
            await using (var state = database.Read())
            {
                var user = await state.Users.AsNoTracking().SingleAsync();
                var challenge = await state.EmailVerificationChallenges.AsNoTracking().SingleAsync();
                var queued = await state.VerificationMailOutboxMessages.AsNoTracking().SingleAsync();
                var receipt = await state.OnboardingOperationReceipts.AsNoTracking().SingleAsync();
                Assert.True(user.Email == input.Email && user.Status == UserStatus.PendingVerification && !user.EmailConfirmed);
                Assert.True(challenge.UserId == user.Id && challenge.Generation == 1 && queued.ChallengeId == challenge.Id
                    && queued.OperationId == receipt.Id && receipt.UserId == user.Id && receipt.ChallengeId == challenge.Id
                    && receipt.OutboxId == queued.Id && receipt.Kind == OnboardingOperationKind.Register);
                userId = user.Id;
                challengeId = challenge.Id;
            }
            output.WriteLine("ZEK27_COMPOSED durable_registration=PASS hosted_worker_registered=PASS");

            var delivered = await mailbox.WaitForTokenAsync();
            Assert.True(VerificationTokenService.TryRead(delivered, out var message) && message.ChallengeId == challengeId);
            string cookiePair;
            string credential;
            using (var exchange = await SendAsync(browser, ExchangePath, JsonContent.Create(new { token = delivered }), csrf, Origin))
            {
                Assert.Equal(HttpStatusCode.NoContent, exchange.StatusCode);
                var cookies = exchange.Headers.TryGetValues("Set-Cookie", out var values) ? values.ToArray() : [];
                Assert.True(cookies.Length == 1, "Expected one restricted cookie.");
                var restricted = cookies[0];
                Assert.True(restricted.StartsWith("__Secure-ZekaOnboarding=", StringComparison.Ordinal)
                    && restricted.Contains("; secure", StringComparison.OrdinalIgnoreCase)
                    && restricted.Contains("; httponly", StringComparison.OrdinalIgnoreCase)
                    && restricted.Contains("; samesite=strict", StringComparison.OrdinalIgnoreCase)
                    && restricted.Contains("path=/api/v1/browser-session/onboarding", StringComparison.OrdinalIgnoreCase)
                    && !restricted.Contains("domain=", StringComparison.OrdinalIgnoreCase));
                cookiePair = restricted.Split(';')[0];
                credential = cookiePair[(cookiePair.IndexOf('=') + 1)..];
                Assert.True(credential.Length > 0 && VerificationTokenService.IsGrantCredential(credential));
            }

            using (var replay = await SendAsync(browser, ExchangePath, JsonContent.Create(new { token = delivered }), csrf, Origin))
            {
                Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
                Assert.False(replay.Headers.Contains("Set-Cookie"));
            }

            using var direct = host.NewBrowser(direct: true, cookies: false);
            var tenant = $"/api/v1/tenant-access/{Guid.NewGuid():D}";
            foreach (var route in new[] { tenant, $"{tenant}/memberships/{Guid.NewGuid():D}" })
            {
                foreach (var bearer in new[] { false, true })
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, route);
                    if (bearer) request.Headers.Authorization = new("Bearer", credential);
                    else Assert.True(request.Headers.TryAddWithoutValidation("Cookie", cookiePair));
                    using var denied = await direct.SendAsync(request);
                    var envelope = await ReadEnvelopeAsync(denied);
                    Assert.Equal(401, envelope.Status);
                    Assert.True(envelope.BodyBytes == 0 && envelope.NoStore && !envelope.CredentialPresent);
                    Assert.True(denied.Headers.WwwAuthenticate.Any(challenge => challenge.Scheme == "Bearer"));
                }
            }
            Assert.Equal(0, host.ProtectedRequests);

            await using var after = database.Read();
            var identity = await after.Users.AsNoTracking().SingleAsync();
            var grant = await after.OnboardingGrants.AsNoTracking().SingleAsync();
            var challengeAfter = await after.EmailVerificationChallenges.AsNoTracking().SingleAsync();
            Assert.True(identity.Id == userId && identity.EmailConfirmed && identity.Status == UserStatus.Active);
            Assert.True(grant.UserId == userId && grant.ChallengeId == challengeId
                && grant.Purpose == OnboardingGrant.OrganisationPurpose && grant.ConsumedAtUtc is null);
            Assert.Equal(VerificationChallengeState.Consumed, challengeAfter.Status);
            Assert.Equal(2, await after.OnboardingOperationReceipts.CountAsync());
            Assert.Equal(0, await after.OrganisationMemberships.CountAsync());
            Assert.Equal(0, await after.BrowserSessionFamilies.CountAsync());
            output.WriteLine("ZEK27_COMPOSED actual_mail=PASS exchange=204 replay=400 restricted_cookie=PASS tenant_cookie=401 tenant_bearer=401 handler_entries=0");
        }
        finally { ClosePostReservation(3); }
    }

    // Owner-native release witness: explicit opt-in and real admitted inputs required.
#if ZEKA_OPERATOR_WITNESSES
    [Fact]
    public Task Registration_and_issuer_readiness_compose_without_public_details()
#else
    internal Task Registration_and_issuer_readiness_compose_without_public_details()
#endif
        => RunApprovedCaseAsync(VerifyCombinedReadinessAsync);

    private async Task VerifyCombinedReadinessAsync()
    {
        await using var valid = await fixture.PrepareAsync();
        await using (var host = await Transport.StartAsync(CreateReleaseBuilder(valid.Runtime, mail: false), release: true))
        {
            using var deadline = ReleaseDeadline(TimeSpan.FromSeconds(12));
            while (!host.RegistrationIsReady) await Task.Delay(TimeSpan.FromMilliseconds(50), deadline.Token);
            await AssertHealthAsync(host, "/health/authentication", 503);
            await AssertHealthAsync(host, "/health/ready", 503);
            await AssertHealthAsync(host, "/health/live", 200);
            await AdmitIssuerAsync(host);
            await host.AwaitReadinessAsync();
            await AssertHealthAsync(host, "/health/ready", 200);
            await AssertHealthAsync(host, "/health/live", 200);
            using var untrusted = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
                { Timeout = TimeSpan.FromSeconds(5) };
            var tls = await Assert.ThrowsAsync<HttpRequestException>(async () =>
            {
                using var rejected = await untrusted.GetAsync(host.Authority + "/health/live");
            });
            Assert.Equal(HttpRequestError.SecureConnectionError, tls.HttpRequestError);
        }

        var emptyDatabase = await fixture.CreateEmptyDatabaseRuntimeAsync();
        await using (var host = await Transport.StartAsync(CreateReleaseBuilder(emptyDatabase, mail: false), release: true))
        {
            await AdmitIssuerAsync(host);
            Assert.False(host.RegistrationIsReady);
            await AssertHealthAsync(host, "/health/authentication", 200);
            await AssertHealthAsync(host, "/health/ready", 503);
            await AssertHealthAsync(host, "/health/live", 200);
        }
        output.WriteLine("ZEK27_READINESS issuer_only=503 registration_only=503 both=200 live=200 public_body_empty=PASS untrusted_root_rejected=PASS");
    }

    private async Task AdmitIssuerAsync(Transport host)
    {
        using var client = host.NewBrowser(direct: true, cookies: false);
        using var response = await client.GetAsync("/.well-known/jwks.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore == true);
        using var document = await response.Content.ReadFromJsonAsync<JsonDocument>()
            ?? throw new InvalidOperationException("ZEK27_PUBLIC_JWKS_INVALID");
        var keys = document.RootElement.GetProperty("keys");
        Assert.True(keys.GetArrayLength() == 1);
        var key = keys[0];
        Assert.True(key.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal)
            .SetEquals(["kid", "kty", "alg", "use", "n", "e"]));
        Assert.True(key.GetProperty("kid").GetString() == ReleaseKeyId && key.GetProperty("kty").GetString() == "RSA"
            && key.GetProperty("alg").GetString() == "RS256" && key.GetProperty("use").GetString() == "sig");
        using var rsa = RSA.Create();
        rsa.ImportParameters(new RSAParameters
        {
            Modulus = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlDecode(key.GetProperty("n").GetString()!),
            Exponent = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlDecode(key.GetProperty("e").GetString()!)
        });
        var sha = Convert.ToHexString(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo())).ToLowerInvariant();
        Assert.True(rsa.KeySize == 3072 && sha == Environment.GetEnvironmentVariable(IssuerSpkiEnvironment));
        using var deadline = ReleaseDeadline(TimeSpan.FromSeconds(5));
        while (true)
        {
            using var ready = await client.GetAsync("/health/authentication", deadline.Token);
            if (ready.StatusCode == HttpStatusCode.OK) break;
            Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
            await Task.Delay(TimeSpan.FromMilliseconds(50), deadline.Token);
        }
        output.WriteLine($"ZEK27_ISSUER authority={host.Authority} audience={ReleaseAudience} kid={ReleaseKeyId} spki_sha256={sha} jwks_public_only=PASS");
    }

    private static async Task AssertHealthAsync(Transport host, string path, int expected)
    {
        using var local = host.NewBrowser(direct: true, cookies: false);
        using var response = await local.GetAsync(path);
        Assert.Equal(expected, (int)response.StatusCode);
        Assert.True((await response.Content.ReadAsByteArrayAsync()).Length == 0, "Public health body must remain empty.");
    }

    private static WebApplicationBuilder CreateReleaseBuilder(string runtime, bool mail, params IInterceptor[] interceptors)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "LocalStack" });
        builder.Logging.ClearProviders();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = runtime,
            ["BrowserSession:Origin"] = Origin, ["BrowserSession:TrustedProxyPeers:0"] = "127.0.0.1",
            ["BrowserSession:Limits:PerAddress"] = "10", ["BrowserSession:Limits:Aggregate"] = "10",
            ["BrowserSession:Limits:MaxPartitions"] = "16", ["BrowserSession:Limits:WindowSeconds"] = "600",
            ["OnboardingRegistration:Enabled"] = "true",
            ["OnboardingRegistration:VerificationMailpitEnabled"] = "true",
            ["OnboardingRegistration:Deployment"] = "zek27-native-release",
            ["OnboardingRegistration:ActiveKeyId"] = "fixture-v1",
            ["OnboardingRegistration:VerificationKeyFiles:fixture-v1"] = RequiredPath("ZEKA_ZEK27_VERIFICATION_KEY_FILE"),
            ["OnboardingRegistration:VerificationPagePath"] = "/onboarding/verify-email"
        });
        builder.Services.Infrastructure(builder.Configuration);
        if (interceptors.Length != 0)
        {
            var registration = builder.Services.Single(service => service.ServiceType == typeof(DbContextOptions<AuthDbContext>));
            var createOptions = registration.ImplementationFactory
                ?? throw new InvalidOperationException("ZEK27_EFFECTIVE_DB_OPTIONS_FACTORY_MISSING");

            // Keep the runtime options and scoped registration, then add the fixture interceptor.
            builder.Services[builder.Services.IndexOf(registration)] = ServiceDescriptor.Describe(
                registration.ServiceType,
                services => new DbContextOptionsBuilder<AuthDbContext>((DbContextOptions<AuthDbContext>)createOptions(services))
                    .AddInterceptors(interceptors).Options,
                registration.Lifetime);
        }
        builder.ConfigureMicrosoftIdentity();
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        builder.Services.AddVerificationRegistration(builder.Configuration, builder.Environment);
        // Keep transport-only witnesses isolated from SMTP; the delivery witness
        // alone starts the real worker against its admitted private sink.
        if (!mail)
            builder.Services.Remove(builder.Services.Single(service => service.ServiceType == typeof(IHostedService)
                && service.ImplementationType == typeof(VerificationMailWorker)));
        return builder;
    }

    private async Task ReservePostsAsync(int count, string phase)
    {
        var begin = Stopwatch.GetTimestamp();
        using var deadline = ReleaseDeadline(TimeSpan.FromMinutes(11));
        while (true)
        {
            var now = DateTimeOffset.UtcNow;
            while (AllReleasePosts.TryPeek(out var prior) && now - prior >= TimeSpan.FromMinutes(10))
                AllReleasePosts.Dequeue();
            if (AllReleasePosts.Count + count <= 10)
            {
                output.WriteLine(FormattableString.Invariant(
                    $"ZEK27_QUOTA phase={phase} address=127.0.0.1 address_limit=10 aggregate_limit=10 window_seconds=600 active={AllReleasePosts.Count} reserving={count} wait_ms={Stopwatch.GetElapsedTime(begin).TotalMilliseconds:F0}"));
                return;
            }
            await Task.Delay(AllReleasePosts.Peek().AddMinutes(10) - now + TimeSpan.FromSeconds(1), deadline.Token);
        }
    }

    private static void ClosePostReservation(int count)
    {
        // Conservatively retain the full reservation until ten minutes after the process finishes.
        var completed = DateTimeOffset.UtcNow;
        for (var i = 0; i < count; i++) AllReleasePosts.Enqueue(completed);
    }

    private static CancellationTokenSource ReleaseDeadline(TimeSpan maximum)
    {
        if (!DateTimeOffset.TryParse(Environment.GetEnvironmentVariable("ZEKA_ZEK27_RELEASE_DEADLINE_UTC"), out var cutoff))
            throw new InvalidOperationException("ZEK27_RELEASE_BUDGET_MISSING");
        var remaining = cutoff - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero) throw new InvalidOperationException("ZEK27_RELEASE_BUDGET_EXHAUSTED");
        return new CancellationTokenSource(remaining < maximum ? remaining : maximum);
    }

    private static string RequiredPath(string variable, bool directory = false)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        if (value is null || !Path.IsPathFullyQualified(value) || (directory ? !Directory.Exists(value) : !File.Exists(value)))
            throw new InvalidOperationException("ZEK27_ADMITTED_PATH_MISSING");
        return value;
    }

    private static async Task<PublicEnvelope> ReadEnvelopeAsync(HttpResponseMessage response)
    {
        var bytes = await response.Content.ReadAsByteArrayAsync();
        return new((int)response.StatusCode, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)),
            response.Content.Headers.ContentType?.ToString(), response.Headers.CacheControl?.NoStore == true,
            response.Headers.Location is not null, response.Headers.Contains("Set-Cookie"));
    }

    private sealed record PublicEnvelope(int Status, int BodyBytes, string BodySha256, string? ContentType,
        bool NoStore, bool LocationPresent, bool CredentialPresent);
    private sealed record ReleaseRequest(PublicEnvelope Envelope, double WorkMs, double ResponseMs);
    private sealed record ReleaseSample(int Index, int ApplicationPid, string State, double SchemaMs,
        double ReadyMs, ReleaseRequest Cold, ReleaseRequest Warm, ReleaseRequest Failed);

    private static async Task AssertSameUnavailableAsync(HttpResponseMessage first, HttpResponseMessage second)
    {
        foreach (var response in new[] { first, second })
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore == true);
            Assert.False(response.Headers.Contains("Set-Cookie"));
            Assert.Null(response.Headers.Location);
            Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
        }
    }

    private sealed class InitializationBarrier : DbCommandInterceptor
    {
        public TaskCompletionSource FirstAttempt { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource RejectFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource RecoveryAttempt { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowRecovery { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int commands;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            switch (Interlocked.Increment(ref commands))
            {
                case 1:
                    FirstAttempt.SetResult();
                    await RejectFirst.Task.WaitAsync(cancellationToken);
                    throw new InvalidOperationException("SYNTHETIC_REGISTRATION_READINESS_REJECTION");
                case 2:
                    RecoveryAttempt.SetResult();
                    await AllowRecovery.Task.WaitAsync(cancellationToken);
                    break;
            }
            return result;
        }
    }

    private sealed class DependencyFailure(string sqlState)
    {
        public TaskCompletionSource Observed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Record(Exception failure)
        {
            if (failure is PostgresException error && error.SqlState == sqlState) Observed.TrySetResult();
        }
    }

    private sealed class FailedConnection(DependencyFailure failure) : DbConnectionInterceptor
    {
        public override Task ConnectionFailedAsync(DbConnection connection, ConnectionErrorEventData eventData,
            CancellationToken cancellationToken = default)
        {
            failure.Record(eventData.Exception);
            return Task.CompletedTask;
        }
    }

    private sealed class FailedCommand(DependencyFailure failure) : DbCommandInterceptor
    {
        public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData,
            CancellationToken cancellationToken = default)
        {
            failure.Record(eventData.Exception);
            return Task.CompletedTask;
        }
    }

    private sealed class TransactionDeadline : DbTransactionInterceptor
    {
        public volatile bool HoldBeginning;
        public volatile bool HoldCommitAcknowledgement;
        private int blockedBegins;
        private int blockedAcknowledgements;
        public int BlockedBegins => Volatile.Read(ref blockedBegins);
        public int BlockedAcknowledgements => Volatile.Read(ref blockedAcknowledgements);

        public override async ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(DbConnection connection,
            TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result,
            CancellationToken cancellationToken = default)
        {
            if (HoldBeginning)
            {
                Interlocked.Increment(ref blockedBegins);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return result;
        }

        public override async Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (HoldCommitAcknowledgement)
            {
                Interlocked.Increment(ref blockedAcknowledgements);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
        }
    }

    private sealed class RegistrationTiming
    {
        private long firstWorkTicks = -1;
        private long lastWorkTicks = -1;
        public TimeSpan? LastWork
        {
            get
            {
                var ticks = Volatile.Read(ref lastWorkTicks);
                return ticks < 0 ? null : TimeSpan.FromTicks(ticks);
            }
        }
        public TimeSpan? FirstWork
        {
            get
            {
                var ticks = Volatile.Read(ref firstWorkTicks);
                return ticks < 0 ? null : TimeSpan.FromTicks(ticks);
            }
        }

        public void Record(TimeSpan duration)
        {
            Interlocked.CompareExchange(ref firstWorkTicks, duration.Ticks, -1);
            Volatile.Write(ref lastWorkTicks, duration.Ticks);
        }
    }

    private sealed class MeasuredRegistration(IRegistrationService service, RegistrationTiming timing) : IRegistrationService
    {
        public string? NormalizeAddress(string? address) => service.NormalizeAddress(address);
        public Guid? ReadChallengeId(string? token) => service.ReadChallengeId(token);

        public async Task<RegistrationOutcome> RegisterAsync(RegistrationRequest input, CancellationToken cancellationToken)
        {
            var start = Stopwatch.GetTimestamp();
            try { return await service.RegisterAsync(input, cancellationToken); }
            finally { timing.Record(Stopwatch.GetElapsedTime(start)); }
        }

        public Task<RegistrationOutcome> ResendAsync(string email, CancellationToken cancellationToken)
            => service.ResendAsync(email, cancellationToken);

        public Task<VerificationExchange> ExchangeAsync(string token, CancellationToken cancellationToken)
            => service.ExchangeAsync(token, cancellationToken);
    }

    private static async Task<string?> SeedAsync(RegistrationPostgreSqlFixture.RegistrationFixtureHost database,
        RegistrationRequest input, UserStatus status)
    {
        await using var scope = database.Services.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var db = scope.ServiceProvider.GetRequiredService<AuthManager.Infrastructure.Persistence.AuthDbContext>();
        var record = User.Create(input.Email!, input.Email!, "Existing", "Fixture", database.Clock.GetUtcNow());
        record.EmailConfirmed = status == UserStatus.Active;
        Assert.True((await manager.CreateAsync(record, $"Aa7!{Guid.NewGuid():N}")).Succeeded);
        db.Entry(record).Property(user => user.Status).CurrentValue = status;
        await db.SaveChangesAsync();
        return record.PasswordHash;
    }

    private sealed class CountingHasher(IOptions<PasswordHasherOptions> options) : IPasswordHasher<User>
    {
        private readonly PasswordHasher<User> inner = new(options);
        private int count;
        public int Count => Volatile.Read(ref count);

        public string HashPassword(User user, string password)
        {
            Interlocked.Increment(ref count);
            return inner.HashPassword(user, password);
        }

        public PasswordVerificationResult VerifyHashedPassword(User user, string hash, string provided)
            => inner.VerifyHashedPassword(user, hash, provided);
    }

    private static RegistrationRequest Input() => new()
    {
        Email = $"fictional-{Guid.NewGuid():N}@example.invalid", FirstName = "Ada", LastName = "Witness",
        Password = $"Aa7!{Guid.NewGuid():N}"
    };

    private static async Task<string> CsrfAsync(HttpClient client)
    {
        using var response = await client.GetAsync(SessionPath + "/csrf");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return payload.RootElement.GetProperty("csrfToken").GetString()!;
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string path, HttpContent body,
        string? csrf, string? origin, bool multipleOrigins = false, bool spoof = false, string? onboardingCookie = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = body };
        if (csrf is not null) request.Headers.Add("X-Zeka-CSRF", csrf);
        if (origin is not null)
            request.Headers.TryAddWithoutValidation("Origin", multipleOrigins ? new[] { origin, origin } : [origin]);
        if (spoof) request.Headers.Add("X-Forwarded-For", "127.0.0.9");
        if (onboardingCookie is not null) request.Headers.TryAddWithoutValidation("Cookie", onboardingCookie);
        return await client.SendAsync(request);
    }

    private sealed class Transport(WebApplication auth, WebApplication proxy, HttpClient upstream,
        X509Certificate2 certificate, bool release, Func<int> protectedRequests) : IAsyncDisposable
    {
        public int HashCount => auth.Services.GetRequiredService<CountingHasher>().Count;
        public bool RegistrationIsReady => auth.Services.GetRequiredService<RegistrationReadiness>().IsReady;
        public bool VerificationWorkerRegistered => auth.Services.GetServices<IHostedService>().Any(worker => worker is VerificationMailWorker);
        public int ProtectedRequests => protectedRequests();
        public string Authority => Address(auth);

        public HttpClient NewBrowser(bool direct = false, bool cookies = true)
            => NewClient(Address(direct ? auth : proxy), release ? "127.0.0.1" : "127.0.0.2", certificate, cookies);

        public HttpClient NewCleartextLegacyProbe()
            => NewClient(Address(auth, https: false), "127.0.0.2", certificate, cookies: false);

        public async Task<HttpStatusCode> HealthAsync(string path)
        {
            using var client = NewBrowser(direct: true, cookies: false);
            using var response = await client.GetAsync(path);
            return response.StatusCode;
        }

        public async Task AwaitReadinessAsync()
        {
            using var client = NewBrowser(direct: true, cookies: false);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            while (true)
            {
                using var response = await client.GetAsync("/health/ready", deadline.Token);
                if (response.StatusCode == HttpStatusCode.OK) return;
                Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
                await Task.Delay(TimeSpan.FromMilliseconds(50), deadline.Token);
            }
        }

        public static async Task<Transport> StartAsync(WebApplicationBuilder builder, bool release = false, bool tenantEndpoints = false)
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=zeka registration fixture", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder();
            san.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(san.Build());
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyCertSign, true));
            var temporary = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
            var certificate = new X509Certificate2(temporary.Export(X509ContentType.Pfx));
            temporary.Dispose();

            var httpsPort = release ? AvailableLoopbackPort() : 0;
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.Listen(IPAddress.Loopback, httpsPort, endpoint => endpoint.UseHttps(certificate));
                if (!release) options.Listen(IPAddress.Loopback, 0);
            });
            if (release)
            {
                var issuer = $"https://127.0.0.1:{httpsPort}";
                var file = RequiredPath(IssuerKeyFileEnvironment);
                builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Authentication:Issuer"] = issuer,
                    ["Authentication:Audience"] = ReleaseAudience,
                    ["Authentication:JwksUri"] = issuer + "/.well-known/jwks.json",
                    ["Authentication:ClockSkew"] = "00:00:30",
                    ["Authentication:JwksRefreshInterval"] = "00:00:10",
                    ["Authentication:JwksMaximumStaleness"] = "00:01:00",
                    ["AuthenticationIssuer:ActiveSigningKeyReference"] = file,
                    ["AuthenticationIssuer:ActiveKeyId"] = ReleaseKeyId,
                    ["AuthenticationIssuer:AccessTokenLifetime"] = "00:05:00"
                });
                if (tenantEndpoints)
                {
                    builder.Services.AddTenantAuthentication(builder.Configuration);
                    builder.Services.AddHttpClient<IJwksDocumentSource, HttpJwksDocumentSource>()
                        .RemoveAllLoggers().ConfigurePrimaryHttpMessageHandler(() => StrictHandler(certificate));
                }
                builder.Services.AddAccessTokenIssuer(builder.Configuration);
                LocalStackHealth.Add(builder);
            }
            else
            {
                builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["BrowserSession:Origin"] = Origin, ["BrowserSession:TrustedProxyPeers:0"] = "127.0.0.1",
                    ["BrowserSession:Limits:PerAddress"] = "60", ["BrowserSession:Limits:MaxPartitions"] = "16",
                    ["BrowserSession:Limits:Aggregate"] = "256", ["BrowserSession:Limits:WindowSeconds"] = "60"
                });
                builder.Services.AddOnboardingPersistenceFoundations(builder.Configuration);
                builder.Services.AddScoped<IBrowserSessions, BrowserSessions>();
                builder.Services.AddSingleton(new BrowserSessionPolicy
                {
                    HashKeyReference = Path.Combine(Path.GetTempPath(), "fixture-unused-browser-session-key"),
                    AccessLifetime = TimeSpan.FromMinutes(5), IdleLifetime = TimeSpan.FromHours(1),
                    AbsoluteLifetime = TimeSpan.FromHours(4)
                });
                builder.Services.AddSingleton<ISessionAccessTokenIssuer>(services => new AccessTokenIssuer(
                    new FileSigningKeyProvider(),
                    new IssuerOptions { AccessTokenLifetime = TimeSpan.FromMinutes(5) },
                    new AuthenticationOptions(), services.GetRequiredService<TimeProvider>()));
            }
            builder.Services.AddBrowserSessionTransport();
            builder.Services.AddSingleton<BrowserRegistrationEndpoints.RegistrationAbuseLimits>();
            var auth = builder.Build();
            var proxyBuilder = WebApplication.CreateBuilder();
            proxyBuilder.Logging.ClearProviders();
            proxyBuilder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, endpoint => endpoint.UseHttps(certificate)));
            var proxy = proxyBuilder.Build();
            HttpClient? upstream = null;
            var afterAuthorization = 0;
            try
            {
                auth.UseLocalStackHealth();
                if (tenantEndpoints) auth.UseRouting();
                auth.UseBrowserSessionAttribution();
                if (tenantEndpoints)
                {
                    auth.UseAuthentication();
                    auth.UseAuthorization();
                    auth.Use(async (context, next) =>
                    {
                        if (context.Request.Path.StartsWithSegments("/api/v1/tenant-access"))
                            Interlocked.Increment(ref afterAuthorization);
                        await next(context);
                    });
                    auth.MapTenantAccess();
                }
                auth.UseRateLimiter();
                if (release) auth.MapIssuerEndpoints();
                auth.MapBrowserSessions();
                auth.RegisterEndpoints();
                auth.UseAuthHttpsRedirection();
                await auth.StartAsync();
                if (release)
                {
                    var actual = auth.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
                    Assert.True(actual.Count == 1 && actual.Single() == $"https://127.0.0.1:{httpsPort}");
                    if (tenantEndpoints)
                    {
                        var endpoints = ((IEndpointRouteBuilder)auth).DataSources.SelectMany(source => source.Endpoints)
                            .OfType<RouteEndpoint>().Where(e => e.RoutePattern.RawText?.StartsWith("/api/v1/tenant-access/", StringComparison.Ordinal) == true).ToArray();
                        Assert.True(endpoints.Length == 2 && endpoints.All(e =>
                            e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.SequenceEqual(["GET"])
                            && e.Metadata.GetOrderedMetadata<IAuthorizeData>().Any(a => a.AuthenticationSchemes == JwtBearerDefaults.AuthenticationScheme)));
                    }
                }
                upstream = NewClient(Address(auth), "127.0.0.1", certificate, cookies: false);
                proxy.Run(async context =>
                {
                    using var forwarded = new HttpRequestMessage(new HttpMethod(context.Request.Method),
                        context.Request.Path + context.Request.QueryString);
                    if (HttpMethods.IsPost(context.Request.Method))
                    {
                        forwarded.Content = new StreamContent(context.Request.Body);
                        forwarded.Content.Headers.TryAddWithoutValidation("Content-Type", context.Request.ContentType);
                    }
                    foreach (var key in new[] { "Origin", "X-Zeka-CSRF", "Cookie" })
                        if (context.Request.Headers.TryGetValue(key, out var value))
                            forwarded.Headers.TryAddWithoutValidation(key, value.ToArray());
                    forwarded.Headers.Add("X-Forwarded-For", context.Connection.RemoteIpAddress!.MapToIPv4().ToString());
                    using var response = await upstream.SendAsync(forwarded, context.RequestAborted);
                    context.Response.StatusCode = (int)response.StatusCode;
                    foreach (var header in response.Headers)
                        if (header.Key is "Set-Cookie" or "Cache-Control" or "Location")
                            context.Response.Headers[header.Key] = header.Value.ToArray();
                    context.Response.ContentType = response.Content.Headers.ContentType?.ToString();
                    await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
                });
                await proxy.StartAsync();
                return new(auth, proxy, upstream, certificate, release, () => Volatile.Read(ref afterAuthorization));
            }
            catch
            {
                upstream?.Dispose();
                await proxy.DisposeAsync();
                await auth.DisposeAsync();
                certificate.Dispose();
                throw;
            }
        }

        private static string Address(WebApplication app, bool https = true)
            => app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses
                .Single(uri => uri.StartsWith(https ? "https:" : "http:", StringComparison.Ordinal));

        private static int AvailableLoopbackPort()
        {
            var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            try { return ((IPEndPoint)reservation.LocalEndpoint).Port; }
            finally { reservation.Stop(); }
        }

        private static SocketsHttpHandler StrictHandler(X509Certificate2 certificate)
        {
            var chain = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust, RevocationMode = X509RevocationMode.NoCheck };
            chain.CustomTrustStore.Add(certificate);
            return new SocketsHttpHandler
            {
                UseProxy = false, AllowAutoRedirect = false, UseCookies = false,
                SslOptions = new() { CertificateChainPolicy = chain }
            };
        }

        private static HttpClient NewClient(string location, string localAddress, X509Certificate2 certificate, bool cookies)
        {
            var handler = StrictHandler(certificate);
            handler.UseCookies = cookies;
            handler.CookieContainer = new CookieContainer();
            handler.ConnectCallback = async (context, cancellationToken) =>
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    socket.Bind(new IPEndPoint(IPAddress.Parse(localAddress), 0));
                    await socket.ConnectAsync(context.DnsEndPoint, cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            };
            return new HttpClient(handler) { BaseAddress = new Uri(location), Timeout = TimeSpan.FromSeconds(10) };
        }

        public async ValueTask DisposeAsync()
        {
            upstream.Dispose();
            await proxy.DisposeAsync();
            await auth.DisposeAsync();
            certificate.Dispose();
        }
    }
}
