using AdminAreaManagement.Core.Interfaces;
using AutoMapper;
using AutoMapper.QueryableExtensions;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace AdminAreaManagement.Application.DocumentPartners.Queries.GetDocumentsList
{
    public class GetDocumentsPartnerListQuery : IRequest<DocumentPartnersListDto>
    {
        public class  GetDocumentsPartnerListQueryHandler : IRequestHandler< GetDocumentsPartnerListQuery, DocumentPartnersListDto>
        {
            private readonly IRepositoryManager _repository;
            private readonly IMapper _mapper;

            public GetDocumentsPartnerListQueryHandler(IRepositoryManager repository, IMapper mapper)
            {
                _repository = repository;
                _mapper = mapper;
            }

            public async Task<DocumentPartnersListDto> Handle(GetDocumentsPartnerListQuery query, CancellationToken cancellationToken)
            {
                var documents = await _repository.DocumentPartner.GetDocuments()
                    .AsNoTracking()
                    .ProjectTo<DocumentPartnerDto>(_mapper.ConfigurationProvider)
                    .OrderBy(b => b.DocumentPartnerId)
                    .ToListAsync(cancellationToken);

                var vm = new DocumentPartnersListDto()
                {
                    DocumentPartners = documents
                };

                return vm;
            }
        }
    }
}