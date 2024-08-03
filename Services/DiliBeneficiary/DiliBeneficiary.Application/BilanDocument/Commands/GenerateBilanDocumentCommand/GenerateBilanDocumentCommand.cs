//using DiliBeneficiary.Application.Common.Exceptions;
//using DiliBeneficiary.Application.Common.Interfaces;
//using DiliBeneficiary.Application.Configuration;
//using DiliBeneficiary.Application.Exceptions;
//using DiliBeneficiary.Application.Models;
//using DiliBeneficiary.Core.Entities;
//using DiliBeneficiary.Core.Interfaces;
//using MediatR;
//using Microsoft.Extensions.Logging;
//using Microsoft.Extensions.Options;

//namespace DiliBeneficiary.Application.BilanDocument.Commands.GenerateBilanDocumentCommand
//{
//    public class GenerateBilanDocumentCommand: IRequest<byte[]>
//    {
//        public int BilanId;
//        public GenerateBilanDocumentCommand()
//        {

//        }

//        public class GenerateBilanDocumentCommandHandler : IRequestHandler<GenerateBilanDocumentCommand, byte[]>
//        {
//            private readonly IDocumentGeneratorService _documentGeneratorService;
//            private readonly ILogger<GenerateBilanDocumentCommand> _logger;
//            private readonly FluidServiceConfiguration _configuration;
//            private readonly IRepositoryManager _repository;

//            public GenerateBilanDocumentCommandHandler(IDocumentGeneratorService documentGeneratorService, 
//                ILogger<GenerateBilanDocumentCommand> logger, 
//                IOptionsSnapshot<FluidServiceConfiguration> configurationAccessor,
//                IRepositoryManager repository) 
//            {
//                _documentGeneratorService = documentGeneratorService;
//                _repository = repository;
//                _logger = logger;
//                _configuration = configurationAccessor.Value; 
//            }

//            public async Task<byte[]> Handle(GenerateBilanDocumentCommand request, CancellationToken cancellationToken)
//            {
//                try
//                {
//                    var bilanReportModel = new BilanReportDocumentModel();

//                    if (!_configuration.IsValid())
//                        throw new FluidException("Invalid Fluid configuration");

//                    var logoBytes = File.ReadAllBytes(_configuration.LogoCPASFilePath);
//                    bilanReportModel.CpasLogo = Convert.ToBase64String(logoBytes);
//                    bilanReportModel.CpasNameFr = _configuration.CpasNameFr;
//                    bilanReportModel.CpasNameNl = _configuration.CpasNameNl;
//                    bilanReportModel.CpasZip = _configuration.CpasZip;
//                    bilanReportModel.CpasAdresse = _configuration.CpasAdresse;
//                    bilanReportModel.CallCenter = _configuration.CallCenter;
//                    bilanReportModel.EmailFr = _configuration.EmailFr;
//                    bilanReportModel.EmailNl = _configuration.EmailNl;


//                    Bilan bilan = _repository.Bilan.GetBilanById(request.BilanId);
//                    if (bilan == null)
//                        throw new NotFoundException(nameof(Bilan), request.BilanId);
//                    bilanReportModel.Bilan = bilan;

//                    Beneficiary beneficiary = _repository.Beneficiary.Get(bilan.BeneficiaryId,true);
//                    if (beneficiary == null)
//                        throw new NotFoundException(nameof(Beneficiary), request.BilanId);
//                    bilanReportModel.Beneficiary = beneficiary;

//                    var supports = _repository.Support.GetSupportsByBeneficiary(beneficiary.Id);
//                    bilanReportModel.Supports = supports.ToList();

//                    var schoolRegistrations = _repository.SchoolRegistration.GetResgistrationsByBeneficiary(beneficiary.Id);
//                    bilanReportModel.SchoolRegistrations = schoolRegistrations.ToList();

//                    var profExperiences = _repository.ProfessionnalExperience.GetProfessionnalExperienceByBenef(beneficiary.Id);
//                    bilanReportModel.ProfessionalExperiences = profExperiences.ToList<ProfessionnalExperience>();

//                    var doc = await _documentGeneratorService.GenerateBilanReportAsync(bilanReportModel);

//                    return doc;
//                }
//                catch (System.Exception e)
//                {
//                    _logger.LogError($@"Root Level Exception: {e.Message}");
//                    throw;
//                }
//            }
//        }
//    }
//}
