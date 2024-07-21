using DiliBeneficiary.Core.Interfaces;
using MediatR;

namespace DiliBeneficiary.Application.Beneficiaries.Commands.UpdateAllBeneficiaries
{
    public class UpdateAllBeneficiariesCommand : IRequest<int>
    {
        public class UpdateAllBeneficiariesCommandHandler : IRequestHandler<UpdateAllBeneficiariesCommand, int>
        {
            public readonly IRepositoryManager _repository;
            public readonly IBeneficiaryService _beneficiaryService;

            public UpdateAllBeneficiariesCommandHandler(
                IRepositoryManager repository,
                IBeneficiaryService beneficiaryService
                )
            {
                _repository = repository;
                _beneficiaryService = beneficiaryService;
            }

            public async Task<int> Handle(UpdateAllBeneficiariesCommand request, CancellationToken cancellationToken)
            {
                var nisses = await _repository.Beneficiary.GetBeneficiaryNissesAsync(false);

                var numberOfUpdatedBeneficiaries = await _beneficiaryService.Update(nisses);

                return numberOfUpdatedBeneficiaries;
            }
        }
    }
}
