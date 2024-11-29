using AdminAreaManagement.Application.Common.Models;
using AdminAreaManagement.Application.TrainingFields.Common;
using AdminAreaManagement.Application.TrainingFields.Queries.GetTrainingFieldList;
using Microsoft.AspNetCore.Mvc;

namespace AdminAreaManagement.API.Controllers
{
    public class TrainingFieldController : ApiControllerBase
    {
        [HttpGet]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesDefaultResponseType]
        public async Task<ActionResult<PaginatedList<TrainingFieldDto>>> GetTypes([FromQuery] GetTrainingFieldListQuery query)
        {
            return await Mediator.Send(query);
        }

    }
}