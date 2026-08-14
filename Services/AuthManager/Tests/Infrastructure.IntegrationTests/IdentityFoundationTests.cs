using AutoFixture;
using AuthManager.Application.Authentication.Models;
using AuthManager.Application.Common.Interfaces;
using AuthManager.Core.Enums;
using AuthManager.Infrastructure.Identity.Models;
using AuthManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.IntegrationTests;

public sealed class IdentityFoundationTests
{
    private readonly Fixture _fixture = new();

    [Fact]
    public async Task Registration_creates_pending_user_with_hashed_password_and_trusted_timestamp()
    {
        var now = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
        await using var context = await IdentityTestContext.CreateAsync(now);
        await using var scope = context.Services.CreateAsyncScope();
        var registration = scope.ServiceProvider.GetRequiredService<IAuthenticationService>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var request = ValidRequest();

        var result = await registration.RegisterUserAsync(request);
        var user = await userManager.FindByEmailAsync(request.Email);

        result.Succeeded.Should().BeTrue(string.Join(Environment.NewLine, result.Errors));
        user.Should().NotBeNull();
        user!.Status.Should().Be(UserStatus.PendingVerification);
        user.CreatedAtUtc.Should().Be(now);
        user.LockoutEnabled.Should().BeTrue();
        user.EmailConfirmed.Should().BeFalse();
        user.PasswordHash.Should().NotBeNullOrWhiteSpace().And.NotBe(request.Password);
        (await userManager.CheckPasswordAsync(user, request.Password)).Should().BeTrue();
        user.SecurityStamp.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Normalized_email_has_database_unique_constraint()
    {
        await using var context = await IdentityTestContext.CreateAsync();
        var normalizedEmail = "OWNER@EXAMPLE.ORG";

        await using (var firstScope = context.Services.CreateAsyncScope())
        {
            var dbContext = firstScope.ServiceProvider.GetRequiredService<AuthDbContext>();
            var first = CreateUser("owner@example.org", "first-owner");
            first.NormalizedEmail = normalizedEmail;
            dbContext.Add(first);
            await dbContext.SaveChangesAsync();
        }

        await using var secondScope = context.Services.CreateAsyncScope();
        var secondDbContext = secondScope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var duplicate = CreateUser("OWNER@example.org", "second-owner");
        duplicate.NormalizedEmail = normalizedEmail;
        secondDbContext.Add(duplicate);

        var action = () => secondDbContext.SaveChangesAsync();

        await action.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task Identity_rejects_password_that_does_not_satisfy_policy()
    {
        await using var context = await IdentityTestContext.CreateAsync();
        await using var scope = context.Services.CreateAsyncScope();
        var registration = scope.ServiceProvider.GetRequiredService<IAuthenticationService>();
        var request = ValidRequest() with { Password = "weak" };

        var result = await registration.RegisterUserAsync(request);

        result.Succeeded.Should().BeFalse();
        result.Errors.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Three_failed_access_attempts_lock_the_user()
    {
        await using var context = await IdentityTestContext.CreateAsync();
        await using var scope = context.Services.CreateAsyncScope();
        var registration = scope.ServiceProvider.GetRequiredService<IAuthenticationService>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var request = ValidRequest();
        var registrationResult = await registration.RegisterUserAsync(request);
        var user = await userManager.FindByEmailAsync(request.Email);

        registrationResult.Succeeded.Should().BeTrue();
        user.Should().NotBeNull();
        await userManager.AccessFailedAsync(user!);
        await userManager.AccessFailedAsync(user!);
        await userManager.AccessFailedAsync(user!);

        (await userManager.IsLockedOutAsync(user!)).Should().BeTrue();
    }

    [Fact]
    public async Task Confirmation_and_password_reset_tokens_are_available()
    {
        await using var context = await IdentityTestContext.CreateAsync();
        await using var scope = context.Services.CreateAsyncScope();
        var registration = scope.ServiceProvider.GetRequiredService<IAuthenticationService>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var request = ValidRequest();
        var registrationResult = await registration.RegisterUserAsync(request);
        var user = await userManager.FindByEmailAsync(request.Email);

        registrationResult.Succeeded.Should().BeTrue();
        user.Should().NotBeNull();
        var emailToken = await userManager.GenerateEmailConfirmationTokenAsync(user!);
        var passwordResetToken = await userManager.GeneratePasswordResetTokenAsync(user!);

        emailToken.Should().NotBeNullOrWhiteSpace();
        passwordResetToken.Should().NotBeNullOrWhiteSpace();
    }

    private RegisterUserRequest ValidRequest() =>
        _fixture.Build<RegisterUserRequest>()
            .With(value => value.Email, "owner@example.org")
            .With(value => value.UserName, "owner")
            .With(value => value.FirstName, "Ada")
            .With(value => value.LastName, "Lovelace")
            .With(value => value.Password, "Correct-Horse-7!")
            .Create();

    private User CreateUser(string email, string userName) =>
        User.Create(
            email,
            userName,
            _fixture.Create<string>(),
            _fixture.Create<string>(),
            DateTimeOffset.UtcNow);
}
