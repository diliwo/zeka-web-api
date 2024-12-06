using AdminAreaManagement.Application.Common.Models;
using AdminAreaManagement.Application.Schools.Commands.CreateSchool;
using AdminAreaManagement.Application.Schools.Commands.DeleteSchool;
using AdminAreaManagement.Application.Schools.Commands.UpdateSchool;
using AdminAreaManagement.Application.Schools.Common;
using AdminAreaManagement.Application.Schools.Queries.GetSchoolList;
using Microsoft.AspNetCore.Mvc;

namespace AdminAreaManagement.API.Controllers
{
    public class SchoolsController : ApiControllerBase
    {
        [HttpGet]
        public async Task<PaginatedList<SchoolDto>> GetAll([FromQuery] GetSchoolListQuery query)
        {
            return await Mediator.Send(query);
        }

        [HttpPost]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesDefaultResponseType]
        public async Task<IActionResult> Create(CreateSchoolCommand command)
        {
            var id = await Mediator.Send(command);

            return Ok(id);
        }

        [HttpPut]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesDefaultResponseType]
        public async Task<IActionResult> Update(UpdateSchoolCommand command)
        {
            var id = await Mediator.Send(command);

            return Ok(id);
        }

        [HttpDelete("{id}")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> Delete(int id)
        {
            await Mediator.Send(new DeleteSchoolCommand() { SchoolId = id });

            return NoContent();
        }
    }
}
