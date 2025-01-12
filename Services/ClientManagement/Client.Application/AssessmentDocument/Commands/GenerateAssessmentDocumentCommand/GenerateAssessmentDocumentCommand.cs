using ClientManagement.Application.Common.Exceptions;
using ClientManagement.Application.Common.Interfaces;
using ClientManagement.Application.Configuration;
using ClientManagement.Application.Exceptions;
using ClientManagement.Application.Models;
using ClientManagement.Core.Entities;
using ClientManagement.Core.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClientManagement.Application.AssessmentDocument.Commands.GenerateAssessmentDocumentCommand
{
    public class GenerateAssessmentDocumentCommand: IRequest<byte[]>
    {
        public int AssessmentId;
        public GenerateAssessmentDocumentCommand()
        {

        }

        public class GenerateAssessmentDocumentCommandHandler : IRequestHandler<GenerateAssessmentDocumentCommand, byte[]>
        {
            private readonly IDocumentGeneratorService _documentGeneratorService;
            private readonly ILogger<GenerateAssessmentDocumentCommand> _logger;
            private readonly FluidServiceConfiguration _configuration;
            private readonly IRepositoryManager _repository;

            public GenerateAssessmentDocumentCommandHandler(IDocumentGeneratorService documentGeneratorService, 
                ILogger<GenerateAssessmentDocumentCommand> logger, 
                IOptionsSnapshot<FluidServiceConfiguration> configurationAccessor,
                IRepositoryManager repository) 
            {
                _documentGeneratorService = documentGeneratorService;
                _repository = repository;
                _logger = logger;
                _configuration = configurationAccessor.Value; 
            }

            public async Task<byte[]> Handle(GenerateAssessmentDocumentCommand request, CancellationToken cancellationToken)
            {
                try
                {
                    var AssessmentReportModel = new AssessmentReportDocumentModel();

                    if (!_configuration.IsValid())
                        throw new FluidException("Invalid Fluid configuration");

                    var logoBytes = File.ReadAllBytes(_configuration.LogoCPASFilePath);
                    AssessmentReportModel.Logo = Convert.ToBase64String(logoBytes);
                    AssessmentReportModel.Name = _configuration.CpasNameFr;
                    AssessmentReportModel.ZipCode = _configuration.CpasZip;
                    AssessmentReportModel.Address = _configuration.CpasAdresse;
                    AssessmentReportModel.PhoneNumber = _configuration.CallCenter;
                    AssessmentReportModel.Email = _configuration.EmailFr;

                    Assessment Assessment = _repository.Assessment.GetAssessmentById(request.AssessmentId);
                    if (Assessment == null)
                        throw new NotFoundException(nameof(Assessment), request.AssessmentId);
                    AssessmentReportModel.Assessment = Assessment;

                    Core.Entities.Client client = _repository.Client.Get(Assessment.ClientId,true);
                    if (client == null)
                        throw new NotFoundException(nameof(Client), request.AssessmentId);
                    AssessmentReportModel.Client = client;

                    var supports = _repository.Support.GetSupportsByClient(client.Id);
                    AssessmentReportModel.Supports = supports.ToList();

                    var schoolRegistrations = _repository.SchoolRegistration.GetResgistrationsByClient(client.Id);
                    AssessmentReportModel.SchoolRegistrations = schoolRegistrations.ToList();

                    var profExperiences = _repository.ProfessionnalExperience.GetProfessionnalExperienceByBenef(client.Id);
                    AssessmentReportModel.ProfessionalExperiences = profExperiences.ToList<ProfessionnalExperience>();

                    var doc = await _documentGeneratorService.GenerateAssessmentReportAsync(AssessmentReportModel);

                    return doc;
                }
                catch (System.Exception e)
                {
                    _logger.LogError($@"Root Level Exception: {e.Message}");
                    throw;
                }
            }
        }
    }
}
