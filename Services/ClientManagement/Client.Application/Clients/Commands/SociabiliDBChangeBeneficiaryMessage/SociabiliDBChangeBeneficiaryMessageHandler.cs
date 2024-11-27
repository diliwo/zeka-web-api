//using AutoMapper;
//using Client.Application.Common.Exceptions;
//using Client.Core.Entities;
//using Client.Core.Interfaces;
//using Microsoft.Extensions.DependencyInjection;
//using Microsoft.Extensions.Logging;
//using Newtonsoft.Json;

//namespace Client.Application.Clients.Commands.SociabiliDBChangeClientMessage
//{
//    public class SociabiliDBChangeClientMessageHandler : IAsyncNonCyclicMessageHandler
//    {
//        readonly ILogger<SociabiliDBChangeClientMessageHandler> _logger;
//        public readonly IRepositoryManager _repository;
//        public readonly IClientService _ClientService;
//        //public readonly ISociabiliService _sociabiliService;

//        protected readonly IMapper _mapper;

//        public SociabiliDBChangeClientMessageHandler(IServiceScopeFactory serviceScopeFactory, IAppointmentService appointmentService)
//        {
//            var scoped = serviceScopeFactory.CreateScope();
//            _logger = scoped.ServiceProvider.GetRequiredService<ILogger<SociabiliDBChangeClientMessageHandler>>();
//            _repository = scoped.ServiceProvider.GetRequiredService<IRepositoryManager>();
//            _ClientService = scoped.ServiceProvider.GetRequiredService<IClientService>();
//            //_sociabiliService = scoped.ServiceProvider.GetRequiredService<ISociabiliService>();
//        }



//        public async Task Handle(string message, string routingKey, IQueueService queueService)
//        {
//            var resultMessage = "";

//            _logger.LogInTraining($"Update Client for Appi : {message} by routing key {routingKey}");

//            try
//            {
                
//                if (message.StartsWith("\"{\\\""))
//                {
//                    message = JsonConvert.DeserializeObject<string>(message);
//                    var sociabiliDBChange = JsonConvert.DeserializeObject<Message.SociabiliDBChange>(message);


//                    Client client = null;

//                    if (sociabiliDBChange.TableName.Equals("SSC_INDIVIDUS"))
//                    {
//                        client = await _repository.Client.GetSourceIdAsync(Int32.Parse(sociabiliDBChange.IdTarget), true);

//                    }
//                    else if (sociabiliDBChange.TableName.Equals("SSC_ADRESSE"))
//                    {
//                        var ClientId = await _sociabiliService.GetClientIdByAddressId(Int32.Parse(sociabiliDBChange.IdTarget));

//                        if (ClientId != 0)
//                        {
//                            client = await _repository.Client.GetSourceIdAsync(Int32.Parse(sociabiliDBChange.IdTarget), true);
//                        }
//                    }


//                    if (client is not null)
//                    {
//                        await _ClientService.UpSert(client.Niss);
//                    }
//                    else
//                    {
//                        throw new NotFoundException(nameof(client));
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
