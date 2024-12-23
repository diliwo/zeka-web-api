using System.Reflection;
using ClientManagement.Application.Clients.Commands.AddClient;
using ClientManagement.Application.Clients.Commands.UpdateNativeLanguage;
using ClientManagement.Application.Clients.Queries.GetClientDetail;
using ClientManagement.Application.Clients.Queries.GetClients;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClientManagement.API.Controllers
{
    public class ClientsController : ApiControllerBase
    {
        [HttpGet]
        public async Task<ActionResult<ClientsDto>> GetAll()
        {
            var vm = await Mediator.Send(new GetClientsQuery());
            return Ok(vm);
        }


        [HttpGet("searchtext/{text}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult<ClientsDto>> GetBySearchText(string text)
        {
            var vm = await Mediator.Send(new GetClientsBySearchTextQuery() { SearchText = text});
            return Ok(vm);
        }

        [HttpGet("{niss}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult<ClientsDto>> Get(string niss)
        {
            var vm = await Mediator.Send(new GetClientDetailQuery() { Niss = niss });

            return Ok(vm);
        }

        [HttpPost()]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        //public async Task<ActionResult<BeneficiariesVm>> Import(ImportBeneficiaryCommand command)
        public async Task<ActionResult<ClientsDto>> Add()
        {
            var vm = await Mediator.Send(new AddClientCommand());

            return Ok(vm);
        }

        [HttpPost("updatelanguage/{niss}/{language?}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult> UpdateLanguage(string niss, string language = "")
        {
            var vm = await Mediator.Send(new UpdateNativeLanguageCommand() { Niss = niss, Language = language });

            return Ok(vm);
        }

        [AllowAnonymous]
        [HttpGet]
        [Route("/api/version")]
        public IActionResult Get()
        {
            var attribute = Assembly.GetExecutingAssembly()
                .GetCustomAttributes<AssemblyInformationalVersionAttribute>()
                .FirstOrDefault();

            if (attribute == null)
            {
                return NotFound("Version information is not available.");
            }

            var version = attribute.InformationalVersion;
            return Ok(version);

        }

    }
}
