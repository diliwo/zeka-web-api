using AuthManager.Application.Authentication.Models;
using AuthManager.Application.Common.Interfaces;
using AuthManager.Core.Enums;
using AuthManager.Infrastructure.Identity.Models;
using AuthManager.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuthManager.Tests;

public sealed class IdentityFoundationTests
{
    [Fact]
    public async Task Registration_creates_pending_user_with_hashed_password_and_utc_timestamp()
    {
        await using var context = await IdentityTestContext.CreateAsync();
        await using var scope = context.Services.CreateAsyncScope();
        var registration = scope.ServiceProvider.GetRequiredService<IAuthenticationService>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();

        var request = ValidRequest("owner@example.org", "owner");
        var before = DateTimeOffset.UtcNow;

        var result = await registration.RegisterUserAsync(request);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Errors));

        var user = await userManager.FindByEmailAsync(request.Email);
        Assert.NotNull(user);
        Assert.Equal(UserStatus.PendingVerification, user.Status);
        Assert.InRange(user.CreatedAtUtc, before, DateTimeOffset.UtcNow);
        Assert.True(user.LockoutEnabled);
        Assert.False(user.EmailConfirmed);
        Assert.NotNull(user.PasswordHash);
        Assert.NotEqual(request.Password, user.PasswordHash);
        Assert.True(await userManager.CheckPasswordAsync(user, request.Password));
        Assert.False(string.IsNullOrWhiteSpace(user.SecurityStamp));
    }

    [Fact]
    public async Task Normalized_email_has_database_unique_constraint()
    {
        await using var context = await IdentityTestContext.CreateAsync();

        await using (var firstScope = context.Services.CreateAsyncScope())
        {
            var dbContext = firstScope.ServiceProvider.GetRequiredService<AuthDbContext>();
            var first = User.Create(
                "owner@example.org",
                "first-owner",
                "Ada",
                "Lovelace",
                DateTimeOffset.UtcNow);
            first.NormalizedEmail = "OWNER@EXAMPLE.ORG";
            dbContext.Add(first);
            await dbContext.SaveChangesAsync();
        }

        await using var secondScope = context.Services.CreateAsyncScope();
        var secondDbContext = secondScope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var duplicate = User.Create(
            "OWNER@example.org",
            "second-owner",
            "Grace",
            "Hopper",
            DateTimeOffset.UtcNow);
        duplicate.NormalizedEmail = "OWNER@EXAMPLE.ORG";
        secondDbContext.Add(duplicate);

        await Assert.ThrowsAsync<DbUpdateException>(() => secondDbContext.SaveChangesAsync());
    }

    [Fact]
    public async Task Identity_rejects_password_that_does_not_satisfy_policy()
    {
        await using var context = await IdentityTestContext.CreateAsync();
        await using var scope = context.Services.CreateAsyncScope();
        var registration = scope.ServiceProvider.GetRequiredService<IAuthenticationService>();

        var result = await registration.RegisterUserAsync(
            ValidRequest("owner@example.org", "owner") with { Password = "weak" });

        Assert.False(result.Succeeded);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public async Task Three_failed_access_attempts_lock_the_user()
    {
        await using var context = await IdentityTestContext.CreateAsync();
        await using var scope = context.Services.CreateAsyncScope();
        var registration = scope.ServiceProvider.GetRequiredService<IAuthenticationService>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var request = ValidRequest("owner@example.org", "owner");
        var registrationResult = await registration.RegisterUserAsync(request);
        Assert.True(registrationResult.Succeeded);
        var user = Assert.IsType<User>(await userManager.FindByEmailAsync(request.Email));

        await userManager.AccessFailedAsync(user);
        await userManager.AccessFailedAsync(user);
        await userManager.AccessFailedAsync(user);

        Assert.True(await userManager.IsLockedOutAsync(user));
    }

    [Fact]
    public async Task Confirmation_and_password_reset_tokens_are_available()
    {
        await using var context = await IdentityTestContext.CreateAsync();
        await using var scope = context.Services.CreateAsyncScope();
        var registration = scope.ServiceProvider.GetRequiredService<IAuthenticationService>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var request = ValidRequest("owner@example.org", "owner");
        var registrationResult = await registration.RegisterUserAsync(request);
        Assert.True(registrationResult.Succeeded);
        var user = Assert.IsType<User>(await userManager.FindByEmailAsync(request.Email));

        var emailToken = await userManager.GenerateEmailConfirmationTokenAsync(user);
        var passwordResetToken = await userManager.GeneratePasswordResetTokenAsync(user);

        Assert.False(string.IsNullOrWhiteSpace(emailToken));
        Assert.False(string.IsNullOrWhiteSpace(passwordResetToken));
    }

    private static RegisterUserRequest ValidRequest(string email, string userName) =>
        new(email, userName, "Ada", "Lovelace", "Correct-Horse-7!");
}
