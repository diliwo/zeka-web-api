using AdminAreaManagement.Core.Interfaces;
using FluentValidation;

namespace AdminAreaManagement.Application.Staffs.Commands.UpsertStaff
{
    public class UpsertStaffMemberCommandValidator : AbstractValidator<UpsertStaffMemberCommand>
    {
        private readonly IRepositoryManager _repository;

        public UpsertStaffMemberCommandValidator(IRepositoryManager repository)
        {
            _repository = repository;

            RuleFor(v => v.UserName)
                .MustAsync(async (obj, username, cancellationToken) =>
                {
                    bool result = _repository.StaffMember.StaffMemberBelongsToTeam(username, obj.TeamId);

                    return !result;

                }).OverridePropertyName("Property").WithMessage("Action not allowed, the staff member already exists in this team");
        }
    }
}
