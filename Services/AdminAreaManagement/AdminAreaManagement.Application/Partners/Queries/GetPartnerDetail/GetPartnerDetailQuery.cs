using AdminAreaManagement.Application.Partners.Queries.Common;
using AdminAreaManagement.Core.Interfaces;
using AutoMapper;
using AutoMapper.QueryableExtensions;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace AdminAreaManagement.Application.Partners.Queries.GetPartnerDetail
{
    public class GetPartnerDetailQuery : IRequest<PartnerDto>
    {
        public int Id { get; set; }

        public class GetPartnerDetailQueryHandler : IRequestHandler<GetPartnerDetailQuery, PartnerDto>
        {
            private readonly IRepositoryManager _repository;
            private readonly IMapper _mapper;

            public GetPartnerDetailQueryHandler(IRepositoryManager repository, IMapper mapper)
            {
                _repository = repository;
                _mapper = mapper;
            }

            public async Task<PartnerDto> Handle(GetPartnerDetailQuery request, CancellationToken cancellationToken)
            {
                var vm = await _repository.Partner.GetPartners()
                    .Where(p => p.Id == request.Id && p.Softdelete != true)
                    .AsNoTracking()
                    .ProjectTo<PartnerDto>(_mapper.ConfigurationProvider)
                    .SingleOrDefaultAsync(cancellationToken);

                return vm;
            }
        }
    }
}
