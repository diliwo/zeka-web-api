using DiliBeneficiary.Application.Common.Exceptions;
using DiliBeneficiary.Core.Entities;
using DiliBeneficiary.Core.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace DiliBeneficiary.Application.QuarterlyMonitorings.Commands.CreateQuarterlyMonitoring
{
    public class CreateQuarterlyMonitoringCommand : IRequest<int>
    {
        public int BeneficiaryId { get; set; }
        public int ReferentId { get; set; }
        public int MonitoringActionId { get; set; }
        public DateTime ActionDate { get; set; } = DateTime.Now;
        public string ActionComment { get; set; } = "";
    }

    public class CreateQuarterlyMonitoringCommandHandler : IRequestHandler<CreateQuarterlyMonitoringCommand, int>
    {
        private readonly IRepositoryManager _repository;
        private readonly IMonitoringActionRepository _monitoringActionRepository;
        public CreateQuarterlyMonitoringCommandHandler(IRepositoryManager repository, IMonitoringActionRepository monitoringActionRepository)
        {
            _repository = repository;
            _monitoringActionRepository = monitoringActionRepository;
        }
        

        public async Task<int> Handle(CreateQuarterlyMonitoringCommand request, CancellationToken cancellationToken)
        {
            var beneficiary = _repository.Beneficiary.Get(request.BeneficiaryId);
            if (beneficiary == null)
            {
                throw new NotFoundException(nameof(Beneficiary), request.BeneficiaryId);
            }
            var referent = _repository.Referent.Get(request.ReferentId);
            if (referent == null)
            {
                throw new NotFoundException(nameof(Referent), request.ReferentId);
            }
            var monitoringAction = await _monitoringActionRepository.GetMonitoringActionById(request.MonitoringActionId).SingleOrDefaultAsync(cancellationToken);
            if (monitoringAction == null)
            {
                throw new NotFoundException(nameof(MonitoringAction), request.MonitoringActionId);
            }
            return await _repository.QuarterlyMonitoring.Persist(new QuarterlyMonitoring(request.BeneficiaryId, request.ReferentId, request.MonitoringActionId, request.ActionDate.ToLocalTime(), request.ActionComment));
        }
    }
}
