using AutoMapper;
using AutoMapper.QueryableExtensions;
using Client.Application.Bilans.Common;
using Client.Core.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Client.Application.Bilans.Queries.GetCurrentBilan
{
    public class GetCurrentBilanQuery : IRequest<BilanDto>
    {
        public int ClientId { get; set; }

        public class GetClientDetailQueryHandler : IRequestHandler<GetCurrentBilanQuery, BilanDto>
        {
            private readonly IRepositoryManager _repository;
            private readonly IMapper _mapper;

            public GetClientDetailQueryHandler(IRepositoryManager repository, IMapper mapper)
            {
                _repository = repository;
                _mapper = mapper;
            }

            public async Task<BilanDto> Handle(GetCurrentBilanQuery request, CancellationToken cancellationToken)
            {
                var vm = await _repository.Bilan.GetBilans(request.ClientId)
                    .Where(b => b.IsFinalized != true)
                    .AsNoTracking()
                    .ProjectTo<BilanDto>(_mapper.ConfigurationProvider)
                    .SingleOrDefaultAsync(cancellationToken);

                return vm;
            }
        }
    }
}
