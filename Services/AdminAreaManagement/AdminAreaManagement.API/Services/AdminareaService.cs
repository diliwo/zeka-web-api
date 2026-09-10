using Adminarea.Grpc.Protos;
using AdminAreaManagement.Application.Staffs.Queries;
using AdminAreaManagement.Core.Interfaces;
using Grpc.Core;
using MediatR;
using SocialWorker = Adminarea.Grpc.Protos.SocialWorker;

namespace AdminAreaManagement.API.Services
{
    public class AdminareaService : AdminareaProtoService.AdminareaProtoServiceBase
    {
        private readonly IMediator _mediator;
        private readonly ILogger<AdminareaService> _logger;
        private readonly IRepositoryManager _repository;

        public AdminareaService(ILogger<AdminareaService> logger, IRepositoryManager repository, IMediator mediator)
        {
            _logger = logger;
            _mediator = mediator;
            _repository = repository;
        }

        public override async Task<SocialWorker> GetSocialWorker(GetSocialWorkerRequest request, ServerCallContext context)
        {
            var socialWorkerRequest = new GetSocialWorkerQuery() { SocialWorkerId = request.SocialWorkerId };

            var result = await _mediator.Send(socialWorkerRequest);
            _logger.LogInformation($"Social worker with id: {request.SocialWorkerId} has been found");

            return new SocialWorker { Id = result.Id, FirstName = result.FirstName, LastName = result.LastName, UserName = result.UserName, TeamName = result.TeamName, TeamAcronym = result.TeamAcronym };
        }
    }
}
