using AdminAreaManagement.Application.Common.Models;
using AdminAreaManagement.Application.Professions.Commands.DeleteProfession;
using AdminAreaManagement.Application.Professions.Commands.UpsertProfession;
using AdminAreaManagement.Application.Professions.Queries;
using Microsoft.AspNetCore.Mvc;

namespace AdminAreaManagement.API.Controllers
{
    public class ProfessionsController : ApiControllerBase
    {
        [HttpGet]
        [AdminAreaManagement.API.Services.TenantRequestPolicy(typeof(GetProfessionsListQuery))]
        public async Task<ActionResult<PaginatedList<ProfessionDto>>> GetAll([FromQuery] GetProfessionsListQuery query)
        {
            var vm = await Mediator.Send(query);
            return Ok(vm);
        }

        [HttpPost]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesDefaultResponseType]
        [AdminAreaManagement.API.Services.TenantRequestPolicy(typeof(UpsertProfessionCommand))]
        public async Task<IActionResult> Upsert(UpsertProfessionCommand command)
        {
            var id = await Mediator.Send(command);

            return Ok(id);
        }

        [HttpDelete("{id}")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [AdminAreaManagement.API.Services.TenantRequestPolicy(typeof(DeleteProfessionCommand))]
        public async Task<IActionResult> Delete(int id)
        {
            await Mediator.Send(new DeleteProfessionCommand() { Id = id });

            return NoContent();
        }
    }
}
