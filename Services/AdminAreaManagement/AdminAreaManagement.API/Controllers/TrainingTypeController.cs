using AdminAreaManagement.Application.Common.Models;
using AdminAreaManagement.Application.TrainingTypes.Commands.CreateTrainingType;
using AdminAreaManagement.Application.TrainingTypes.Commands.DeleteTrainingType;
using AdminAreaManagement.Application.TrainingTypes.Commands.UpdateTrainingType;
using AdminAreaManagement.Application.TrainingTypes.Common;
using AdminAreaManagement.Application.TrainingTypes.Queries.GetTrainingTypeList;
using Microsoft.AspNetCore.Mvc;

namespace AdminAreaManagement.API.Controllers
{
    public class TrainingTypeController : ApiControllerBase
    {

        [HttpGet]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesDefaultResponseType]
        public async Task<ActionResult<PaginatedList<TrainingTypeDto>>> GetTypes([FromQuery] GetTrainingTypeListQuery query)
        {
            return await Mediator.Send(query);
        }

        [HttpPost]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesDefaultResponseType]
        public async Task<IActionResult> Create(CreateTrainingTypeCommand command)
        {
            var id = await Mediator.Send(command);

            return Ok(id);
        }

        [HttpPut]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesDefaultResponseType]
        public async Task<IActionResult> Update(UpdateTrainingTypeCommand command)
        {
            var id = await Mediator.Send(command);

            return Ok(id);
        }

        [HttpDelete("{id}")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> Delete(int id)
        {
            await Mediator.Send(new DeleteTrainingTypeCommand() { Id = id });

            return NoContent();
        }
    }
}