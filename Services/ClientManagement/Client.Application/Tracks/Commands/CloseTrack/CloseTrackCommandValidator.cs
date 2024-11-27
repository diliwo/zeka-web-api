using Client.Core.Interfaces;
using FluentValidation;

namespace Client.Application.Tracks.Commands.CloseTrack
{
    public class CloseTrackCommandValidator : AbstractValidator<CloseTrackCommand>
    {
        private readonly IRepositoryManager _repository;

        public CloseTrackCommandValidator(IRepositoryManager repository)
        {
            _repository = repository;

            RuleFor(v => v.EndDate)
                .MustAsync(async (obj, date, cancellationToken) =>
                {
                    bool result = await _repository.Track.EndDateIsGreaterThanStartDate(obj.TrackId, date);

                    return result;

                }).OverridePropertyName("Property").WithMessage("Action not allowed, The end date is not consistent !");
            RuleFor(support => support.ReasonOfClosure)
                .NotEmpty()
                .OverridePropertyName("Property")
                .WithMessage("The reason is mandatory !");
        }
    }
}
