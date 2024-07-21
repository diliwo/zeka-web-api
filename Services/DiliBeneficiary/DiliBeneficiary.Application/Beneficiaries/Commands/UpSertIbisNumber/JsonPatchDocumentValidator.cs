using DiliBeneficiary.Application.Beneficiaries.Model;
using FluentValidation;
using Microsoft.AspNetCore.JsonPatch;
using Microsoft.AspNetCore.JsonPatch.Operations;

namespace DiliBeneficiary.Application.Beneficiaries.Commands.UpSertIbisNumber;

public class JsonPatchDocumentValidator : AbstractValidator<JsonPatchDocument<UpdateBeneficaryDto>>
{
    public JsonPatchDocumentValidator()
    {
        RuleForEach(doc => doc.Operations).Custom((operation, context) =>
        {
            if (operation.OperationType == OperationType.Add || operation.OperationType == OperationType.Replace)
            {
                if (operation.value is string stringValue && stringValue.Length > 20)
                {
                    context.AddFailure("Property", $"Action impossible, nombre de caractères max autorisé : 20");
                }
            }
        });
    }
}