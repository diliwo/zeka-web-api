using AutoMapper;
using AutoMapper.QueryableExtensions;
using DiliBeneficiary.Application.Bilans.Common;
using DiliBeneficiary.Core.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace DiliBeneficiary.Application.Bilans.Queries.GetCurrentBilan
{
    public class GetCurrentBilanQuery : IRequest<BilanDto>
    {
        public int BeneficiaryId { get; set; }

        public class GetBeneficiaryDetailQueryHandler : IRequestHandler<GetCurrentBilanQuery, BilanDto>
        {
            private readonly IRepositoryManager _repository;
            private readonly IMapper _mapper;

            public GetBeneficiaryDetailQueryHandler(IRepositoryManager repository, IMapper mapper)
            {
                _repository = repository;
                _mapper = mapper;
            }

            public async Task<BilanDto> Handle(GetCurrentBilanQuery request, CancellationToken cancellationToken)
            {
                var vm = await _repository.Bilan.GetBilans(request.BeneficiaryId)
                    .Where(b => b.IsFinalized != true)
                    .AsNoTracking()
                    .ProjectTo<BilanDto>(_mapper.ConfigurationProvider)
                    .SingleOrDefaultAsync(cancellationToken);

                return vm;
            }
        }
    }
}
