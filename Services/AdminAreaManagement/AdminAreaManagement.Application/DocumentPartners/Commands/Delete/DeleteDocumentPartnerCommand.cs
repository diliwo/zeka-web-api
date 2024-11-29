using AdminAreaManagement.Core.Interfaces;
using MediatR;

namespace AdminAreaManagement.Application.DocumentPartners.Commands.Delete
{
    public class DeleteDocumentPartnerCommand :IRequest
    {
        public DeleteDocumentPartnerCommand(int id)
        {
            Id = id;
        }

        public DeleteDocumentPartnerCommand()
        {
        }

        public int Id { get; set; }
    }

    public class DeleteDocumentPartnerCommandHandler : IRequestHandler<DeleteDocumentPartnerCommand>
    {
        private readonly IRepositoryManager _repository;

        public DeleteDocumentPartnerCommandHandler(IRepositoryManager repository)
        {
            _repository = repository;
        }

        public Task Handle(DeleteDocumentPartnerCommand request, CancellationToken cancellationToken)
        {
            try
            {
                _repository.DocumentPartner.Delete(request.Id);
            }
            catch (Exception ex)
            {
                throw;
            }

            return Unit.Task;
        }
    }

}
