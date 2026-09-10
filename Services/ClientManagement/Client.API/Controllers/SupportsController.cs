using ClientManagement.Application.Common.Models;
using ClientManagement.Application.Supports.Commands.CloseTrack;
using ClientManagement.Application.Supports.Commands.DeleteSupport;
using ClientManagement.Application.Supports.Commands.UpsertSupport;
using ClientManagement.Application.Supports.Queries;
using ClientManagement.Application.Supports.Queries.GetSupportsByReferents;
using ClientManagement.Core.Common.Dto;
using ClientManagement.Core.Interfaces;
using ClientManagement.Core.ValueObjects;
using Microsoft.AspNetCore.Mvc;

namespace ClientManagement.API.Controllers
{
    public class SupportsController : ApiControllerBase
    {
        [HttpGet]
        [ClientManagement.API.Services.TenantRequestPolicy(typeof(GetSupportsListByClientQuery))]
        public async Task<PaginatedList<SupportDto>> GetAllByBeneficiaryId([FromQuery] GetSupportsListByClientQuery query)
        {
            return  await Mediator.Send(query);

        }

        [HttpGet("mysupports")]
        [ClientManagement.API.Services.TenantRequestPolicy(typeof(GetSupportsBySocialWorkersQuery))]
        public async Task<PaginatedList<MySupportDto>> GetSupportsByReferent([FromQuery] GetSupportsBySocialWorkersQuery query)
        {
            return await Mediator.Send(query);
        }


        [HttpPost]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesDefaultResponseType]
        [ClientManagement.API.Services.TenantRequestPolicy(typeof(UpsertSupportCommand))]
        public async Task<IActionResult> Upsert(UpsertSupportCommand command)
        {
            var id = await Mediator.Send(command);

            return Ok(id);
        }

        [HttpPut("close")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesDefaultResponseType]
        [ClientManagement.API.Services.TenantRequestPolicy(typeof(CloseSupportCommand))]
        public async Task<IActionResult> Close(CloseSupportCommand command)
        {
            var id = await Mediator.Send(command);

            return Ok(id);
        }

        [HttpDelete("{id}")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ClientManagement.API.Services.TenantRequestPolicy(typeof(DeleteSupportCommand))]
        public async Task<IActionResult> Delete(int id)
        {
            await Mediator.Send(new DeleteSupportCommand { Id = id });

            return NoContent();
        }

        [HttpGet("reasons")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ClientManagement.API.Services.TenantRequestPolicy(typeof(GetSupportClosureReasonsQuery))]
        public async Task<IActionResult> GetRewards() => Ok(await Mediator.Send(new GetSupportClosureReasonsQuery()));
    }
}
