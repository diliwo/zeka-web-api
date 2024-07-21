using DiliBeneficiary.Application.Beneficiaries.Commands.Exceptions;
using DiliBeneficiary.Core.Interfaces;
using MediatR;

namespace DiliBeneficiary.Application.Beneficiaries.Commands.UpdateBeneficiaries
{
    public class UpdateBeneficiairesCommand : IRequest<int>
    {
        //public int Id { get; set; }
        public List<string> ListOfNiss { get; set; }

        public class UpdateBeneficiairesCommandHandler : IRequestHandler<UpdateBeneficiairesCommand, int>
        {
            public readonly IRepositoryManager _repository;
            public readonly IBeneficiaryService _beneficiaryService;

            public UpdateBeneficiairesCommandHandler(
                IRepositoryManager repository,
                IBeneficiaryService beneficiaryService
                )
            {
                _repository = repository;
                _beneficiaryService = beneficiaryService;
            }

            public async Task<int> Handle(UpdateBeneficiairesCommand request, CancellationToken cancellationToken)
            {
                if (request.ListOfNiss.Count == 0)
                {
                    throw new BeneficiaryBadRequestException();
                }

                var numberOfUpdatedBeneficiaries = await _beneficiaryService.Update(request.ListOfNiss);

                return numberOfUpdatedBeneficiaries;
            }
        }
    }
}
