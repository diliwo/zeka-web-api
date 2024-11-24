﻿using Client.Core.Interfaces;
using MediatR.Pipeline;
using Microsoft.Extensions.Logging;

namespace Client.Application.Common.Behaviours
{
    public class LoggingBehaviour<TRequest> : IRequestPreProcessor<TRequest>
    {
        private readonly ILogger _logger;
        private readonly ICurrentUserService _currentUserService;

        public LoggingBehaviour(ILogger<TRequest> logger, ICurrentUserService currentUserService)
        {
            _logger = logger;
            _currentUserService = currentUserService;
        }

        public async Task Process(TRequest request, CancellationToken cancellationToken)
        {
            var requestName = typeof(TRequest).Name;
            var userId = _currentUserService.UserId ?? string.Empty;
            string userName = string.Empty;

            if (!string.IsNullOrEmpty(userId))
            {
                userName =  _currentUserService.Username;
            }

            _logger.LogInformation("Isp Request: {FirstName} {@UserId} {@UserName} {@Request}",
                requestName, userId, userName, request);
        }
    }
}
