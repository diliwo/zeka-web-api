using System.Globalization;
using System.Text.RegularExpressions;
using ClientManagement.API.LocalDevelopment;
using ClientManagement.Application.BeneficiaryB1;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Infrastructure.IntegrationTests;

// Deliberately compiled only into the test assembly. Client.API does not reference this assembly.
[ApiController]
[Route("api/v1/beneficiaries/{beneficiaryId}")]
public sealed partial class BeneficiaryB1TestController(BeneficiaryB1Service service) : ControllerBase
{
    [GeneratedRegex("^[1-9][0-9]*$", RegexOptions.CultureInvariant)]
    private static partial Regex PositiveDecimal();

    [HttpGet("context")]
    public async Task<IActionResult> Context(string beneficiaryId, CancellationToken token) =>
        Ok(await service.ContextAsync(Id(beneficiaryId), token));

    [HttpGet("interactions")]
    public async Task<IActionResult> Recent(string beneficiaryId, CancellationToken token)
    {
        var id = Id(beneficiaryId);
        var limit = 20;
        if (Request.Query.TryGetValue("limit", out var values))
        {
            if (values.Count != 1) throw new BeneficiaryWireException("INVALID_REQUEST");
            limit = Id(values[0] ?? string.Empty);
        }
        if (limit > 50 || Request.Query.Keys.Any(k => k is not ("limit" or "cursor")))
            throw new BeneficiaryWireException("INVALID_REQUEST");
        string? cursor = null;
        if (Request.Query.TryGetValue("cursor", out var cursors))
        {
            if (cursors.Count != 1) throw new BeneficiaryWireException("INVALID_CURSOR");
            cursor = cursors[0];
        }
        return Ok(await service.RecentAsync(id, limit, cursor, token));
    }

    [HttpGet("interactions/{interactionId}")]
    public async Task<IActionResult> Direct(string beneficiaryId, string interactionId, CancellationToken token) =>
        Ok(await service.DirectAsync(Id(beneficiaryId), Id(interactionId), token));

    [HttpGet("interactions/{interactionId}/revisions")]
    public async Task<IActionResult> Revisions(string beneficiaryId, string interactionId,
        CancellationToken token) =>
        Ok(await service.RevisionsAsync(Id(beneficiaryId), Id(interactionId), token));

    [HttpPost("interactions")]
    public async Task<IActionResult> Record(string beneficiaryId, CancellationToken token) =>
        StatusCode(StatusCodes.Status201Created,
            await BeneficiaryInteractionWire.RecordAsync(Request, beneficiaryId, service, token));

    private static int Id(string text) => PositiveDecimal().IsMatch(text)
        && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
        && value > 0 ? value : throw new BeneficiaryWireException("INVALID_REQUEST");
}

public sealed class BeneficiaryB1ProblemMiddleware(RequestDelegate next)
{
    internal static bool IsClosureFence(Exception error) => BeneficiaryInteractionWire.IsClosureFence(error);

    public async Task InvokeAsync(HttpContext http)
    {
        try { await next(http); }
        catch (Exception error) when (BeneficiaryInteractionWire.IsProblem(error))
        {
            await BeneficiaryInteractionWire.WriteProblemAsync(http, error);
        }
    }
}
