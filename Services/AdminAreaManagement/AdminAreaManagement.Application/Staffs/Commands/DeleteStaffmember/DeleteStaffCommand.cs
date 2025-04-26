using AdminAreaManagement.Application.Common.Exceptions;
using AdminAreaManagement.Core.Entities;
using AdminAreaManagement.Core.Interfaces;
using MediatR;

namespace AdminAreaManagement.Application.Staffs.Commands.DeleteStaff
{
    public class DeleteStaffMemberCommand : IRequest
    {
        public int Id { get; set; }

        public class DeleteStaffMemberCommandHandler : IRequestHandler<DeleteStaffMemberCommand>
        {
            private readonly IRepositoryManager _repository;

            public DeleteStaffMemberCommandHandler(IRepositoryManager repository)
            {
                _repository = repository;
            }

            public async Task Handle(DeleteStaffMemberCommand request, CancellationToken cancellationToken)
            {
                var foundedStaffMember = _repository.StaffMember.Get(request.Id);

                if (foundedStaffMember != null)
                {
                    if (foundedStaffMember.Softdelete)
                    {
                        foundedStaffMember.Softdelete = false;
                    }
                    else
                    {
                        foundedStaffMember.Softdelete = true;
                    }

                    _repository.StaffMember.SoftDelete(foundedStaffMember);

                    _repository.Save();
                }
                else
                {
                    throw new NotFoundException(nameof(StaffMember), request.Id);
                }
            }
        }
    }
}
