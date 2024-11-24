namespace Client.Core.Interfaces;

public interface IRepositoryManager
{
    IClientRepository Client { get; }
    ISupportRepository Support { get; }
    IStaffRepository Staff { get; }
    ISchoolRegistrationRepository SchoolRegistration { get; }
    ITrainingTypeRepository TrainingType { get; }
    IProfessionRepository Profession { get; }
    ITrainingRepository Training { get; }
    IProfessionBilanRepository ProfessionBilan { get; }
    IBilanRepository Bilan { get; }
    ITrainingFieldRepository TrainingField { get; }
    IQuarterlyMonitoringRepository QuarterlyMonitoring { get; }
    IMonitoringActionRepository MonitoringAction { get; }
    void Save();
    void SaveAsync();
}