using AutoMapper;
using AutoMapper.QueryableExtensions;
using ClientManagement.Core.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace ClientManagement.Application.Supports.Queries
{
    public class GetClientsListQuery : IRequest<SupportsListVm>
    {
        public class GetSupportsListQueryHandler : IRequestHandler<GetClientsListQuery, SupportsListVm>
        {
            private readonly IRepositoryManager _repository;
            private readonly IMapper _mapper;

            public GetSupportsListQueryHandler(IRepositoryManager repository, IMapper mapper)
            {
                _repository = repository;
                _mapper = mapper;
            }

            public async Task<SupportsListVm> Handle(GetClientsListQuery request, CancellationToken cancellationToken)
            {
                var supports = await _repository.Support.GetSupports()
                    .ProjectTo<SupportDto>(_mapper.ConfigurationProvider)
                    .OrderBy(s => s.StartDate)
                    .ToListAsync(cancellationToken);

                var vm = new SupportsListVm
                {
                    Tracks = supports
                };

                return vm;
            }
        }
    }
}
