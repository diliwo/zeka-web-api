using AutoMapper;
using AutoMapper.QueryableExtensions;
using Client.Core.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Client.Application.Clients.Queries.GetClientDetail
{
    public class GetClientDetailQuery : IRequest<ClientVm>
    {
        public string Niss { get; set; }

        public class GetClientDetailQueryHandler : IRequestHandler<GetClientDetailQuery, ClientVm>
        {
            private readonly IRepositoryManager _repository;
            private readonly IMapper _mapper;

            public GetClientDetailQueryHandler(IRepositoryManager repository, IMapper mapper)
            {
                _repository = repository;
                _mapper = mapper;
            }

            public async Task<ClientVm> Handle(GetClientDetailQuery request, CancellationToken cancellationToken)
            {
                var vm = await _repository.Client.GetClients()
                    //.Include(b =>b.Candidacies)
                    .Include(b => b.SchoolRegistrations)
                    .Where(b => b.Niss == request.Niss)
                    .AsNoTracking()
                    .ProjectTo<ClientVm>(_mapper.ConfigurationProvider)
                    .SingleOrDefaultAsync(cancellationToken);
                return vm;
            }
        }
    }
}
