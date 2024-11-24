namespace Client.Application.Supports.Commands.DeleteSupport
{
    //public class DeleteServiceCommandValidator : AbstractValidator<DeleteServiceCommand>
    //{

    //    //private readonly IServiceRepository _serviceRepository;
    //    private readonly IStaffRepository _StaffRepository;

    //    public DeleteServiceCommandValidator(IStaffRepository context)
    //    {
    //        _StaffRepository = context;

    //        RuleFor(v => v.Id)
    //            //.MustAsync(HasStaff).WithMessage("Cette action n'est pas permise !");
    //            .MustAsync(async (int id, CancellationToken cancellationToken) =>
    //            {
    //                bool exist = await _StaffRepository.ServiceHasStaffs(id);
    //                return !exist;
    //            }).WithMessage("Action impossible, des référents sont attachés à ce service !");
    //    }

    //    //public async Task<bool> HasStaff(int id, CancellationToken cancellationToken)
    //    //{
    //    //    var result = _StaffRepository.GetStaffs().AnyAsync(staff => staff.ServiceId == id).ConfigureAwait(false);
    //    //    Console.WriteLine(result);
    //    //    return await result;
    //    //}
    //}
}
