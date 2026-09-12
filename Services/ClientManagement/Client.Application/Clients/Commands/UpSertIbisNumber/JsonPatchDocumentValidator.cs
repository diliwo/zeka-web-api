using FluentValidation;

namespace ClientManagement.Application.Clients.Commands.UpSertIbisNumber;

public sealed class JsonPatchDocumentValidator : AbstractValidator<IClientPatch>
{
    public JsonPatchDocumentValidator()
    {
        RuleForEach(patch => patch.ReplacementValues).MaximumLength(20)
            .WithMessage("Action impossible, nombre de caractères max autorisé : 20");
    }
}
