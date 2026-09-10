using System.Text.Json;
using AdminAreaManagement.Application.Staffs;
using AdminAreaManagement.Core.Common;
using AdminAreaManagement.Core.Entities;
using AdminAreaManagement.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.Logging;
using Zeka.Contracts.Staff.V1;
using Zeka.Extensions.EventBus.Abstractions;
using Zeka.Extensions.MultiTenancy.Abstractions;

namespace AdminAreaManagement.Infrastructure.Messaging;

public sealed class StaffProjectionMessage : TenantOwnedEntity
{
    public Guid EventId { get; set; }
    public string Payload { get; set; } = string.Empty;
    public DateTimeOffset? PublishedAtUtc { get; set; }
}

public sealed class StaffProjectionMessageConfiguration : IEntityTypeConfiguration<StaffProjectionMessage>
{
    public void Configure(EntityTypeBuilder<StaffProjectionMessage> builder)
    {
        builder.ToTable("StaffProjectionOutbox");
        builder.HasKey(x => x.Id);
        builder.HasIndex(x => new { x.OrganisationId, x.EventId }).IsUnique();
        builder.Property(x => x.Payload).IsRequired();
    }
}

public sealed class StaffProjectionOutbox(ApplicationDbContext database, ITenantContextAccessor tenant,
    IEventBus publisher, ILogger<StaffProjectionOutbox> logger) : IStaffProjectionOutbox
{
    public void Stage(StaffMember staff, Team team)
    {
        var organisation = tenant.Current.OrganisationId.Value;
        if (staff.OrganisationMembershipId == Guid.Empty || team.OrganisationId != organisation)
            throw new TenantContextException(TenantFailure.Conflict);
        var message = new StaffProjectionChangedV1(organisation, staff.OrganisationMembershipId,
            staff.ProjectionVersion, !staff.Softdelete, staff.FirstName, staff.LastName, staff.UserName, team.Name, team.Acronym);
        var row = new StaffProjectionMessage { EventId = message.Id, Payload = JsonSerializer.Serialize(message) };
        row.AssignToOrganisation(organisation);
        database.Add(row); // Committed atomically by the use case together with the staff change.
    }

    // This same operation can be retried by an explicitly authorized, tenant-scoped worker.
    public async Task DispatchAsync(CancellationToken cancellationToken)
    {
        var messages = await database.Set<StaffProjectionMessage>()
            .Where(x => x.PublishedAtUtc == null).OrderBy(x => x.Id).Take(100).ToListAsync(cancellationToken);
        foreach (var row in messages)
        {
            var message = JsonSerializer.Deserialize<StaffProjectionChangedV1>(row.Payload)
                ?? throw new InvalidOperationException("Invalid staff outbox record.");
            if (message.OrganisationId != tenant.Current.OrganisationId.Value || message.Id != row.EventId)
                throw new TenantContextException(TenantFailure.Conflict);
            try { await publisher.PublishAsync(message); }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning("Staff projection publication failed; the durable record remains pending.");
                return;
            }
            row.PublishedAtUtc = DateTimeOffset.UtcNow;
            await database.SaveChangesAsync(cancellationToken);
        }
    }
}
