using AutoMapper;
using AutoMapper.QueryableExtensions;
using Client.Application.Bilans.Common;
using Client.Core.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Client.Application.Bilans.Queries.GetLastBilan
{
    public class GetLastBilanQuery : IRequest<BilanDto>
    {
        public int ClientId { get; set; }

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
                var vm = await _repository.Bilan.GetBilans(request.ClientId)
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
