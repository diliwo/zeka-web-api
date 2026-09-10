using AdminAreaManagement.Application.Common.Models;
using AdminAreaManagement.Application.Formations.Commands.CreateTraining;
using AdminAreaManagement.Application.Formations.Commands.DeleteTraining;
using AdminAreaManagement.Application.Formations.Commands.UpdateTraining;
using AdminAreaManagement.Application.Formations.Common;
using AdminAreaManagement.Application.Formations.Queries.GetTrainingList;
using Microsoft.AspNetCore.Mvc;

namespace AdminAreaManagement.API.Controllers
{
    public class TrainingsController : ApiControllerBase
    {
        [HttpGet]
        [AdminAreaManagement.API.Services.TenantRequestPolicy(typeof(GetTrainingListQuery))]
        public async Task<ActionResult<PaginatedList<TrainingDto>>> GetAll([FromQuery] GetTrainingListQuery query)
        {
            return await Mediator.Send(query);
        }

        [HttpPost]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesDefaultResponseType]
        [AdminAreaManagement.API.Services.TenantRequestPolicy(typeof(CreateTrainingCommand))]
        public async Task<IActionResult> Create(CreateTrainingCommand command)
        {
            var id = await Mediator.Send(command);

            return Ok(id);
        }

        [HttpPut]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesDefaultResponseType]
        [AdminAreaManagement.API.Services.TenantRequestPolicy(typeof(UpdateTrainingCommand))]
        public async Task<IActionResult> Update(UpdateTrainingCommand command)
        {
            var id = await Mediator.Send(command);

            return Ok(id);
        }

        [HttpDelete("{id}")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [AdminAreaManagement.API.Services.TenantRequestPolicy(typeof(DeleteTrainingCommand))]
        public async Task<IActionResult> Delete(int id)
        {
            await Mediator.Send(new DeleteTrainingCommand() { Id = id });

            return NoContent();
        }
    }
}
