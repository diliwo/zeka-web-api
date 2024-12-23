using AdminAreaManagement.Application.Common.Exceptions;
using AdminAreaManagement.Core.Entities;
using AdminAreaManagement.Core.Interfaces;
using MediatR;

namespace AdminAreaManagement.Application.DocumentPartners.Commands.Persist
{
    public class PersistDocumentPartnerCommand : IRequest<int>
    {
        public int? DocumentId { get; set; }
        public int PartnerId { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string ContentType { get; set; }
        public byte[] ContentFile { get; set; }

        public class PersistDocumentPartnerCommandHandler : IRequestHandler<PersistDocumentPartnerCommand, int>
        {
            private readonly IRepositoryManager _repository;

            public PersistDocumentPartnerCommandHandler(IRepositoryManager repository)
            {
                _repository = repository;
            }

            public async Task<int> Handle(PersistDocumentPartnerCommand request, CancellationToken cancellationToken)
            {
               DocumentPartner entity;

               var foundedPartner = _repository.Partner.Get(request.PartnerId);
               if (foundedPartner == null)
               {
                   throw new NotFoundException(nameof(foundedPartner), request.PartnerId);
               }

                try
                {
                    entity = new DocumentPartner()
                    {
                        Partner = foundedPartner,
                        Name = request.Name,
                        Description = request.Description,
                        ContentFile = request.ContentFile,
                        ContentType = request.ContentType,
                    };

                    _repository.DocumentPartner.Persist(entity);
                    _repository.Save();
                }
                catch (Exception e)
                {
                    throw;
                }

                return entity.Id;
            }
        }
    }
}
