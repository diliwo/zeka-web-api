using AdminAreaManagement.Core.Interfaces;
using AutoMapper;
using AutoMapper.QueryableExtensions;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace AdminAreaManagement.Application.DocumentPartners.Queries.GetDocumentsList
{
    public class GetDocumentsJobPartnerListQuery : IRequest<DocumentPartnersListDto>
    {
        public int PartnerId { get; set; }
        public int JobId { get; set; }

        public class  GetDocumentsJobPartnerListQueryHandler : IRequestHandler< GetDocumentsJobPartnerListQuery, DocumentPartnersListDto>
        {
            private readonly IRepositoryManager _repository;
            private readonly IMapper _mapper;

            public GetDocumentsJobPartnerListQueryHandler(IRepositoryManager repository, IMapper mapper)
            {
                _repository = repository;
                _mapper = mapper;
            }

            public async Task<DocumentPartnersListDto> Handle(GetDocumentsJobPartnerListQuery query, CancellationToken cancellationToken)
            {
                var documents = await _repository.DocumentPartner.getDocumentsByJobIAndPartnerId(query.PartnerId, query.JobId)
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