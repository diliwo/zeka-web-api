namespace AdminAreaManagement.Core.Interfaces;

public interface IRepositoryManager
{
    ITeamRepository Team { get; }
    IStaffMemberRepository StaffMember { get; }
    IPartnerRepository Partner { get; }
    IDocumentPartnerRepository DocumentPartner { get; }
    ISchoolRepository School { get; }
    ITrainingTypeRepository TrainingType { get; }
    IProfessionRepository Profession { get; }
    ITrainingsRepository Trainings { get; }
    ITrainingFieldRepository TrainingField { get; }
    void Save();
    Task SaveAsync();
}