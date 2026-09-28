namespace ClientManagement.Application.Common.Authorization;

public enum ClientRequestActivity
{
    Neutral = 0,
    OrdinaryOrganisationMutation = 1
}

public static class ClientRequestActivityClassifier
{
    private static readonly HashSet<string> OrdinaryMutationTypeNames = new(StringComparer.Ordinal)
    {
        "ClientManagement.Application.Assessments.Commands.CreateAssessment.CreateAssessmentCommand",
        "ClientManagement.Application.Assessments.Commands.DeleteAssessment.DeleteAssessmentCommand",
        "ClientManagement.Application.Assessments.Commands.FinalizeAssessment.FinalizeAssessmentCommand",
        "ClientManagement.Application.Assessments.Commands.UpdateAssessment.UpdateAssessmentCommand",
        "ClientManagement.Application.Clients.Commands.AddClient.AddClientCommand",
        "ClientManagement.Application.Clients.Commands.UpSertIbisNumber.UpSertIbisNumberCommand",
        "ClientManagement.Application.Clients.Commands.UpdateClient.UpdateClientCommand",
        "ClientManagement.Application.Clients.Commands.UpdateNativeLanguage.UpdateNativeLanguageCommand",
        "ClientManagement.Application.MonitoringActions.Commands.CreateAction.CreateMonitoringActionCommand",
        "ClientManagement.Application.MonitoringActions.Commands.DeleteAction.DeleteMonitoringActionCommand",
        "ClientManagement.Application.MonitoringActions.Commands.UpdateAction.UpdateMonitoringActionCommand",
        "ClientManagement.Application.QuarterlyMonitorings.Commands.CreateQuarterlyMonitoring.CreateQuarterlyMonitoringCommand",
        "ClientManagement.Application.QuarterlyMonitorings.Commands.DeleteQuarterlyMonitoring.DeleteQuarterlyMonitoringCommand",
        "ClientManagement.Application.QuarterlyMonitorings.Commands.UpdateQuarterlyMonitoring.UpdateQuarterlyMonitoringCommand",
        "ClientManagement.Application.SchoolRegistations.Commands.CreateSchoolRegistration.CreateSchoolRegistrationCommand",
        "ClientManagement.Application.SchoolRegistations.Commands.DeleteSchoolRegistration.DeleteSchoolRegistrationCommand",
        "ClientManagement.Application.SchoolRegistations.Commands.UpdateSchoolRegistration.UpdateSchoolRegistrationCommand",
        "ClientManagement.Application.Supports.Commands.CloseTrack.CloseSupportCommand",
        "ClientManagement.Application.Supports.Commands.DeleteSupport.DeleteSupportCommand",
        "ClientManagement.Application.Supports.Commands.UpsertSupport.UpsertSupportCommand"
    };

    private static readonly HashSet<string> NonMutatingCommandTypeNames = new(StringComparer.Ordinal)
    {
        "ClientManagement.Application.AssessmentDocument.Commands.GenerateAssessmentDocumentCommand.GenerateAssessmentDocumentCommand"
    };

    public static IReadOnlyCollection<string> ClassifiedCommandTypeNames =>
        OrdinaryMutationTypeNames.Concat(NonMutatingCommandTypeNames).Order(StringComparer.Ordinal).ToArray();

    public static ClientRequestActivity Classify(Type requestType)
    {
        ArgumentNullException.ThrowIfNull(requestType);
        var identity = requestType.FullName
            ?? throw new InvalidOperationException("client_request_identity_missing");
        if (OrdinaryMutationTypeNames.Contains(identity))
            return ClientRequestActivity.OrdinaryOrganisationMutation;
        if (NonMutatingCommandTypeNames.Contains(identity))
            return ClientRequestActivity.Neutral;
        if (requestType.Namespace?.Contains(".Commands.", StringComparison.Ordinal) is true)
            throw new InvalidOperationException($"client_command_activity_unclassified:{identity}");
        return ClientRequestActivity.Neutral;
    }
}
