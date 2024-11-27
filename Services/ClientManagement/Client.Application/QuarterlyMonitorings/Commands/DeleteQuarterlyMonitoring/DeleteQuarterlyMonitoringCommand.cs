using Client.Application.Common.Exceptions;
using Client.Core.Entities;
using Client.Core.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Client.Application.QuarterlyMonitorings.Commands.DeleteQuarterlyMonitoring
{
    public  class DeleteQuarterlyMonitoringCommand : IRequest
    {        
        public int QMonitoringId { get; set; }

        public DeleteQuarterlyMonitoringCommand(int id)
        {
            QMonitoringId = id;
        }
        
    }
    public class DeleteQuarterlyMonitoringCommandHandler : IRequestHandler<DeleteQuarterlyMonitoringCommand>
    {
        private readonly IRepositoryManager _repository;
        public DeleteQuarterlyMonitoringCommandHandler(IRepositoryManager repository)
        {
            _repository = repository;
        }
        public async Task Handle(DeleteQuarterlyMonitoringCommand request, CancellationToken cancellationToken)
        {
            var quarterlyMonitoring = await _repository.QuarterlyMonitoring.GetQuarterlyMonitoringById(request.QMonitoringId)
                .SingleOrDefaultAsync(cancellationToken);
            if (quarterlyMonitoring == null)
            {
                throw new NotFoundException(nameof(QuarterlyMonitoring), request.QMonitoringId);
            }
            _repository.QuarterlyMonitoring.SoftDelete(request.QMonitoringId);
        }
    }
}
