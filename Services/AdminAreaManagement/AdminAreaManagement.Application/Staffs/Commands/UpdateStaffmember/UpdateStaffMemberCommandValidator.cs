using AdminAreaManagement.Core.Interfaces;
using FluentValidation;
using FluentValidation.Validators;

namespace AdminAreaManagement.Application.Staffs.Commands.UpdateStaffmember
{
    public class UpdateStaffMemberCommandValidator : AbstractValidator<UpdateStaffMemberCommand>
    {
        private readonly IRepositoryManager _repository;

        public UpdateStaffMemberCommandValidator(IRepositoryManager repository)
        {
            _repository = repository;

            RuleFor(v => v.StaffMemberId)
                .NotNull().WithMessage("FirstName is required.");

            RuleFor(v => v.FirstName)
                .NotEmpty().WithMessage("FirstName is required.")
                .MaximumLength(100).WithMessage("Number of chars must not exceed 50.");

            RuleFor(v => v.LastName)
                .NotEmpty().WithMessage("LastName is required.")
                .MaximumLength(100).WithMessage("Number of chars must not exceed 50.");

            When(v => v.StaffMemberId != null, () =>
            {
                RuleFor(v => v.UserName)
                    .MustAsync(async (obj, username, cancellationToken) =>
                    {
                        bool result = _repository.StaffMember.StaffMemberBelongsToTeam(username, obj.TeamId);

                        return !result;

                    }).OverridePropertyName("Property").WithMessage("Action not allowed, the staff member already exists in this team");
            });
        }
    }
}
