﻿using DiliBeneficiary.Application.Common.Exceptions;
using DiliBeneficiary.Core.Entities;
using DiliBeneficiary.Core.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace DiliBeneficiary.Application.MonitoringActions.Commands.DeleteAction
{
    public class DeleteMonitoringActionCommand : IRequest
    {
        public int ActionId { get; set; }

        public DeleteMonitoringActionCommand(int actionId)
        {
            ActionId = actionId;
        }
    }

    public class DeleteMonitoringActionCommandHandler : IRequestHandler<DeleteMonitoringActionCommand>
    {
        private IMonitoringActionRepository _monitoringActionRepository;

        public DeleteMonitoringActionCommandHandler(IMonitoringActionRepository monitoringActionRepository)
        {
            _monitoringActionRepository = monitoringActionRepository;
        }

        public async Task<Unit> Handle(DeleteMonitoringActionCommand request, CancellationToken cancellationToken)
        {
            var action = await _monitoringActionRepository.GetMonitoringActionById(request.ActionId).SingleOrDefaultAsync(cancellationToken);
            if (action == null)
            {
                throw new NotFoundException(nameof(MonitoringAction), request.ActionId);
            }
            _monitoringActionRepository.SoftDelete(request.ActionId);
            return Unit.Value;
        }
    }
}