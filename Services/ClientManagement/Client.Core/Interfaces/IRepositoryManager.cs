namespace ClientManagement.Core.Interfaces;

public interface IRepositoryManager
{
    IClientRepository Client { get; }
    ISupportRepository Support { get; }
    ISocialWorkerRepository SocialWorker { get; }
    ISchoolRegistrationRepository SchoolRegistration { get; }
    ITrainingTypeRepository TrainingType { get; }
    IProfessionRepository Profession { get; }
    ITrainingRepository Training { get; }
    IProfessionalAssessmentRepository ProfessionalAssessment { get; }
    IAssessmentRepository Assessment { get; }
    ITrainingFieldRepository TrainingField { get; }
    IMonitoringReportRepository MonitoringReport { get; }
    IMonitoringActionRepository MonitoringAction { get; }
    INatureOfContractRepository NatureOfContract { get; }
    IProfessionnalExperienceRepository ProfessionnalExperience { get; }
    void Save();
    void SaveAsync();
}