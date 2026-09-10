using AdminAreaManagement.Application.Common.Models;
using AdminAreaManagement.Application.Partners.Commands.CreatePartner;
using AdminAreaManagement.Application.Partners.Commands.DeletePartener;
using AdminAreaManagement.Application.Partners.Commands.UpdatePartner;
using AdminAreaManagement.Application.Partners.Queries.Common;
using AdminAreaManagement.Application.Partners.Queries.GetPartnerDetail;
using AdminAreaManagement.Application.Partners.Queries.GetPartners;
using AdminAreaManagement.Application.Partners.Queries.GetPartnersName;
using Microsoft.AspNetCore.Mvc;

namespace AdminAreaManagement.API.Controllers
{

    public class PartnersController : ApiControllerBase
    {
        [HttpGet]
        [AdminAreaManagement.API.Services.TenantRequestPolicy(typeof(GetPartnersListQuery))]
        public async Task<ActionResult<PaginatedList<PartnerDto>>> GetAll([FromQuery] GetPartnersListQuery query)
        {
            var vm = await Mediator.Send(query);
            return Ok(vm);
        }

        [HttpGet("selectionlist")]
        [AdminAreaManagement.API.Services.TenantRequestPolicy(typeof(GetPartnersSelectionListQuery))]
        public async Task<ActionResult<PaginatedList<PartnerSelectionListDto>>> GetSelectionList([FromQuery] GetPartnersSelectionListQuery query)
        {
            var vm = await Mediator.Send(query);
            return Ok(vm);
        }

        [HttpDelete("{id}")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [AdminAreaManagement.API.Services.TenantRequestPolicy(typeof(DeletePartnerCommand))]
        public async Task<IActionResult> Delete(int id)
        {
            await Mediator.Send(new DeletePartnerCommand { Id = id });

            return NoContent();
        }

        [HttpPost]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesDefaultResponseType]
        [AdminAreaManagement.API.Services.TenantRequestPolicy(typeof(CreatePartnerCommand))]
        public async Task<IActionResult> Create(CreatePartnerCommand command)
        {
            var id = await Mediator.Send(command);

            return Ok(id);
        }

        [HttpPut]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesDefaultResponseType]
        [AdminAreaManagement.API.Services.TenantRequestPolicy(typeof(UpdatePartnerCommand))]
        public async Task<IActionResult> Update(UpdatePartnerCommand command)
        {
            var id = await Mediator.Send(command);

            return Ok(id);
        }

        [HttpGet("{id}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [AdminAreaManagement.API.Services.TenantRequestPolicy(typeof(GetPartnerDetailQuery))]
        public async Task<ActionResult<PartnerDto>> Get(int id)
        {
            var vm = await Mediator.Send(new GetPartnerDetailQuery(){ Id = id });

            return Ok(vm);
        }
    }
}
