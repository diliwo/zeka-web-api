using AdminAreaManagement.Application.Common.Models;
using AdminAreaManagement.Application.Staffs.Commands.CreateStaffmember;
using AdminAreaManagement.Application.Staffs.Commands.DeleteStaff;
using AdminAreaManagement.Application.Staffs.Commands.UpdateStaffmember;
using AdminAreaManagement.Application.Staffs.Queries;
using Microsoft.AspNetCore.Mvc;

namespace AdminAreaManagement.API.Controllers
{
    public class StaffMemberController : ApiControllerBase
    {
        [HttpGet]
        [AdminAreaManagement.API.Services.TenantRequestPolicy(typeof(GetStaffMemberListQuery))]
        public async Task<ActionResult<PaginatedList<StaffMemberDto>>> GetAll([FromQuery] GetStaffMemberListQuery query)
        {
            return await Mediator.Send(query);
        }

        [HttpPost]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesDefaultResponseType]
        [AdminAreaManagement.API.Services.TenantRequestPolicy(typeof(CreateStaffMemberCommand))]
        public async Task<IActionResult> Upsert(CreateStaffMemberCommand command)
        {
            var id = await Mediator.Send(command);

            return Ok(id);
        }

        [HttpPut]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesDefaultResponseType]
        [AdminAreaManagement.API.Services.TenantRequestPolicy(typeof(UpdateStaffMemberCommand))]
        public async Task<IActionResult> Upsert(UpdateStaffMemberCommand command)
        {
            var id = await Mediator.Send(command);

            return Ok(id);
        }


        [HttpDelete("{id}")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [AdminAreaManagement.API.Services.TenantRequestPolicy(typeof(DeleteStaffMemberCommand))]
        public async Task<IActionResult> Delete(int id)
        {
            await Mediator.Send(new DeleteStaffMemberCommand { Id = id });

            return NoContent();
        }
    }
}
