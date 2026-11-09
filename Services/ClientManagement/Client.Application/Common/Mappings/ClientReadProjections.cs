using ClientManagement.Application.Clients.Queries.GetClients;
using ClientManagement.Application.MonitoringActions.Common;
using ClientManagement.Application.QuarterlyMonitorings.Common;
using ClientManagement.Application.Supports.Queries;
using ClientManagement.Core.Entities;
using ClientManagement.Core.Enums;
using Riok.Mapperly.Abstractions;

namespace ClientManagement.Application.Common.Mappings;

[Mapper(RequiredMappingStrategy = RequiredMappingStrategy.Target)]
internal static partial class ClientReadProjections
{
    internal static partial IQueryable<QuarterlyMonitoringDto> ProjectQuarterly(this IQueryable<MonitoringReport> source);

    [MapProperty(nameof(MonitoringReport.Id), nameof(QuarterlyMonitoringDto.QMonitoringId))]
    [MapPropertyFromSource(nameof(QuarterlyMonitoringDto.ClientName), Use = nameof(ClientName))]
    [MapProperty("Client.LastName", nameof(QuarterlyMonitoringDto.ClientLastName))]
    [MapProperty("Client.FirstName", nameof(QuarterlyMonitoringDto.ClientFirstName))]
    [MapProperty("Client.Ssn", nameof(QuarterlyMonitoringDto.ClientNiss))]
    [MapProperty("Client.ReferenceNumber", nameof(QuarterlyMonitoringDto.ClientDossier))]
    [MapProperty(nameof(MonitoringReport.SocialWorkerId), nameof(QuarterlyMonitoringDto.StaffMemberId))]
    [MapPropertyFromSource(nameof(QuarterlyMonitoringDto.StaffMemberName), Use = nameof(StaffMemberName))]
    [MapProperty("SocialWorker.LastName", nameof(QuarterlyMonitoringDto.StaffMemberLastName))]
    [MapProperty("SocialWorker.FirstName", nameof(QuarterlyMonitoringDto.StaffMemberFirstName))]
    [MapProperty("MonitoringAction.Action", nameof(QuarterlyMonitoringDto.MonitoringActionLabel))]
    [MapProperty(nameof(MonitoringReport.Period), nameof(QuarterlyMonitoringDto.Quarter))]
    private static partial QuarterlyMonitoringDto MapQuarterly(MonitoringReport report);

    private static string ClientName(MonitoringReport report) => report.Client.LastName + " " + report.Client.FirstName;

    private static string StaffMemberName(MonitoringReport report) =>
        report.SocialWorker.LastName + " " + report.SocialWorker.FirstName;

    internal static partial IQueryable<MonitoringActionDto> ProjectActions(this IQueryable<MonitoringAction> source);

    [MapProperty(nameof(MonitoringAction.Id), nameof(MonitoringActionDto.ActionId))]
    [MapProperty(nameof(MonitoringAction.Action), nameof(MonitoringActionDto.ActionLabel))]
    private static partial MonitoringActionDto MapAction(MonitoringAction action);

    internal static IQueryable<SupportDto> ProjectSupports(this IQueryable<SocialCase> source)
    {
        var today = DateTime.Today;
        return source.Select(s => new SupportDto
        {
            SupportId = s.Id,
            StartDate = s.StartDate,
            EndDate = s.EndDate,
            ClientId = s.ClientId,
            StaffMemberInfo = s.SocialWorker.LastName + " " + s.SocialWorker.FirstName + " (" + s.SocialWorker.TeamAcronym + ")",
            Client = new ClientLookUpDto
            {
                ClientId = s.Client.Id,
                EmailAddress = s.Client.Email!.EmailAddress,
                Name = s.Client.LastName + " " + s.Client.FirstName,
                Ssn = s.Client.Ssn,
                Gender = s.Client.Gender == Gender.Male ? "H" : "F"
            },
            IsActif = !s.Softdelete && s.StartDate <= today && (s.EndDate == null || s.EndDate > today),
            Note = s.Note,
            ReasonOfClosure = s.ReasonOfClosure,
            HasNote = s.Note != null && s.Note.Length > 0,
            IsLastSupport = s.Client.SocialCases.Where(c => !c.Softdelete)
                .OrderByDescending(c => c.Id).Select(c => (int?)c.Id).FirstOrDefault() == s.Id
        });
    }
}
