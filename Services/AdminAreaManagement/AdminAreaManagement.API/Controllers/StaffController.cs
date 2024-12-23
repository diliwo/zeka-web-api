using AdminAreaManagement.Application.Common.Models;
using AdminAreaManagement.Application.Staffs.Commands.DeleteStaff;
using AdminAreaManagement.Application.Staffs.Commands.UpsertStaff;
using AdminAreaManagement.Application.Staffs.Queries;
using Microsoft.AspNetCore.Mvc;

namespace AdminAreaManagement.API.Controllers
{
    public class StaffMemberController : ApiControllerBase
    {
        [HttpGet]
        public async Task<ActionResult<PaginatedList<StaffMemberDto>>> GetAll([FromQuery] GetStaffMemberListQuery query)
        {
            return await Mediator.Send(query);
        }

        [HttpPost]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesDefaultResponseType]
        public async Task<IActionResult> Upsert(UpsertStaffMemberCommand command)
        {
            var id = await Mediator.Send(command);

            return Ok(id);
        }

        
        [HttpDelete("{id}")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> Delete(int id)
        {
            await Mediator.Send(new DeleteStaffMemberCommand { Id = id });

            return NoContent();
        }
    }
}
