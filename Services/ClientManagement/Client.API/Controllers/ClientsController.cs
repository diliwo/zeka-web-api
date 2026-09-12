using System.Reflection;
using ClientManagement.Application.Clients.Commands.AddClient;
using ClientManagement.API.Filters;
using System.ComponentModel.DataAnnotations;
using ClientManagement.Application.Clients.Commands.UpdateNativeLanguage;
using ClientManagement.Application.Clients.Queries.GetClientDetail;
using ClientManagement.Application.Clients.Queries.GetClients;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClientManagement.API.Controllers
{
    [ApiExceptionFilter]
    public class ClientsController : ApiControllerBase
    {
        [HttpGet]
        [ClientManagement.API.Services.TenantRequestPolicy(typeof(GetClientsQuery))]
        public async Task<ActionResult<ClientsDto>> GetAll()
        {
            var vm = await Mediator.Send(new GetClientsQuery());
            return Ok(vm);
        }


        [HttpPost("search")]
        [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ClientManagement.API.Services.TenantRequestPolicy(typeof(GetClientsBySearchTextQuery))]
        public async Task<ActionResult> GetBySearchText([FromBody, Required] GetClientsBySearchTextQuery query)
        {
            if (!ModelState.IsValid || query is null)
                return BadRequest();

            var vm = await Mediator.Send(query);
            return Ok(vm);
        }

        [HttpGet("{clientid}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ClientManagement.API.Services.TenantRequestPolicy(typeof(GetClientDetailQuery))]
        public async Task<ActionResult<ClientsDto>> Get(int clientid)
        {
            var vm = await Mediator.Send(new GetClientDetailQuery() { ClientId = clientid });

            return Ok(vm);
        }

        [HttpPost]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesDefaultResponseType]
        [ClientManagement.API.Services.TenantRequestPolicy(typeof(AddClientCommand))]
        public async Task<ActionResult> Add(AddClientCommand command)
        {
            if (!ModelState.IsValid || command is null)
                return BadRequest();

            var vm = await Mediator.Send(command);

            return Ok(vm);
        }

        [HttpPatch("native-language")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ClientManagement.API.Services.TenantRequestPolicy(typeof(UpdateNativeLanguageCommand))]
        public async Task<ActionResult> UpdateLanguage([FromBody, Required] UpdateNativeLanguageCommand command)
        {
            if (!ModelState.IsValid || command is null)
                return BadRequest();

            var vm = await Mediator.Send(command);

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
