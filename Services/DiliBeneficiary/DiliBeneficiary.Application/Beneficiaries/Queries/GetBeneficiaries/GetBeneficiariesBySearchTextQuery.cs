using AutoMapper;
using AutoMapper.QueryableExtensions;
using DiliBeneficiary.Core.Interfaces;
using MediatR;

namespace DiliBeneficiary.Application.Beneficiaries.Queries.GetBeneficiaries
{
    public class GetBeneficiariesBySearchTextQuery : IRequest<BeneficiariesVm>
    {
        public string SearchText{ get; set; }

        public class GetBeneficiariesBySearchTextQueryHandler : IRequestHandler<GetBeneficiariesBySearchTextQuery, BeneficiariesVm>
        {
            private readonly IRepositoryManager _repository;
            private readonly IMapper _mapper;

            public GetBeneficiariesBySearchTextQueryHandler(IRepositoryManager repository, IMapper mapper)
            {
                _repository = repository;
                _mapper = mapper;
            }

            public async Task<BeneficiariesVm> Handle(GetBeneficiariesBySearchTextQuery query, CancellationToken cancellationToken)
            {
                var beneficiaries = _repository.Beneficiary.GetBeneficiariesBySearchText(query.SearchText)
                    .ProjectTo<BeneficiaryLookUpDto>(_mapper.ConfigurationProvider)
                    .OrderBy(b => b.Name)
                    .ToList();

                var vm = new BeneficiariesVm
                {
                    Beneficiaries = beneficiaries
                };

                return vm;
            }
        }

    }
}
