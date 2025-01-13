using ClientManagement.Core.Interfaces;

namespace ClientManagement.Infrastructure.Persistence;
/**
 * Repository Manager will instantiate and register (inside dependency injection container) a repository class only if needed.
 */
public sealed class RepositoryManager : IRepositoryManager
{
    private readonly ApplicationDbContext _applicationDbContext;
    private readonly Lazy<IClientRepository> _clientRepository;
    private readonly Lazy<ISupportRepository> _supportRepository;
    private readonly Lazy<ISocialWorkerRepository> _socialWorkerRepository;
    private readonly Lazy<ITrainingTypeRepository> _trainingTypeRepository;
    private readonly Lazy<ITrainingRepository> _trainingRepository;
    private readonly Lazy<IAssessmentRepository> _assessmentRepository;
    private readonly Lazy<ITrainingFieldRepository> _trainingFieldRepository;
    private readonly Lazy<IMonitoringReportRepository> _quarterlyMonitoring;
    private readonly Lazy<IMonitoringActionRepository> _monitoringAction;
    private readonly Lazy<ISchoolRegistrationRepository> _schoolRegistrationRepository;
    private readonly Lazy<IProfessionalAssessmentRepository> _professionAssessmentRepository;
    private readonly Lazy<IProfessionRepository> _professionRepository;
    private readonly Lazy<IProfessionnalExperienceRepository> _professionnalExperienceRepository;
    private readonly Lazy<INatureOfContractRepository> _natureOfContractRepository;

    public RepositoryManager(ApplicationDbContext applicationDbContext)
    {
        _applicationDbContext = applicationDbContext;
        _clientRepository = new Lazy<IClientRepository>(() => new ClientRepository(applicationDbContext));
        _schoolRegistrationRepository =
            new Lazy<ISchoolRegistrationRepository>(() => new SchoolRegistrationRepository(applicationDbContext));
        _professionAssessmentRepository =
            new Lazy<IProfessionalAssessmentRepository>(() => new ProfessionalAssessmentRepository(applicationDbContext));
        _assessmentRepository = new Lazy<IAssessmentRepository>(() => new AssessmentRepository(applicationDbContext));
        _quarterlyMonitoring =
            new Lazy<IMonitoringReportRepository>(() => new MonitoringReportRepository(applicationDbContext));
        _monitoringAction = new Lazy<IMonitoringActionRepository>(() => new MonitoringActionRepository(applicationDbContext));
        _natureOfContractRepository =
            new Lazy<INatureOfContractRepository>(() => new NatureOfContractRepository(applicationDbContext));
        _professionnalExperienceRepository =
            new Lazy<IProfessionnalExperienceRepository>(() =>
                new ProfessionnalExperienceRepository(applicationDbContext));
    }

    public IClientRepository Client => _clientRepository.Value;
    public ISupportRepository Support => _supportRepository.Value;
    public ISocialWorkerRepository SocialWorker => _socialWorkerRepository.Value;
    public ISchoolRegistrationRepository SchoolRegistration => _schoolRegistrationRepository.Value;
    public ITrainingTypeRepository TrainingType => _trainingTypeRepository.Value;
    public IProfessionRepository Profession => _professionRepository.Value;
    public ITrainingRepository Training => _trainingRepository.Value;
    public IProfessionalAssessmentRepository ProfessionalAssessment => _professionAssessmentRepository.Value;
    public IAssessmentRepository Assessment => _assessmentRepository.Value;
    public ITrainingFieldRepository TrainingField => _trainingFieldRepository.Value;
    public IMonitoringReportRepository MonitoringReport => _quarterlyMonitoring.Value;
    public IMonitoringActionRepository MonitoringAction => _monitoringAction.Value;
    public INatureOfContractRepository NatureOfContract => _natureOfContractRepository.Value;
    public IProfessionnalExperienceRepository ProfessionnalExperience => _professionnalExperienceRepository.Value;
    public void Save() => _applicationDbContext.SaveChanges();
    public void SaveAsync() => _applicationDbContext.SaveChangesAsync();
}