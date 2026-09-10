using AdminAreaManagement.Application.Cities.Commands.CreateCity;
using AdminAreaManagement.Application.Cities.Commands.DeleteClity;
using AdminAreaManagement.Application.Cities.Commands.UpdateCity;
using AdminAreaManagement.Application.Cities.Queries;
using AdminAreaManagement.Application.Common.Models;
using AdminAreaManagement.Application.Formations.Commands.CreateTraining;
using AdminAreaManagement.Application.Formations.Commands.DeleteTraining;
using AdminAreaManagement.Application.Formations.Commands.UpdateTraining;
using AdminAreaManagement.Application.Formations.Common;
using AdminAreaManagement.Application.Formations.Queries.GetTrainingList;
using Microsoft.AspNetCore.Mvc;

namespace AdminAreaManagement.API.Controllers
{
    public class CitiesController : ApiControllerBase
    {
        [HttpGet]
        [AdminAreaManagement.API.Services.TenantRequestPolicy(typeof(GetCitiesListQuery))]
        public async Task<ActionResult<PaginatedList<CityDto>>> GetAll([FromQuery] GetCitiesListQuery query)
        {
            return await Mediator.Send(query);
        }

        [HttpPost]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesDefaultResponseType]
        [AdminAreaManagement.API.Services.TenantRequestPolicy(typeof(CreateCityCommand))]
        public async Task<IActionResult> Create(CreateCityCommand command)
        {
            var id = await Mediator.Send(command);

            return Ok(id);
        }

        [HttpPut]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesDefaultResponseType]
        [AdminAreaManagement.API.Services.TenantRequestPolicy(typeof(UpdateCityCommand))]
        public async Task<IActionResult> Update(UpdateCityCommand command)
        {
            var id = await Mediator.Send(command);

            return Ok(id);
        }

        [HttpDelete("{id}")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [AdminAreaManagement.API.Services.TenantRequestPolicy(typeof(DeleteCityCommand))]
        public async Task<IActionResult> Delete(int id)
        {
            await Mediator.Send(new DeleteCityCommand() { Id = id });

            return NoContent();
        }
    }
}
