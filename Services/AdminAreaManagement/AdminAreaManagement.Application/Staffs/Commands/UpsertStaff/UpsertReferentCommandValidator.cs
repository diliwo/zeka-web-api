using AdminAreaManagement.Core.Interfaces;
using FluentValidation;

namespace AdminAreaManagement.Application.Referents.Commands.UpsertReferent
{
    public class UpsertReferentCommandValidator : AbstractValidator<UpsertStaffMemberCommand>
    {
        private readonly IRepositoryManager _repository;

        public UpsertReferentCommandValidator(IRepositoryManager repository)
        {
            _repository = repository;

            RuleFor(v => v.UserName)
                .MustAsync(async (obj, username, cancellationToken) =>
                {
                    bool result = _repository.StaffMember.UserAlreadyExists(username, obj.TeamId);

                    return !result;

                }).OverridePropertyName("Property").WithMessage("Action not allowed, the staff member already exists in this team");
        }
    }
}
