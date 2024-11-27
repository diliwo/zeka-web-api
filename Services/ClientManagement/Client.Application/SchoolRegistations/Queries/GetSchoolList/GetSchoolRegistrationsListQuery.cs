using AutoMapper;
using AutoMapper.QueryableExtensions;
using Client.Application.Common.Exceptions;
using Client.Application.Common.Mappings;
using Client.Application.Common.Models;
using Client.Application.SchoolRegistations.Common;
using Client.Core.Interfaces;
using MediatR;

namespace Client.Application.SchoolRegistations.Queries.GetSchoolList
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