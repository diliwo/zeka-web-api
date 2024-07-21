using DiliBeneficiary.Application.Common.Exceptions;
using DiliBeneficiary.Core.Entities;
using DiliBeneficiary.Core.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace DiliBeneficiary.Application.QuarterlyMonitorings.Commands.UpdateQuarterlyMonitoring
{
    public class UpdateQuarterlyMonitoringCommand : IRequest<int>
    {
        public int QMonitoringId { get; set; }
        public int BeneficiaryId { get; set; }
        public int ReferentId { get; set; }
        public int MonitoringActionId { get; set; }
        public DateTime ActionDate { get; set; }
        public string ActionComment { get; set; }

    }

    public class UpdateQuarterlyMonitoringCommandHandler : IRequestHandler<UpdateQuarterlyMonitoringCommand, int>
    {
        private readonly IMonitoringActionRepository _monitoringActionRepository;
        private readonly IRepositoryManager _repository;

        public UpdateQuarterlyMonitoringCommandHandler(
            IRepositoryManager repository,
            IMonitoringActionRepository monitoringActionRepository)
        {
            _repository = repository;
            _monitoringActionRepository = monitoringActionRepository;
        }


        public async Task<int> Handle(UpdateQuarterlyMonitoringCommand request, CancellationToken cancellationToken)
        {
            var qMonitoring = await _repository.QuarterlyMonitoring.GetQuarterlyMonitoringById(request.QMonitoringId)
                .SingleOrDefaultAsync(cancellationToken);
            if (qMonitoring == null)
            {
                throw new NotFoundException(nameof(QuarterlyMonitoring), request.QMonitoringId);
            }

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

            var monitoringAction = await _monitoringActionRepository.GetMonitoringActionById(request.MonitoringActionId)
                .SingleOrDefaultAsync(cancellationToken);
            if (monitoringAction == null)
            {
                throw new NotFoundException(nameof(MonitoringAction), request.MonitoringActionId);
            }

            qMonitoring.Beneficiary = beneficiary;
            qMonitoring.BeneficiaryId = request.BeneficiaryId;
            qMonitoring.Referent = referent;
            qMonitoring.ReferentId = request.ReferentId;
            qMonitoring.MonitoringAction = monitoringAction;
            qMonitoring.MonitoringActionId = request.MonitoringActionId;
            qMonitoring.ActionDate = request.ActionDate.ToLocalTime();
            qMonitoring.ActionComment = request.ActionComment;
            return await _repository.QuarterlyMonitoring.Persist(qMonitoring);
        }
    }
}
