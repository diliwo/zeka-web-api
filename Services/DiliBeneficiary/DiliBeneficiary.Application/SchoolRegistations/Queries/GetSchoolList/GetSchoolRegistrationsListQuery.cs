using AutoMapper;
using DiliBeneficiary.Application.Common.Exceptions;
using DiliBeneficiary.Application.Common.Mappings;
using DiliBeneficiary.Application.Common.Models;
using DiliBeneficiary.Application.SchoolRegistations.Common;
using DiliBeneficiary.Core.Interfaces;
using MediatR;

namespace DiliBeneficiary.Application.SchoolRegistations.Queries.GetSchoolList
{
    public class GetSchoolRegistrationsListQuery : IRequest<PaginatedList<SchoolRegistrationDto>>
    {
        public int beneficiaryId { get; set; }
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
                if (request.beneficiaryId == null)
                {
                    throw new NotFoundException("Beneficiary",request.beneficiaryId);
                }

                var resgistrations = _sort.ApplySort(
                    _repository.SchoolRegistration
                        .GetResgistrationsByBeneficiaryId(request.beneficiaryId, request.Filter)
                        .ProjectTo<SchoolRegistrationDto>(_mapper.ConfigurationProvider), request.Orderby);

                return await resgistrations.PaginatedListAsync(request.PageNumber, request.PageSize); ;
            }
        }
    }
}