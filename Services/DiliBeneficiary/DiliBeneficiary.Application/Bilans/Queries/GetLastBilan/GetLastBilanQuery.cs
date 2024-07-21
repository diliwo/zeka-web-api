using AutoMapper;
using AutoMapper.QueryableExtensions;
using DiliBeneficiary.Application.Bilans.Common;
using DiliBeneficiary.Core.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace DiliBeneficiary.Application.Bilans.Queries.GetLastBilan
{
    public class GetLastBilanQuery : IRequest<BilanDto>
    {
        public int BeneficiaryId { get; set; }

        public class GetLastBilanHandler : IRequestHandler<GetLastBilanQuery, BilanDto>
        {
            private readonly IRepositoryManager _repository;
            private readonly IMapper _mapper;

            public GetLastBilanHandler(IRepositoryManager repository, IMapper mapper)
            {
                _repository = repository;
                _mapper = mapper;
            }

            public async Task<BilanDto> Handle(GetLastBilanQuery request, CancellationToken cancellationToken)
            {
                var vm = await _repository.Bilan.GetBilans(request.BeneficiaryId)
                    .Where(b => b.Softdelete != true && b.IsFinalized == true)
                    .OrderBy(b => b.Id)
                    .AsNoTracking()
                    .ProjectTo<BilanDto>(_mapper.ConfigurationProvider)
                    .LastOrDefaultAsync(cancellationToken);
                return vm;
            }
        }
    }
}
