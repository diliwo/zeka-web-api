using MediatR;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AdminAreaManagement.Application.Common.Models;
using AdminAreaManagement.Core.Interfaces;
using AutoMapper;
using AutoMapper.QueryableExtensions;
using Grpc.Core;

namespace AdminAreaManagement.Application.Staffs.Queries
{
    public class GetSocialWorkerQuery : IRequest<Adminarea.Grpc.Protos.SocialWorker>
    {
        public int SocialWorkerId { get; set; }

        public class GetSocialWorkerQueryHandler : IRequestHandler<GetSocialWorkerQuery, Adminarea.Grpc.Protos.SocialWorker>
        {
            private readonly IRepositoryManager _repository;
            private readonly IMapper _mapper;

            public GetSocialWorkerQueryHandler(
                IRepositoryManager repository,
                IMapper mapper
            )
            {
                _repository = repository;
                _mapper = mapper;
            }

            public async Task<Adminarea.Grpc.Protos.SocialWorker> Handle(GetSocialWorkerQuery request, CancellationToken cancellationToken)
            {
                var staffMember = _repository.StaffMember.Get(request.SocialWorkerId);

                if (staffMember is null)
                {
                    throw new RpcException(new Status(StatusCode.NotFound,
                        $"Social worker with the id = {request.SocialWorkerId}"));
                }

                var socialWorker = _mapper.Map<Adminarea.Grpc.Protos.SocialWorker>(staffMember);

                return socialWorker;
            }
        }
    }
}
