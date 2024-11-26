﻿using AutoMapper;
using AutoMapper.QueryableExtensions;
using Client.Core.Interfaces;
using MediatR;

namespace Client.Application.Clients.Queries.GetClients
{
    public class GetClientsBySearchTextQuery : IRequest<ClientsVm>
    {
        public string SearchText{ get; set; }

        public class GetClientsBySearchTextQueryHandler : IRequestHandler<GetClientsBySearchTextQuery, ClientsVm>
        {
            private readonly IRepositoryManager _repository;
            private readonly IMapper _mapper;

            public GetClientsBySearchTextQueryHandler(IRepositoryManager repository, IMapper mapper)
            {
                _repository = repository;
                _mapper = mapper;
            }

            public async Task<ClientsVm> Handle(GetClientsBySearchTextQuery query, CancellationToken cancellationToken)
            {
                var Clients = _repository.Client.GetClientsBySearchText(query.SearchText)
                    .ProjectTo<ClientLookUpDto>(_mapper.ConfigurationProvider)
                    .OrderBy(b => b.Name)
                    .ToList();

                var vm = new ClientsVm
                {
                    Clients = Clients
                };

                return vm;
            }
        }

    }
}