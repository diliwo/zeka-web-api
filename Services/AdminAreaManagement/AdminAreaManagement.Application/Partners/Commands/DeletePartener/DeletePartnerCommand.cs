using AdminAreaManagement.Application.Common.Exceptions;
using AdminAreaManagement.Core.Entities;
using AdminAreaManagement.Core.Interfaces;
using MediatR;

namespace AdminAreaManagement.Application.Partners.Commands.DeletePartener
{
    public class DeletePartnerCommand : IRequest
    {
        public int Id { get; set; }

        public class DeletePartnerCommandHandler : IRequestHandler<DeletePartnerCommand>
        {
            private readonly IRepositoryManager _repository;

            public DeletePartnerCommandHandler(IRepositoryManager partnerRepository)
            {
                _repository = partnerRepository;
            }

            public async Task Handle(DeletePartnerCommand request, CancellationToken cancellationToken)
            {
                var foundedPartner = _repository.Partner.Get(request.Id);


                if (foundedPartner != null)
                {
                    if (foundedPartner.Softdelete)
                    {
                        foundedPartner.Softdelete = false;
                    }
                    else
                    {
                        foundedPartner.Softdelete = true;
                    }

                    _repository.Partner.SoftDelete(foundedPartner);

                    _repository.Save();
                }
                else
                {
                    throw new NotFoundException(nameof(StaffMember), request.Id);
                }
            }
        }
    }
}
