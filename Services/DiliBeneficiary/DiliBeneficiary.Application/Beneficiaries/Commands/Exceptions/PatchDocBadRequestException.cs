using DiliBeneficiary.Application.Common.Exceptions;

namespace DiliBeneficiary.Application.Beneficiaries.Commands.Exceptions
{
    public class PatchDocBadRequestException : BadRequestException
    {
        public PatchDocBadRequestException() : base("patchDoc object sent from client is null.")
        {
        }
    }
}
