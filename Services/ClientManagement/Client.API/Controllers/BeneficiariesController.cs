using System.ComponentModel.DataAnnotations;
using ClientManagement.API.Filters;
using ClientManagement.API.Services;
using ClientManagement.Application.Clients.Queries.GetBeneficiaryIdentity;
using ClientManagement.Application.Clients.Queries.LookupBeneficiaries;
using Microsoft.AspNetCore.Mvc;

namespace ClientManagement.API.Controllers;

[ApiExceptionFilter]
public sealed class BeneficiariesController : ApiControllerBase
{
    [HttpPost("/api/v1/beneficiaries/lookup")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [TenantRequestPolicy(typeof(LookupBeneficiariesQuery))]
    [ProducesResponseType(typeof(BeneficiaryLookupDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<BeneficiaryLookupDto>> Lookup(
        [FromBody, Required] LookupBeneficiariesQuery query, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid || query is null) return BadRequest();
        return Ok(await Mediator.Send(query, cancellationToken));
    }

    [HttpGet("/api/v1/beneficiaries/{beneficiaryId:int}/identity")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [TenantRequestPolicy(typeof(GetBeneficiaryIdentityQuery))]
    [ProducesResponseType(typeof(BeneficiaryIdentityDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BeneficiaryIdentityDto>> GetIdentity(
        [FromRoute] int beneficiaryId, CancellationToken cancellationToken)
    {
        var identity = await Mediator.Send(new GetBeneficiaryIdentityQuery(beneficiaryId), cancellationToken);
        return identity is null ? NotFound() : Ok(identity);
    }
}
