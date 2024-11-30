using AdminAreaManagement.Application.Common.Services;
using AdminAreaManagement.Core.Entities;
using AdminAreaManagement.Core.Interfaces;

namespace AdminAreaManagement.Infrastructure.Persistence;
/**
 * Repository Manager will instantiate and register (inside dependency injection container) a repository class only if needed.
 */
public sealed class RepositoryManager : IRepositoryManager
{
    private readonly ApplicationDbContext _applicationDbContext;
    private readonly Lazy<IStaffMemberRepository> _StaffMemberRepository;
    private readonly Lazy<ITeamRepository> _teamRepository;
    private readonly Lazy<ITrainingTypeRepository> _trainingTypeRepository;
    private readonly Lazy<ITrainingsRepository> _TrainingRepository;
    private readonly Lazy<ITrainingFieldRepository> _trainingFieldRepository;
    private readonly Lazy<IProfessionRepository> _professionRepository;
    private readonly Lazy<IPartnerRepository> _partnerRepository;
    private readonly Lazy<ISchoolRepository> _schoolRepository;
    private readonly Lazy<IDocumentPartnerRepository> _documentPartnerRepository;

    public RepositoryManager(ApplicationDbContext applicationDbContext, IFileService fileService)
    {
        _applicationDbContext = applicationDbContext;
        _teamRepository = new Lazy<ITeamRepository>(() => new TeamRepository(applicationDbContext));
        _StaffMemberRepository = new Lazy<IStaffMemberRepository>(() => new StaffMemberRepository(applicationDbContext));
        _partnerRepository = new Lazy<IPartnerRepository>(() => new PartnerRepository(applicationDbContext));
        _documentPartnerRepository = new Lazy<IDocumentPartnerRepository>(() => new DocumentPartnerRepository(applicationDbContext, fileService));
        _schoolRepository = new Lazy<ISchoolRepository>(() => new SchoolRepository(applicationDbContext));
        _TrainingRepository =
            new Lazy<ITrainingsRepository>(() => new TrainingsRepository(applicationDbContext));
        _professionRepository = new Lazy<IProfessionRepository>(() => new ProfessionRepository(applicationDbContext));
        _trainingFieldRepository = new Lazy<ITrainingFieldRepository>(() => new TrainingFieldRepository(applicationDbContext));
        _trainingTypeRepository =
            new Lazy<ITrainingTypeRepository>(() => new TrainingTypeRepository(applicationDbContext));
        _trainingFieldRepository =
            new Lazy<ITrainingFieldRepository>(() => new TrainingFieldRepository(applicationDbContext));
    }

    public IStaffMemberRepository StaffMember => _StaffMemberRepository.Value;
    public ITrainingTypeRepository TrainingType => _trainingTypeRepository.Value;
    public IProfessionRepository Profession => _professionRepository.Value;
    public ITrainingsRepository Trainings => _TrainingRepository.Value;
    public ITrainingFieldRepository TrainingField => _trainingFieldRepository.Value;
    public IPartnerRepository Partner => _partnerRepository.Value;
    public ITeamRepository Team => _teamRepository.Value;
    public ISchoolRepository School => _schoolRepository.Value;
    public IDocumentPartnerRepository DocumentPartner => _documentPartnerRepository.Value;
    public void Save() => _applicationDbContext.SaveChanges();
    public async Task SaveAsync() => await _applicationDbContext.SaveChangesAsync();
}