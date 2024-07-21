using AutoMapper;
using AutoMapper.QueryableExtensions;
using DiliBeneficiary.Core.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace DiliBeneficiary.Application.Beneficiaries.Queries.GetBeneficiaries
{
    public class GetBeneficiariesQuery : IRequest<BeneficiariesVm>
    {
        public class GetBeneficiariesQueryHandler : IRequestHandler<GetBeneficiariesQuery, BeneficiariesVm>
        {
            private readonly IRepositoryManager _repository;
            private readonly IMapper _mapper;

            public GetBeneficiariesQueryHandler(IRepositoryManager repository, IMapper mapper)
            {
                _repository = repository;
                _mapper = mapper;
            }

            public async Task<BeneficiariesVm> Handle(GetBeneficiariesQuery query, CancellationToken cancellationToken)
            {
                var beneficiaries = await _repository.Beneficiary.GetBeneficiaries()
                    .ProjectTo<BeneficiaryLookUpDto>(_mapper.ConfigurationProvider)
                    .OrderBy(b => b.Name)
                    .ToListAsync(cancellationToken);

                var vm = new BeneficiariesVm
                {
                    Beneficiaries = beneficiaries
                };

                return vm;
            }
        }

    }
}
