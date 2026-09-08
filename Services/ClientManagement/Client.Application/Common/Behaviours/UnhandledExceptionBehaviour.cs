using MediatR;
using Microsoft.Extensions.Logging;

namespace ClientManagement.Application.Common.Behaviours
{
    public class UnhandledExceptionBehaviour<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
    {
        private readonly ILogger<TRequest> _logger;

        public UnhandledExceptionBehaviour(ILogger<TRequest> logger)
        {
            _logger = logger;
        }

        public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
        {
            try
            {
                return await next();
            }
            catch (Exception ex)
            {
                var requestName = typeof(TRequest).Name;

                // Exception messages and request payloads may contain personal identifiers.
                _logger.LogError("Zeka Request: Unhandled {ExceptionType} for Request {Name}",
                    ex.GetType().Name, requestName);

                throw;
            }
        }
    }
}
