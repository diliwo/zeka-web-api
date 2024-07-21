using DiliBeneficiary.Application.Common.Exceptions;

namespace DiliBeneficiary.Application.Beneficiaries.Commands.Exceptions
{
    public class BeneficiaryBadRequestException : BadRequestException
    {
        public BeneficiaryBadRequestException() : base("Parameter is null")
        {
        }
    }
}
