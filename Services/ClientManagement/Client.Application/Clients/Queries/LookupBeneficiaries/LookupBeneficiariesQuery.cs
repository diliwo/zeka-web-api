using ClientManagement.Application.Common.Authorization;
using ClientManagement.Core.Interfaces;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace ClientManagement.Application.Clients.Queries.LookupBeneficiaries;

[RequiresTenantPermission("Clients.ViewAll", "Clients.ViewAssigned")]
public sealed class LookupBeneficiariesQuery : IRequest<BeneficiaryLookupDto>
{
    public string? SearchText { get; set; }
}

public sealed class LookupBeneficiariesQueryValidator : AbstractValidator<LookupBeneficiariesQuery>
{
    public LookupBeneficiariesQueryValidator() => RuleFor(x => x.SearchText)
        .Must(text => text is not null && text.Count(character => !char.IsWhiteSpace(character)) >= 3)
        .WithMessage("Search text must contain at least three non-whitespace characters.");
}

public sealed class LookupBeneficiariesQueryHandler(IRepositoryManager repository)
    : IRequestHandler<LookupBeneficiariesQuery, BeneficiaryLookupDto>
{
    public async Task<BeneficiaryLookupDto> Handle(LookupBeneficiariesQuery request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var text = request.SearchText!.Trim().ToLowerInvariant();
        // GetClients applies current dossier assignment visibility; EF tenant scope and RLS
        // remain active. Fetch one extra authorized row, never a global count.
        var matches = await repository.Client.GetClients()
            .Where(client => client.Id > 0 && (client.FirstName.ToLower().Contains(text)
                || client.LastName.ToLower().Contains(text)))
            .Select(client => new BeneficiaryLookupItemDto
            {
                BeneficiaryId = client.Id,
                DisplayName = client.LastName + " " + client.FirstName
            })
            .OrderBy(item => item.DisplayName).ThenBy(item => item.BeneficiaryId)
            .Take(21).ToListAsync(cancellationToken);
        return new BeneficiaryLookupDto
        {
            Items = matches.Take(20).ToArray(),
            HasMore = matches.Count > 20
        };
    }
}
