using AdminAreaManagement.Application.Cities.Queries;
using AdminAreaManagement.Application.Common.Models;
using AdminAreaManagement.Application.Formations.Commands.CreateTraining;
using AdminAreaManagement.Application.Formations.Commands.DeleteTraining;
using AdminAreaManagement.Application.Formations.Commands.UpdateTraining;
using AdminAreaManagement.Application.Formations.Common;
using AdminAreaManagement.Application.Formations.Queries.GetTrainingList;
using AdminAreaManagement.Application.Nationalities.Commands.CreateCity;
using AdminAreaManagement.Application.Nationalities.Commands.DeleteNationality;
using AdminAreaManagement.Application.Nationalities.Commands.UpdateCity;
using AdminAreaManagement.Application.Nationalities.Queries;
using Microsoft.AspNetCore.Mvc;

namespace AdminAreaManagement.API.Controllers
{
    public class NationalitiesController : ApiControllerBase
    {
        [HttpGet]
        public async Task<ActionResult<PaginatedList<NationalityDto>>> GetAll([FromQuery] GetNationalitiesListQuery query)
        {
            return await Mediator.Send(query);
        }

        [HttpPost]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesDefaultResponseType]
        public async Task<IActionResult> Create(CreateNationalityCommand command)
        {
            var id = await Mediator.Send(command);

            return Ok(id);
        }

        [HttpPut]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesDefaultResponseType]
        public async Task<IActionResult> Update(UpdateNationalityCommand command)
        {
            var id = await Mediator.Send(command);

            return Ok(id);
        }

        [HttpDelete("{id}")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> Delete(int id)
        {
            await Mediator.Send(new DeleteNationalityCommand() { Id = id });

            return NoContent();
        }
    }
}