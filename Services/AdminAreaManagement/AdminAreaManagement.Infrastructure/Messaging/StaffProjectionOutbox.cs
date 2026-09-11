using System.Text.Json;
using System.Diagnostics.Metrics;
using AdminAreaManagement.Application.Staffs;
using AdminAreaManagement.Core.Common;
using AdminAreaManagement.Core.Entities;
using AdminAreaManagement.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
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
    IEventBus publisher, ILogger<StaffProjectionOutbox> logger, IConfiguration configuration) : IStaffProjectionOutbox
{
    public const string DiagnosticsName = "AdminAreaManagement.StaffOutbox";
    private static readonly Meter Metrics = new(DiagnosticsName);
    private static readonly Counter<long> Publications = Metrics.CreateCounter<long>("staff_outbox.publications");
    private static readonly Histogram<double> PendingAge = Metrics.CreateHistogram<double>("staff_outbox.pending_age", "s");
    public void Stage(StaffMember staff, Team team)
    {
        var organisation = tenant.Current.OrganisationId.Value;
        if (!StaffProjectionRetryWorker.Organisations(configuration).Contains(organisation))
            throw new InvalidOperationException("Staff changes require configured autonomous outbox retry coverage for this organisation.");
        if (staff.OrganisationMembershipId == Guid.Empty || team.OrganisationId != organisation)
            throw new TenantContextException(TenantFailure.Conflict);
        var message = new StaffProjectionChangedV1(organisation, staff.OrganisationMembershipId,
            staff.ProjectionVersion, !staff.Softdelete, staff.FirstName, staff.LastName, staff.UserName, team.Name, team.Acronym);
        var row = new StaffProjectionMessage { EventId = message.Id, Payload = JsonSerializer.Serialize(message) };
        row.AssignToOrganisation(organisation);
        database.Add(row); // Committed atomically by the use case together with the staff change.
    }

    // Each call handles at most 100 rows. Failed attempts move behind older pending rows.
    public async Task DispatchAsync(CancellationToken cancellationToken)
    {
        var messages = await database.Set<StaffProjectionMessage>()
            .Where(x => x.PublishedAtUtc == null).OrderBy(x => x.LastModified ?? x.Created)
            .ThenBy(x => x.Id).Take(100).ToListAsync(cancellationToken);
        foreach (var row in messages)
        {
            PendingAge.Record(Math.Max(0, (DateTime.Now - row.Created).TotalSeconds));
            try
            {
                var message = JsonSerializer.Deserialize<StaffProjectionChangedV1>(row.Payload)
                    ?? throw new InvalidOperationException("Invalid staff outbox record.");
                if (message.OrganisationId != tenant.Current.OrganisationId.Value || message.Id != row.EventId)
                    throw new TenantContextException(TenantFailure.Conflict);
                // The legacy broker port has no cancellation argument; bound the wait. Duplicates are safe.
                await publisher.PublishAsync(message).WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
                row.PublishedAtUtc = DateTimeOffset.UtcNow;
                Publications.Add(1, new KeyValuePair<string, object?>("outcome", "published"));
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                Publications.Add(1, new KeyValuePair<string, object?>("outcome", "pending_failure"));
                logger.LogWarning("Staff projection publication failed; the durable record remains pending for automatic retry.");
                // Persist the attempt time using the existing audit column, including malformed records.
                row.LastModified = DateTime.Now;
            }
            await database.SaveChangesAsync(cancellationToken);
        }
    }
}
