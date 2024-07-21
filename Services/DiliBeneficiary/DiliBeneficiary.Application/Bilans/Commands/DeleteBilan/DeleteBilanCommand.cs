using DiliBeneficiary.Application.Common.Exceptions;
using DiliBeneficiary.Core.Entities;
using DiliBeneficiary.Core.Interfaces;
using MediatR;

namespace DiliBeneficiary.Application.Bilans.Commands.DeleteBilan
{
    public class DeleteBilanCommand : IRequest<Unit>
    {
        public int BilanId { get; set; }

        public class DeleteBilanCommandHandler : IRequestHandler<DeleteBilanCommand, Unit>
        {
            private readonly IRepositoryManager _repository;

            public DeleteBilanCommandHandler(IRepositoryManager repository)
            {
                _repository = repository;
            }
            public async Task<Unit> Handle(DeleteBilanCommand request, CancellationToken cancellationToken)
            {
                var entity = _repository.Bilan.GetBilanById(request.BilanId);

                if (entity != null)
                {
                    _repository.Bilan.SoftDelete(entity);
                }
                else
                {
                    throw new NotFoundException(nameof(Formation), request.BilanId);
                }

                return Unit.Value;
            }
        }

    }
}