using Client.Application.Common.Exceptions;
using Client.Core.Entities;
using Client.Core.Interfaces;
using MediatR;

namespace Client.Application.Bilans.Commands.DeleteBilan
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
                    throw new NotFoundException(nameof(Training), request.BilanId);
                }

                return Unit.Value;
            }
        }

    }
}