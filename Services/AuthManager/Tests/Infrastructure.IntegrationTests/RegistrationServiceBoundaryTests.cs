using System.Security.Cryptography;
using AuthManager.Application.Onboarding;
using AuthManager.Core.Enums;
using AuthManager.Infrastructure.Identity.Models;
using AuthManager.Infrastructure.Onboarding;
using AuthManager.Infrastructure.Persistence;
using AuthManager.Infrastructure.Persistence.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Infrastructure.IntegrationTests;

public sealed class RegistrationServiceBoundaryTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("missing-at")]
    [InlineData("Display Name <unit@example.invalid>")]
    [InlineData("unit<>@example.invalid")]
    public void Invalid_addresses_are_rejected_before_any_store_access(string? address)
    {
        using var fixture = new RegistrationServiceInputs();
        Assert.Null(fixture.Service.NormalizeAddress(address));
        fixture.AssertNoIdentityOrHashAccess();
    }

    [Fact]
    public void Address_normalization_trims_and_applies_the_configured_identity_character_policy()
    {
        using var fixture = new RegistrationServiceInputs();
        Assert.Equal("UNIT@EXAMPLE.INVALID", fixture.Service.NormalizeAddress(" unit@example.invalid "));
        Assert.Null(fixture.Service.NormalizeAddress(new string('a', 257) + "@example.invalid"));
        fixture.Manager.Options.User.AllowedUserNameCharacters = "";
        Assert.Equal("UNIT+TAG@EXAMPLE.INVALID", fixture.Service.NormalizeAddress("unit+tag@example.invalid"));
        fixture.Manager.Options.User.AllowedUserNameCharacters = "unit@example.invalid";
        Assert.Null(fixture.Service.NormalizeAddress("unit+tag@example.invalid"));
        fixture.AssertNoIdentityOrHashAccess();
    }

    [Theory]
    [InlineData("email")]
    [InlineData("first-null")]
    [InlineData("first-empty")]
    [InlineData("first-whitespace")]
    [InlineData("first-control")]
    [InlineData("first-too-long")]
    [InlineData("last-null")]
    [InlineData("last-control")]
    [InlineData("password-null")]
    [InlineData("password-empty")]
    [InlineData("password-too-long")]
    [InlineData("password-policy")]
    public async Task Registration_rejects_invalid_inputs_before_hashing_or_mutation(string invalid)
    {
        using var fixture = new RegistrationServiceInputs();
        var request = new RegistrationRequest
        {
            Email = invalid == "email" ? "not-an-email" : "unit@example.invalid",
            FirstName = invalid switch
            {
                "first-null" => null, "first-empty" => "", "first-whitespace" => " ",
                "first-control" => "Unit\n", "first-too-long" => new string('a', 101), _ => "Unit"
            },
            LastName = invalid switch { "last-null" => null, "last-control" => "Test\t", _ => "Test" },
            Password = invalid switch
            {
                "password-null" => null, "password-empty" => "", "password-too-long" => new string('a', 257),
                "password-policy" => new string('a', 12), _ => Guid.NewGuid().ToString("N") + "aA1!"
            }
        };
        Assert.Equal(RegistrationOutcome.Invalid, await fixture.Service.RegisterAsync(request, CancellationToken.None));
        Assert.Null(fixture.Cancellation.Token);
        fixture.AssertNoIdentityOrHashAccess();
    }

    [Fact]
    public async Task Disabled_registration_refuses_all_commands_and_grants_before_storage()
    {
        using var fixture = new RegistrationServiceInputs(enabled: false);
        Assert.Equal(RegistrationOutcome.Unavailable, await fixture.Service.RegisterAsync(new(), CancellationToken.None));
        Assert.Equal(RegistrationOutcome.Unavailable, await fixture.Service.ResendAsync("unit@example.invalid", CancellationToken.None));
        var exchange = await fixture.Service.ExchangeAsync("malformed", CancellationToken.None);
        Assert.Equal(RegistrationOutcome.Unavailable, exchange.Outcome);
        Assert.Null(exchange.Credential);
        Assert.Null(exchange.ExpiresAtUtc);
        Assert.Null(await fixture.Service.ValidateGrantAsync(RegistrationServiceInputs.NewCredential(), OnboardingGrant.OrganisationPurpose, CancellationToken.None));
        fixture.AssertNoIdentityOrHashAccess();
    }

    [Theory]
    [InlineData("register")]
    [InlineData("resend")]
    [InlineData("exchange")]
    public async Task Precancelled_commands_restore_the_enclosing_identity_cancellation_binding(string operation)
    {
        using var fixture = new RegistrationServiceInputs();
        using var previous = new CancellationTokenSource();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using (fixture.Cancellation.Bind(previous.Token))
        {
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                if (operation == "register") await fixture.Service.RegisterAsync(new(), cancelled.Token);
                else if (operation == "resend") await fixture.Service.ResendAsync("unit@example.invalid", cancelled.Token);
                else await fixture.Service.ExchangeAsync("malformed", cancelled.Token);
            });
            Assert.Equal(cancelled.Token, error.CancellationToken);
            Assert.Equal(previous.Token, fixture.Cancellation.Token);
        }
        Assert.Null(fixture.Cancellation.Token);
        fixture.AssertNoIdentityOrHashAccess();
    }

    [Fact]
    public async Task Malformed_resend_exchange_and_grant_inputs_fail_closed_without_storage()
    {
        using var fixture = new RegistrationServiceInputs();
        Assert.Equal(RegistrationOutcome.Invalid, await fixture.Service.ResendAsync("not-an-email", CancellationToken.None));
        var exchange = await fixture.Service.ExchangeAsync("malformed", CancellationToken.None);
        Assert.Equal(RegistrationOutcome.Invalid, exchange.Outcome);
        Assert.Null(exchange.Credential);
        Assert.Null(exchange.ExpiresAtUtc);
        Assert.Null(await fixture.Service.ValidateGrantAsync("malformed", OnboardingGrant.OrganisationPurpose, CancellationToken.None));
        Assert.Null(await fixture.Service.ValidateGrantAsync(RegistrationServiceInputs.NewCredential(), "OtherPurpose", CancellationToken.None));
        fixture.AssertNoIdentityOrHashAccess();
    }

    [Theory]
    [InlineData("missing-address")]
    [InlineData("missing-stamp")]
    [InlineData("changed-address")]
    [InlineData("changed-stamp")]
    public void Verification_bindings_reject_changed_or_missing_identity_authority(string change)
    {
        var user = RegistrationStore.CreateUser();
        var challenge = RegistrationStore.CreateChallenge(user);
        Assert.True(RegistrationService.BindingsMatch(challenge, user));
        if (change == "missing-address") user.NormalizedEmail = null;
        else if (change == "missing-stamp") user.SecurityStamp = "";
        else if (change == "changed-address") user.NormalizedEmail = "OTHER@EXAMPLE.INVALID";
        else user.SecurityStamp = Guid.NewGuid().ToString("N");
        Assert.False(RegistrationService.BindingsMatch(challenge, user));
    }

    [Fact]
    public void Challenge_identity_is_read_only_from_a_valid_canonical_token()
    {
        using var fixture = new RegistrationServiceInputs();
        var row = RegistrationStore.CreateChallenge(RegistrationStore.CreateUser());
        var token = fixture.Tokens.Create(row)!;
        Assert.Equal(row.Id, fixture.Service.ReadChallengeId(token));
        Assert.Null(fixture.Service.ReadChallengeId(null));
        Assert.Null(fixture.Service.ReadChallengeId("malformed"));
        fixture.AssertNoIdentityOrHashAccess();
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("purpose")]
    [InlineData("consumed")]
    [InlineData("revoked")]
    [InlineData("expired")]
    [InlineData("pending-user")]
    [InlineData("unconfirmed-user")]
    [InlineData("stamp-changed")]
    [InlineData("stamp-missing")]
    public async Task Organisation_grants_require_a_live_unconsumed_grant_and_current_confirmed_identity(string invalid)
    {
        await using var store = await RegistrationStore.CreateAsync();
        using var fixture = new RegistrationServiceInputs(database: store.Database);
        var credential = RegistrationServiceInputs.NewCredential();
        if (invalid != "missing")
        {
            var user = RegistrationStore.CreateUser();
            if (invalid != "pending-user")
            {
                user.EmailConfirmed = true;
                user.ActivateConfirmedIdentity(RegistrationStore.Now);
            }
            var challenge = RegistrationStore.CreateChallenge(user);
            var grant = new OnboardingGrant
            {
                Id = Guid.NewGuid(), ChallengeId = challenge.Id, UserId = user.Id,
                Purpose = invalid == "purpose" ? "OtherPurpose" : OnboardingGrant.OrganisationPurpose,
                CredentialHash = VerificationTokenService.GrantHash(credential),
                StampBinding = VerificationTokenService.StampBinding(user.SecurityStamp!),
                ExpiresAtUtc = invalid == "expired" ? RegistrationStore.Now : RegistrationStore.Now.AddMinutes(10),
                ConsumedAtUtc = invalid == "consumed" ? RegistrationStore.Now : null,
                RevokedAtUtc = invalid == "revoked" ? RegistrationStore.Now : null
            };
            if (invalid == "unconfirmed-user") user.EmailConfirmed = false;
            if (invalid == "stamp-changed") user.SecurityStamp = Guid.NewGuid().ToString("N");
            if (invalid == "stamp-missing") user.SecurityStamp = null;
            store.Database.AddRange(user, challenge, grant);
            await store.Database.SaveChangesAsync();
            store.Database.ChangeTracker.Clear();
        }
        Assert.Null(await fixture.Service.ValidateGrantAsync(credential, OnboardingGrant.OrganisationPurpose, CancellationToken.None));
        Assert.Empty(store.Database.ChangeTracker.Entries());
        fixture.AssertNoIdentityOrHashAccess();
    }

    [Fact]
    public async Task Current_organisation_grant_selects_only_its_confirmed_user_without_mutation()
    {
        await using var store = await RegistrationStore.CreateAsync();
        using var fixture = new RegistrationServiceInputs(database: store.Database);
        var user = RegistrationStore.CreateUser();
        user.EmailConfirmed = true;
        user.ActivateConfirmedIdentity(RegistrationStore.Now);
        var challenge = RegistrationStore.CreateChallenge(user);
        var credential = RegistrationServiceInputs.NewCredential();
        store.Database.AddRange(user, challenge, new OnboardingGrant
        {
            Id = Guid.NewGuid(), ChallengeId = challenge.Id, UserId = user.Id,
            CredentialHash = VerificationTokenService.GrantHash(credential),
            StampBinding = VerificationTokenService.StampBinding(user.SecurityStamp!),
            ExpiresAtUtc = RegistrationStore.Now.AddMinutes(10)
        });
        await store.Database.SaveChangesAsync();
        store.Database.ChangeTracker.Clear();
        Assert.Equal(user.Id, await fixture.Service.ValidateGrantAsync(credential, OnboardingGrant.OrganisationPurpose, CancellationToken.None));
        Assert.Null(await fixture.Service.ValidateGrantAsync(RegistrationServiceInputs.NewCredential(), OnboardingGrant.OrganisationPurpose, CancellationToken.None));
        Assert.Empty(store.Database.ChangeTracker.Entries());
        fixture.AssertNoIdentityOrHashAccess();
    }
}

internal sealed class RegistrationServiceInputs : IDisposable
{
    private readonly RegistrationOptionInputs inputs = new();
    private readonly ServiceProvider services = new ServiceCollection().BuildServiceProvider();
    private readonly bool ownsDatabase;
    private readonly AuthDbContext database;
    private readonly Mock<IUserStore<User>> identityStore = new(MockBehavior.Strict);
    private readonly RefuseHashing hasher = new();
    public RegistrationOptions Options { get; }
    public VerificationTokenService Tokens { get; }
    public UserManager<User> Manager { get; }
    public RegistrationIdentityCancellation Cancellation { get; } = new();
    public RegistrationService Service { get; }

    public RegistrationServiceInputs(bool enabled = true, AuthDbContext? database = null)
    {
        inputs.Values["OnboardingRegistration:Enabled"] = enabled.ToString();
        Options = inputs.Read();
        Tokens = new VerificationTokenService(Options);
        identityStore.Setup(value => value.Dispose());
        var identity = new IdentityOptions();
        identity.Password.RequiredLength = 8;
        Manager = new UserManager<User>(identityStore.Object, Microsoft.Extensions.Options.Options.Create(identity), hasher,
            [], [new PasswordValidator<User>()], new UpperInvariantLookupNormalizer(), new IdentityErrorDescriber(),
            services, NullLogger<UserManager<User>>.Instance);
        ownsDatabase = database is null;
        this.database = database ?? new AuthDbContext(new DbContextOptionsBuilder<AuthDbContext>().Options);
        Service = new RegistrationService(this.database, Manager, Tokens, Options,
            services.GetRequiredService<IServiceScopeFactory>(), Cancellation, new RegistrationStore.Clock());
    }

    public static string NewCredential() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
    public void AssertNoIdentityOrHashAccess()
    {
        Assert.Empty(identityStore.Invocations);
        Assert.Equal(0, hasher.Calls);
    }
    public void Dispose()
    {
        Manager.Dispose();
        if (ownsDatabase) database.Dispose();
        Options.Dispose();
        services.Dispose();
        inputs.Dispose();
    }
    private sealed class RefuseHashing : IPasswordHasher<User>
    {
        public int Calls { get; private set; }
        public string HashPassword(User user, string password)
        { Calls++; throw new InvalidOperationException("HASHING_REACHED_BEFORE_VALIDATION"); }
        public PasswordVerificationResult VerifyHashedPassword(User user, string hashedPassword, string providedPassword)
            => throw new InvalidOperationException("PASSWORD_VERIFICATION_NOT_ADMITTED_BY_THIS_UNIT_FIXTURE");
    }
}

// SQLite is a unit persistence adapter here, not PostgreSQL/RLS or runtime acceptance evidence.
internal sealed class RegistrationStore(SqliteConnection connection, AuthDbContext database) : IAsyncDisposable
{
    public static readonly DateTimeOffset Now = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);
    public AuthDbContext Database { get; } = database;
    public static async Task<RegistrationStore> CreateAsync(params Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] interceptors)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AuthDbContext>().UseSqlite(connection).AddInterceptors(interceptors).Options;
        var database = new UnitContext(options);
        await database.Database.EnsureCreatedAsync();
        return new(connection, database);
    }
    public static User CreateUser()
    {
        var user = User.Create("unit@example.invalid", "unit@example.invalid", "Unit", "Fixture", Now);
        user.NormalizedEmail = "UNIT@EXAMPLE.INVALID";
        user.NormalizedUserName = user.NormalizedEmail;
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        return user;
    }
    public static EmailVerificationChallenge CreateChallenge(User user, DateTimeOffset? expiresAt = null, string keyId = "active") => new()
    {
        Id = Guid.NewGuid(), UserId = user.Id, Generation = 1, KeyId = keyId,
        AddressBinding = VerificationTokenService.AddressBinding(user.NormalizedEmail!),
        StampBinding = VerificationTokenService.StampBinding(user.SecurityStamp!), TokenHash = "",
        CreatedAtUtc = Now, ExpiresAtUtc = expiresAt ?? Now.AddDays(1)
    };
    public async ValueTask DisposeAsync()
    { await Database.DisposeAsync(); await connection.DisposeAsync(); }
    public sealed class Clock : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = Now;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
    private sealed class UnitContext(DbContextOptions<AuthDbContext> options) : AuthDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder model)
        {
            base.OnModelCreating(model);
            // Preserve production predicates while giving SQLite a sortable UTC representation.
            // No production mapping, privilege, migration or provider contract is changed.
            var utc = new ValueConverter<DateTimeOffset, long>(value => value.UtcTicks,
                value => new DateTimeOffset(value, TimeSpan.Zero));
            foreach (var property in model.Model.GetEntityTypes().SelectMany(type => type.GetProperties()))
                if (property.ClrType == typeof(DateTimeOffset) || property.ClrType == typeof(DateTimeOffset?))
                    property.SetValueConverter(utc);
        }
    }
}
