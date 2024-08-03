using AutoMapper;
using AutoMapper.QueryableExtensions;
using DiliBeneficiary.Core.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace DiliBeneficiary.Application.Beneficiaries.Queries.GetBeneficiaryDetail
{
    public class GetBeneficiaryDetailQuery : IRequest<BeneficiaryVm>
    {
        public string Niss { get; set; }

        public class GetBeneficiaryDetailQueryHandler : IRequestHandler<GetBeneficiaryDetailQuery, BeneficiaryVm>
        {
            private readonly IRepositoryManager _repository;
            private readonly IMapper _mapper;

            public GetBeneficiaryDetailQueryHandler(IRepositoryManager repository, IMapper mapper)
            {
                _repository = repository;
                _mapper = mapper;
            }

            public async Task<BeneficiaryVm> Handle(GetBeneficiaryDetailQuery request, CancellationToken cancellationToken)
            {
                var vm = await _repository.Beneficiary.GetBeneficiaries()
                    //.Include(b =>b.Candidacies)
                    .Include(b => b.SchoolRegistrations)
                    .Where(b => b.Niss == request.Niss)
                    .AsNoTracking()
                    .ProjectTo<BeneficiaryVm>(_mapper.ConfigurationProvider)
                    .SingleOrDefaultAsync(cancellationToken);
                return vm;
            }
        }
    }
}
