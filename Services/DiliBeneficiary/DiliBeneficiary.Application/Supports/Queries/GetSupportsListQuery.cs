using AutoMapper;
using MediatR;

namespace DiliBeneficiary.Application.Supports.Queries
{
    public class GetSupportsListQuery : IRequest<SupportsListVm>
    {
        public class GetSupportsListQueryHandler : IRequestHandler<GetSupportsListQuery, SupportsListVm>
        {
            private readonly IRepositoryManager _repository;
            private readonly IMapper _mapper;

            public GetSupportsListQueryHandler(IRepositoryManager repository, IMapper mapper)
            {
                _repository = repository;
                _mapper = mapper;
            }

            public async Task<SupportsListVm> Handle(GetSupportsListQuery request, CancellationToken cancellationToken)
            {
                var supports = await _repository.Support.GetSupports()
                    .ProjectTo<SupportDto>(_mapper.ConfigurationProvider)
                    .OrderBy(s => s.StartDate)
                    .ToListAsync(cancellationToken);

                var vm = new SupportsListVm
                {
                    Supports = supports
                };

                return vm;
            }
        }
    }
}
