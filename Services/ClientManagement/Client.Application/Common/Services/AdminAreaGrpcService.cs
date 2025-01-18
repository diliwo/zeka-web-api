
using Adminarea.Grpc.Protos;
using AutoMapper.Configuration.Conventions;

namespace ClientManagement.Application.Common.Services
{
    public class AdminAreaGrpcService 
    {
        private readonly AdminareaProtoService.AdminareaProtoServiceClient _adminareaProtoServiceClient;

        public AdminAreaGrpcService(AdminareaProtoService.AdminareaProtoServiceClient adminareaProtoServiceClient)
        {
            _adminareaProtoServiceClient = adminareaProtoServiceClient;
        }

        public async Task<SocialWorker> GetSocialWorkerAsync(int socialWorkerId)
        {
            var socialWorkerRequest = new GetSocialWorkerRequest { SocialWorkerId = socialWorkerId };
            return await _adminareaProtoServiceClient.GetSocialWorkerAsync(socialWorkerRequest);
        }
    }
}
