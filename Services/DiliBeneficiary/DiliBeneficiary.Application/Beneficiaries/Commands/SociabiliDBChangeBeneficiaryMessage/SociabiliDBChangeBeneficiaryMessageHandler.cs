//using AutoMapper;
//using DiliBeneficiary.Application.Common.Exceptions;
//using DiliBeneficiary.Core.Entities;
//using DiliBeneficiary.Core.Interfaces;
//using Microsoft.Extensions.DependencyInjection;
//using Microsoft.Extensions.Logging;
//using Newtonsoft.Json;

//namespace DiliBeneficiary.Application.Beneficiaries.Commands.SociabiliDBChangeBeneficiaryMessage
//{
//    public class SociabiliDBChangeBeneficiaryMessageHandler : IAsyncNonCyclicMessageHandler
//    {
//        readonly ILogger<SociabiliDBChangeBeneficiaryMessageHandler> _logger;
//        public readonly IRepositoryManager _repository;
//        public readonly IBeneficiaryService _beneficiaryService;
//        //public readonly ISociabiliService _sociabiliService;

//        protected readonly IMapper _mapper;

//        public SociabiliDBChangeBeneficiaryMessageHandler(IServiceScopeFactory serviceScopeFactory, IAppointmentService appointmentService)
//        {
//            var scoped = serviceScopeFactory.CreateScope();
//            _logger = scoped.ServiceProvider.GetRequiredService<ILogger<SociabiliDBChangeBeneficiaryMessageHandler>>();
//            _repository = scoped.ServiceProvider.GetRequiredService<IRepositoryManager>();
//            _beneficiaryService = scoped.ServiceProvider.GetRequiredService<IBeneficiaryService>();
//            //_sociabiliService = scoped.ServiceProvider.GetRequiredService<ISociabiliService>();
//        }



//        public async Task Handle(string message, string routingKey, IQueueService queueService)
//        {
//            var resultMessage = "";

//            _logger.LogInformation($"Update Beneficiary for Appi : {message} by routing key {routingKey}");

//            try
//            {
                
//                if (message.StartsWith("\"{\\\""))
//                {
//                    message = JsonConvert.DeserializeObject<string>(message);
//                    var sociabiliDBChange = JsonConvert.DeserializeObject<Message.SociabiliDBChange>(message);


//                    Beneficiary beneficiary = null;

//                    if (sociabiliDBChange.TableName.Equals("SSC_INDIVIDUS"))
//                    {
//                        beneficiary = await _repository.Beneficiary.GetSourceIdAsync(Int32.Parse(sociabiliDBChange.IdTarget), true);

//                    }
//                    else if (sociabiliDBChange.TableName.Equals("SSC_ADRESSE"))
//                    {
//                        var beneficiaryId = await _sociabiliService.GetBeneficiaryIdByAddressId(Int32.Parse(sociabiliDBChange.IdTarget));

//                        if (beneficiaryId != 0)
//                        {
//                            beneficiary = await _repository.Beneficiary.GetSourceIdAsync(Int32.Parse(sociabiliDBChange.IdTarget), true);
//                        }
//                    }


//                    if (beneficiary is not null)
//                    {
//                        await _beneficiaryService.UpSert(beneficiary.Niss);
//                    }
//                    else
//                    {
//                        throw new NotFoundException(nameof(beneficiary));
//                       _logger.LogWarning($" La donnée n'existe pas dans sociabili, table: {sociabiliDBChange.TableName}, id {sociabiliDBChange.IdTarget}");
//                    }
//                }
//            }
//            catch (Exception e)
//            {
//                _logger.LogError($"Erreur lors du traitement d'un changement de bénéficiaire  : {e.Message}");
//                resultMessage = $"Erreur lors du traitement d'un changement de bénéficiaire : {e.Message}";
//            }

//        }
//    }
//}
