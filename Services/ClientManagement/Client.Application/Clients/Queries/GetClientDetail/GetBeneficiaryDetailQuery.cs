using AutoMapper;
using AutoMapper.QueryableExtensions;
using ClientManagement.Core.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace ClientManagement.Application.Clients.Queries.GetClientDetail
{
    public class GetClientDetailQuery : IRequest<ClientDto>
    {
        public string Niss { get; set; }

        public class GetClientDetailQueryHandler : IRequestHandler<GetClientDetailQuery, ClientDto>
        {
            private readonly IRepositoryManager _repository;
            private readonly IMapper _mapper;

            public GetClientDetailQueryHandler(IRepositoryManager repository, IMapper mapper)
            {
                _repository = repository;
                _mapper = mapper;
            }

            public async Task<ClientDto> Handle(GetClientDetailQuery request, CancellationToken cancellationToken)
            {
                var vm = await _repository.Client.GetClients()
                    //.Include(b =>b.Candidacies)
                    .Include(b => b.SchoolRegistrations)
                    .Where(b => b.Niss == request.Niss)
                    .AsNoTracking()
                    .ProjectTo<ClientDto>(_mapper.ConfigurationProvider)
                    .SingleOrDefaultAsync(cancellationToken);
                return vm;
            }
        }
    }
}
