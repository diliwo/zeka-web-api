﻿namespace ClientManagement.Core.Interfaces;

public interface IRepositoryManager
{
    IClientRepository Client { get; }
    ITrackRepository Track { get; }
    IStaffMemberRepository StaffMember { get; }
    ISchoolRegistrationRepository SchoolRegistration { get; }
    ITrainingTypeRepository TrainingType { get; }
    IProfessionRepository Profession { get; }
    ITrainingRepository Training { get; }
    IProfessionalAssessmentRepository ProfessionalAssessment { get; }
    IAssessmentRepository Assessment { get; }
    ITrainingFieldRepository TrainingField { get; }
    IQuarterlyMonitoringRepository QuarterlyMonitoring { get; }
    IMonitoringActionRepository MonitoringAction { get; }
    void Save();
    void SaveAsync();
}