using AdminAreaManagement.Core.Interfaces;
using MediatR;

namespace AdminAreaManagement.Application.DocumentPartners.Commands.Delete
{
    [AdminAreaManagement.Application.Common.Authorization.RequiresTenantPermission("PartnerDocuments.Delete")]
    public class DeleteDocumentPartnerCommand : IRequest<DocumentFileOperationResult>
    {
        public DeleteDocumentPartnerCommand(int id, Guid operationId)
        {
            Id = id;
            OperationId = operationId;
        }

        public DeleteDocumentPartnerCommand()
        {
        }

        public int Id { get; set; }
        public Guid OperationId { get; set; }
    }

    public class DeleteDocumentPartnerCommandHandler : IRequestHandler<DeleteDocumentPartnerCommand, DocumentFileOperationResult>
    {
        private readonly IRepositoryManager _repository;

        public DeleteDocumentPartnerCommandHandler(IRepositoryManager repository)
        {
            _repository = repository;
        }

        public Task<DocumentFileOperationResult> Handle(DeleteDocumentPartnerCommand request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.OperationId == Guid.Empty)
                throw new InvalidOperationException("A non-empty document operation identity is required.");
            try
            {
                var document = _repository.DocumentPartner.Delete(request.Id, request.OperationId);
                return Task.FromResult(new DocumentFileOperationResult(document, deleteOperation: true));
            }
            catch (Exception ex)
            {
                throw;
            }

        }
    }

}
