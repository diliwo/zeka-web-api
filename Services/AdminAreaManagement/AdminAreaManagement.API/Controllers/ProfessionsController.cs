using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AdminAreaManagement.API.Controllers;
using AdminAreaManagement.Application.Common.Models;
using AdminAreaManagement.Application.Professions.Commands.DeleteProfession;
using AdminAreaManagement.Application.Professions.Commands.UpsertProfession;
using AdminAreaManagement.Application.Professions.Queries;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Cpas.Isp.Api.Controllers
{
    public class ProfessionsController : ApiControllerBase
    {
        [HttpGet]
        public async Task<ActionResult<PaginatedList<ProfessionDto>>> GetAll([FromQuery] GetProfessionsListQuery query)
        {
            var vm = await Mediator.Send(query);
            return Ok(vm);
        }

        [HttpPost]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesDefaultResponseType]
        public async Task<IActionResult> Upsert(UpsertProfessionCommand command)
        {
            var id = await Mediator.Send(command);

            return Ok(id);
        }

        [HttpDelete("{id}")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> Delete(int id)
        {
            await Mediator.Send(new DeleteProfessionCommand() { Id = id });

            return NoContent();
        }
    }
}
