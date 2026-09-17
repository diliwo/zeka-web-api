using AdminAreaManagement.Application.Common.Exceptions;
using AdminAreaManagement.Core.Entities;
using AdminAreaManagement.Core.Interfaces;
using MediatR;
using System.Security.Cryptography;
using System.Text;

namespace AdminAreaManagement.Application.DocumentPartners.Commands.Persist
{
    [AdminAreaManagement.Application.Common.Authorization.RequiresTenantPermission("PartnerDocuments.Import")]
    public class PersistDocumentPartnerCommand : IRequest<DocumentFileOperationResult>
    {
        public Guid OperationId { get; set; }
        public int? DocumentId { get; set; }
        public int PartnerId { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string ContentType { get; set; }
        public byte[] ContentFile { get; set; }

        public class PersistDocumentPartnerCommandHandler : IRequestHandler<PersistDocumentPartnerCommand, DocumentFileOperationResult>
        {
            private readonly IRepositoryManager _repository;

            public PersistDocumentPartnerCommandHandler(IRepositoryManager repository)
            {
                _repository = repository;
            }

            public Task<DocumentFileOperationResult> Handle(PersistDocumentPartnerCommand request, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (request.OperationId == Guid.Empty)
                    throw new InvalidOperationException("A non-empty document operation identity is required.");
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

                    entity = _repository.DocumentPartner.Persist(entity, request.OperationId, RequestHash(request));
                    _repository.Save();
                }
                catch (Exception e)
                {
                    throw;
                }

                return Task.FromResult(new DocumentFileOperationResult(entity, deleteOperation: false));
            }

            private static string RequestHash(PersistDocumentPartnerCommand request)
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                Append(request.PartnerId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Append(request.Name);
                Append(request.Description);
                Append(request.ContentType);
                hash.AppendData(request.ContentFile ?? []);
                return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();

                void Append(string? value)
                {
                    var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
                    hash.AppendData(BitConverter.GetBytes(bytes.Length));
                    hash.AppendData(bytes);
                }
            }
        }
    }
}
