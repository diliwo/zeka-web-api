using AuthManager.Infrastructure.Identity.Models;
using AuthManager.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthManager.Infrastructure.Persistence.Configurations;

internal sealed class OnboardingRegistrationConfiguration :
    IEntityTypeConfiguration<EmailVerificationChallenge>,
    IEntityTypeConfiguration<VerificationMailOutboxMessage>,
    IEntityTypeConfiguration<OnboardingGrant>,
    IEntityTypeConfiguration<OnboardingOperationReceipt>
{
    public void Configure(EntityTypeBuilder<EmailVerificationChallenge> builder)
    {
        builder.ToTable("EmailVerificationChallenges");
        builder.HasKey(row => row.Id);
        builder.Property(row => row.KeyId).HasMaxLength(32).IsRequired();
        builder.Property(row => row.TokenHash).HasMaxLength(64).IsRequired();
        builder.Property(row => row.AddressBinding).HasMaxLength(64).IsRequired();
        builder.Property(row => row.StampBinding).HasMaxLength(64).IsRequired();
        builder.Property(row => row.Status).HasConversion<string>().HasMaxLength(16);
        builder.Property(row => row.Version).IsConcurrencyToken();
        builder.HasIndex(row => new { row.UserId, row.Generation }).IsUnique();
        builder.HasIndex(row => row.UserId).IsUnique().HasFilter("\"Status\" = 'Pending'");
        builder.HasIndex(row => new { row.AddressBinding, row.CreatedAtUtc });
        builder.HasOne<User>().WithMany().HasForeignKey(row => row.UserId).OnDelete(DeleteBehavior.Restrict);
    }

    public void Configure(EntityTypeBuilder<VerificationMailOutboxMessage> builder)
    {
        builder.ToTable("VerificationMailOutboxMessages");
        builder.HasKey(row => row.Id);
        builder.Property(row => row.Status).HasConversion<string>().HasMaxLength(16);
        builder.Property(row => row.Outcome).HasMaxLength(32);
        builder.Property(row => row.Version).IsConcurrencyToken();
        builder.HasIndex(row => row.OperationId).IsUnique();
        builder.HasIndex(row => new { row.ChallengeId, row.Generation }).IsUnique();
        builder.HasIndex(row => new { row.Status, row.NextAttemptAtUtc });
        builder.HasOne<EmailVerificationChallenge>().WithMany().HasForeignKey(row => row.ChallengeId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    public void Configure(EntityTypeBuilder<OnboardingGrant> builder)
    {
        builder.ToTable("OnboardingGrants");
        builder.HasKey(row => row.Id);
        builder.Property(row => row.Purpose).HasMaxLength(32).IsRequired();
        builder.Property(row => row.CredentialHash).HasMaxLength(64).IsRequired();
        builder.Property(row => row.StampBinding).HasMaxLength(64).IsRequired();
        builder.HasIndex(row => row.CredentialHash).IsUnique();
        builder.HasIndex(row => row.ChallengeId).IsUnique();
        builder.HasOne<EmailVerificationChallenge>().WithMany().HasForeignKey(row => row.ChallengeId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    public void Configure(EntityTypeBuilder<OnboardingOperationReceipt> builder)
    {
        builder.ToTable("OnboardingOperationReceipts");
        builder.HasKey(row => row.Id);
        builder.Property(row => row.Kind).HasConversion<string>().HasMaxLength(16);
        builder.HasOne<EmailVerificationChallenge>().WithMany().HasForeignKey(row => row.ChallengeId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
