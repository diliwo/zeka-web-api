using DiliBeneficiary.Core.Interfaces;
using MediatR;

namespace DiliBeneficiary.Application.Beneficiaries.Commands.ImportBeneficiary
{
    public class ImportBeneficiaryCommand : IRequest<long>
    {
        //public int Id { get; set; }
        public string Niss { get; set; }

        public class ImportBeneficiaryCommandHandler : IRequestHandler<ImportBeneficiaryCommand, long>
        {
            public readonly IRepositoryManager _repository;
            public readonly IBeneficiaryService _beneficiaryService;

            public ImportBeneficiaryCommandHandler(
                IRepositoryManager repository,
                IBeneficiaryService beneficiaryService
                )
            {
                _repository = repository;
                _beneficiaryService = beneficiaryService;
            }

            public async Task<long> Handle(ImportBeneficiaryCommand request, CancellationToken cancellationToken)
            {
                var id = await _beneficiaryService.UpSert(request.Niss);

                return id;

            }
        }
    }
}
