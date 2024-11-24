using Client.Core.Interfaces;

namespace Client.Infrastructure.Persistence;
/**
 * Repository Manager will instantiate and register (inside dependency injection container) a repository class only if needed.
 */
public sealed class RepositoryManager : IRepositoryManager
{
    private readonly ApplicationDbContext _applicationDbContext;
    private readonly Lazy<IClientRepository> _ClientRepository;
    private readonly Lazy<ISupportRepository> _supportRepository;
    private readonly Lazy<IStaffRepository> _StaffRepository;
    private readonly Lazy<ITrainingTypeRepository> _trainingTypeRepository;
    private readonly Lazy<ITrainingRepository> _TrainingRepository;
    private readonly Lazy<IBilanRepository> _bianRepository;
    private readonly Lazy<ITrainingFieldRepository> _trainingFieldRepository;
    private readonly Lazy<IQuarterlyMonitoringRepository> _quarterlyMonitoring;
    private readonly Lazy<IMonitoringActionRepository> _monitoringAction;
    private readonly Lazy<ISchoolRegistrationRepository> _schoolRegistrationRepository;
    private readonly Lazy<IProfessionBilanRepository> _professionBilanRepository;
    private readonly Lazy<IProfessionRepository> _professionRepository;

    public RepositoryManager(ApplicationDbContext applicationDbContext)
    {
        _applicationDbContext = applicationDbContext;
        _ClientRepository = new Lazy<IClientRepository>(() => new ClientRepository(applicationDbContext));
        _schoolRegistrationRepository =
            new Lazy<ISchoolRegistrationRepository>(() => new SchoolRegistrationRepository(applicationDbContext));
        _professionBilanRepository =
            new Lazy<IProfessionBilanRepository>(() => new ProfessionBilanRepository(applicationDbContext));
        _bianRepository = new Lazy<IBilanRepository>(() => new AssessmentRepository(applicationDbContext));
        _quarterlyMonitoring =
            new Lazy<IQuarterlyMonitoringRepository>(() => new QuarterlyMonitoringRepository(applicationDbContext));
        _monitoringAction = new Lazy<IMonitoringActionRepository>(() => new MonitoringActionRepository(applicationDbContext));
    }

    public IClientRepository Client => _ClientRepository.Value;
    public ISupportRepository Support => _supportRepository.Value;
    public IStaffRepository Staff => _StaffRepository.Value;
    public ISchoolRegistrationRepository SchoolRegistration => _schoolRegistrationRepository.Value;
    public ITrainingTypeRepository TrainingType => _trainingTypeRepository.Value;
    public IProfessionRepository Profession => _professionRepository.Value;
    public ITrainingRepository Training => _TrainingRepository.Value;
    public IProfessionBilanRepository ProfessionBilan => _professionBilanRepository.Value;
    public IBilanRepository Bilan => _bianRepository.Value;
    public ITrainingFieldRepository TrainingField => _trainingFieldRepository.Value;
    public IQuarterlyMonitoringRepository QuarterlyMonitoring => _quarterlyMonitoring.Value;
    public IMonitoringActionRepository MonitoringAction => _monitoringAction.Value;
    public void Save() => _applicationDbContext.SaveChanges();
    public void SaveAsync() => _applicationDbContext.SaveChangesAsync();
}