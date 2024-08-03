using DiliBeneficiary.Core.Interfaces;

namespace DiliBeneficiary.Infrastructure.Persistence;
/**
 * Repository Manager will instantiate and register (inside dependency injection container) a repository class only if needed.
 */
public sealed class RepositoryManager : IRepositoryManager
{
    private readonly ApplicationDbContext _applicationDbContext;
    private readonly Lazy<IBeneficiaryRepository> _beneficiaryRepository;
    private readonly Lazy<ISupportRepository> _supportRepository;
    private readonly Lazy<IReferentRepository> _referentRepository;
    private readonly Lazy<ITrainingTypeRepository> _trainingTypeRepository;
    private readonly Lazy<IFormationRepository> _formationRepository;
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
        _beneficiaryRepository = new Lazy<IBeneficiaryRepository>(() => new BeneficiaryRepository(applicationDbContext));
        _schoolRegistrationRepository =
            new Lazy<ISchoolRegistrationRepository>(() => new SchoolRegistrationRepository(applicationDbContext));
        _professionBilanRepository =
            new Lazy<IProfessionBilanRepository>(() => new ProfessionBilanRepository(applicationDbContext));
        _bianRepository = new Lazy<IBilanRepository>(() => new BilanRepository(applicationDbContext));
        _quarterlyMonitoring =
            new Lazy<IQuarterlyMonitoringRepository>(() => new QuarterlyMonitoringRepository(applicationDbContext));
        _monitoringAction = new Lazy<IMonitoringActionRepository>(() => new MonitoringActionRepository(applicationDbContext));
    }

    public IBeneficiaryRepository Beneficiary => _beneficiaryRepository.Value;
    public ISupportRepository Support => _supportRepository.Value;
    public IReferentRepository Referent => _referentRepository.Value;
    public ISchoolRegistrationRepository SchoolRegistration => _schoolRegistrationRepository.Value;
    public ITrainingTypeRepository TrainingType => _trainingTypeRepository.Value;
    public IProfessionRepository Profession => _professionRepository.Value;
    public IFormationRepository Formation => _formationRepository.Value;
    public IProfessionBilanRepository ProfessionBilan => _professionBilanRepository.Value;
    public IBilanRepository Bilan => _bianRepository.Value;
    public ITrainingFieldRepository TrainingField => _trainingFieldRepository.Value;
    public IQuarterlyMonitoringRepository QuarterlyMonitoring => _quarterlyMonitoring.Value;
    public IMonitoringActionRepository MonitoringAction => _monitoringAction.Value;
    public void Save() => _applicationDbContext.SaveChanges();
    public void SaveAsync() => _applicationDbContext.SaveChangesAsync();
}