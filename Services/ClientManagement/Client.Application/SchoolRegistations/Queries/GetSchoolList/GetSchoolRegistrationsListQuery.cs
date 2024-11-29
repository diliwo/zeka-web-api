using AutoMapper;
using AutoMapper.QueryableExtensions;
using ClientManagement.Application.Common.Exceptions;
using ClientManagement.Application.Common.Mappings;
using ClientManagement.Application.Common.Models;
using ClientManagement.Application.SchoolRegistations.Common;
using ClientManagement.Core.Interfaces;
using MediatR;

namespace ClientManagement.Application.SchoolRegistations.Queries.GetSchoolList
{
    public class GetSchoolRegistrationsListQuery : IRequest<PaginatedList<SchoolRegistrationDto>>
    {
        public int ClientId { get; set; }
        public string Filter { get; set; }
        public string Orderby { get; set; }
        public int PageNumber { get; set; }
        public int PageSize { get; set; }

        public class GetSchoolRegistrationsListQueryHandler : IRequestHandler<GetSchoolRegistrationsListQuery, PaginatedList<SchoolRegistrationDto>>
        {
            private readonly IRepositoryManager _repository;
            private ISortHelper<SchoolRegistrationDto> _sort;
            private readonly IMapper _mapper;

            public GetSchoolRegistrationsListQueryHandler(IRepositoryManager repository, IMapper mapper, ISortHelper<SchoolRegistrationDto> sort)
            {
                _repository = repository;
                _sort = sort;
                _mapper = mapper;
            }

            public async Task<PaginatedList<SchoolRegistrationDto>> Handle(GetSchoolRegistrationsListQuery request, CancellationToken cancellationToken)
            {
                if (request.ClientId == null)
                {
                    throw new NotFoundException("Client",request.ClientId);
                }

                var resgistrations = _sort.ApplySort(
                    _repository.SchoolRegistration
                        .GetResgistrationsByClientId(request.ClientId, request.Filter)
                        .ProjectTo<SchoolRegistrationDto>(_mapper.ConfigurationProvider), request.Orderby);

                return await resgistrations.PaginatedListAsync(request.PageNumber, request.PageSize); ;
            }
        }
    }
}