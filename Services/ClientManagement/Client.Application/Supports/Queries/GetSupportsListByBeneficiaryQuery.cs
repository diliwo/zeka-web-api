using AutoMapper;
using AutoMapper.QueryableExtensions;
using Client.Application.Common.Mappings;
using Client.Application.Common.Models;
using Client.Core.Interfaces;
using MediatR;

namespace Client.Application.Supports.Queries
{
    public class GetSupportsListByClientQuery : IRequest<PaginatedList<SupportDto>>
    {
        public int ClientId { get; set; }
        public string Filter { get; set; }
        public string SortOrder { get; set; }
        public int PageNumber { get; set; }
        public int PageSize { get; set; }


        public class GetSupportListByClientQueryHandler : IRequestHandler<GetSupportsListByClientQuery, PaginatedList<SupportDto>>
        {
            private readonly IRepositoryManager _repository;
            private readonly IMapper _mapper;

            public GetSupportListByClientQueryHandler(IRepositoryManager repository, IMapper mapper)
            {
                _repository = repository;
                _mapper = mapper;
            }

            public async Task<PaginatedList<SupportDto>> Handle(GetSupportsListByClientQuery request, CancellationToken cancellationToken)
            {
                //if (request.ClientId == null)
                //{
                //    throw new BadHttpRequestException("Client",request.ClientId);
                //}

                var supports = await _repository.Support.GetSupportsByClientId(request.ClientId)
                    .ProjectTo<SupportDto>(_mapper.ConfigurationProvider)
                    .OrderBy(s => s.StartDate)
                    .PaginatedListAsync(request.PageNumber, request.PageSize);

                return supports;
            }
        }
    }
}
