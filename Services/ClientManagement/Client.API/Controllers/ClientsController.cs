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
        public async Task<ActionResult> GetBySearchText(string text)
        {
            var vm = await Mediator.Send(new GetClientsBySearchTextQuery() { SearchText = text});
            return Ok(vm);
        }

        [HttpGet("{ssn}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult<ClientsDto>> Get(string ssn)
        {
            var vm = await Mediator.Send(new GetClientDetailQuery() { Niss = ssn });

            return Ok(vm);
        }

        [HttpPost]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesDefaultResponseType]
        public async Task<ActionResult> Add(AddClientCommand command)
        {
            var vm = await Mediator.Send(command);

            return Ok(vm);
        }

        [HttpPost("updatelanguage/{ssn}/{language?}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult> UpdateLanguage(string ssn, string language = "")
        {
            var vm = await Mediator.Send(new UpdateNativeLanguageCommand() { Niss = ssn, Language = language });

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
