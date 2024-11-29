using AdminAreaManagement.Core.Interfaces;
using FluentValidation;

namespace AdminAreaManagement.Application.Services.Commands.DeleteService
{
    public class DeleteTeamCommandValidator : AbstractValidator<DeleteTeamCommand>
    {

        //private readonly ITeamRepository _serviceRepository;
        private readonly IRepositoryManager _repository;

        public DeleteTeamCommandValidator(IRepositoryManager repository)
        {
            _repository = repository;

            RuleFor(v => v.Id)
                //.MustAsync(HasReferent).WithMessage("Cette action n'est pas permise !");
                .MustAsync(async (int id, CancellationToken cancellationToken) =>
                {
                    bool exist = await _repository.Team.TeamHasStaffMembers(id);
                    return !exist;
                }).OverridePropertyName("Property").WithMessage("Action impossible, des référents sont attachés à ce service !");
        }

        //public async Task<bool> HasReferent(int id, CancellationToken cancellationToken)
        //{
        //    var result = _referentRepository.GetStaffMembers().AnyAsync(referent => referent.TeamId == id).ConfigureAwait(false);
        //    Console.WriteLine(result);
        //    return await result;
        //}
    }
}
