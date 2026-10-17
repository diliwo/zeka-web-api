namespace ClientManagement.Application.Clients.Queries.LookupBeneficiaries;

public sealed class BeneficiaryLookupDto
{
    public int ContractVersion => 1;
    public IReadOnlyList<BeneficiaryLookupItemDto> Items { get; init; } = [];
    public bool HasMore { get; init; }
}

public sealed class BeneficiaryLookupItemDto
{
    public int BeneficiaryId { get; init; }
    public string DisplayName { get; init; } = string.Empty;
}
