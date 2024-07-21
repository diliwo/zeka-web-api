namespace DiliBeneficiary.Application.Supports.Commands.DeleteSupport
{
    //public class DeleteServiceCommandValidator : AbstractValidator<DeleteServiceCommand>
    //{

    //    //private readonly IServiceRepository _serviceRepository;
    //    private readonly IReferentRepository _referentRepository;

    //    public DeleteServiceCommandValidator(IReferentRepository context)
    //    {
    //        _referentRepository = context;

    //        RuleFor(v => v.Id)
    //            //.MustAsync(HasReferent).WithMessage("Cette action n'est pas permise !");
    //            .MustAsync(async (int id, CancellationToken cancellationToken) =>
    //            {
    //                bool exist = await _referentRepository.ServiceHasReferents(id);
    //                return !exist;
    //            }).WithMessage("Action impossible, des référents sont attachés à ce service !");
    //    }

    //    //public async Task<bool> HasReferent(int id, CancellationToken cancellationToken)
    //    //{
    //    //    var result = _referentRepository.GetReferents().AnyAsync(referent => referent.ServiceId == id).ConfigureAwait(false);
    //    //    Console.WriteLine(result);
    //    //    return await result;
    //    //}
    //}
}
