namespace AuthManager.Core.Organisations;

public sealed class Organisation
{
    private Organisation()
    {
    }

    public Guid Id { get; private set; }

    public string DisplayName { get; private set; } = string.Empty;

    public Guid OwnerUserId { get; private set; }

    public OrganisationStatus Status { get; private set; }

    public string? Slug { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public uint ConcurrencyVersion { get; private set; }

    public static Organisation Create(
        Guid id,
        string displayName,
        Guid ownerUserId,
        DateTimeOffset createdAtUtc,
        string? slug = null)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Organisation ID must not be empty.", nameof(id));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        if (ownerUserId == Guid.Empty)
        {
            throw new ArgumentException("Owner user ID must not be empty.", nameof(ownerUserId));
        }

        if (slug is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        }

        return new Organisation
        {
            Id = id,
            DisplayName = displayName.Trim(),
            OwnerUserId = ownerUserId,
            Status = OrganisationStatus.Pending,
            Slug = slug?.Trim(),
            CreatedAtUtc = createdAtUtc,
            UpdatedAtUtc = createdAtUtc,
            ConcurrencyVersion = 1
        };
    }

    public bool Activate(DateTimeOffset updatedAtUtc) =>
        TransitionTo(OrganisationStatus.Active, updatedAtUtc,
            OrganisationStatus.Pending, OrganisationStatus.Suspended);

    public bool Suspend(DateTimeOffset updatedAtUtc) =>
        TransitionTo(OrganisationStatus.Suspended, updatedAtUtc, OrganisationStatus.Active);

    public bool Close(DateTimeOffset updatedAtUtc) =>
        TransitionTo(OrganisationStatus.Closed, updatedAtUtc,
            OrganisationStatus.Pending, OrganisationStatus.Active, OrganisationStatus.Suspended);

    private bool TransitionTo(
        OrganisationStatus target,
        DateTimeOffset updatedAtUtc,
        params OrganisationStatus[] allowedCurrentStates)
    {
        if (!allowedCurrentStates.Contains(Status) || updatedAtUtc < UpdatedAtUtc)
        {
            return false;
        }

        Status = target;
        UpdatedAtUtc = updatedAtUtc;
        ConcurrencyVersion++;
        return true;
    }
}
