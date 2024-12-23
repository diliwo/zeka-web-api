using AdminAreaManagement.Application.Common.Models;
using AdminAreaManagement.Application.Teams.Commands.DeleteTeam;
using AdminAreaManagement.Application.Teams.Commands.UpsertTeam;
using AdminAreaManagement.Application.Teams.Queries;
using Microsoft.AspNetCore.Mvc;

namespace AdminAreaManagement.API.Controllers
{
    public class TeamsController : ApiControllerBase
    {
        [HttpGet]
        public async Task<ActionResult<PaginatedList<TeamDto>>> GetAll([FromQuery] GetTeamsListQuery query)
        {
            var vm = await Mediator.Send(query);
            return Ok(vm);
        }

        [HttpPost]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesDefaultResponseType]
        public async Task<IActionResult> Upsert(UpsertTeamCommand command)
        {
            var id = await Mediator.Send(command);

            return Ok(id);
        }

        [HttpDelete("{id}")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> Delete(int id)
        {
            await Mediator.Send(new DeleteTeamCommand { Id = id });

            return NoContent();
        }

        [HttpGet("{name}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult<Boolean>> Get(string name)
        {
            var vm = await Mediator.Send(new GetTeamsByNameQuery() { Name = name });

            return Ok(vm);
        }
    }
}
