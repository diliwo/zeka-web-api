using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClientManagement.Application.ActionWrites;
using ClientManagement.Application.BeneficiaryB1;
using ClientManagement.Application.Common.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Infrastructure.IntegrationTests;

// Deliberately compiled only into the test assembly. Client.API does not reference this assembly.
[ApiController]
[Route("api/v1/beneficiaries/{beneficiaryId}")]
public sealed partial class BeneficiaryB1TestController(BeneficiaryB1Service service) : ControllerBase
{
    [GeneratedRegex("^[1-9][0-9]*$", RegexOptions.CultureInvariant)]
    private static partial Regex PositiveDecimal();
    [GeneratedRegex("^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\\.[0-9]{1,3})?Z$",
        RegexOptions.CultureInvariant)]
    private static partial Regex UtcInstant();

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
    public async Task<IActionResult> Record(string beneficiaryId, CancellationToken token)
    {
        var id = Id(beneficiaryId);
        if (Request.ContentLength is > 8192 || Request.ContentType is null
            || !StringComparer.OrdinalIgnoreCase.Equals(Request.ContentType.Split(';', 2)[0].Trim(), "application/json"))
            throw new BeneficiaryWireException(Request.ContentLength is > 8192 ? "REQUEST_TOO_LARGE" : "INVALID_REQUEST");
        using var body = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var read = await Request.Body.ReadAsync(buffer, token);
            if (read == 0) break;
            if (body.Length + read > 8192) throw new BeneficiaryWireException("REQUEST_TOO_LARGE");
            body.Write(buffer, 0, read);
        }
        JsonDocument document;
        try { document = JsonDocument.Parse(body.ToArray()); }
        catch (JsonException) { throw new BeneficiaryWireException("INVALID_REQUEST"); }
        using (document)
        {
            var root = document.RootElement;
            JsonElement action = default;
            var linked = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("action", out action);
            if (linked && Request.Query.Count != 0)
                throw new BeneficiaryWireException("INVALID_REQUEST");
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != (linked ? 4 : 3)
                || !root.TryGetProperty("occurredAtUtc", out var occurred)
                || !root.TryGetProperty("conciseNote", out var note)
                || !root.TryGetProperty("idempotencyKey", out var key)
                || occurred.ValueKind != JsonValueKind.String || note.ValueKind != JsonValueKind.String
                || key.ValueKind != JsonValueKind.String)
                throw new BeneficiaryWireException("INVALID_REQUEST");
            var timestamp = linked ? WireString(occurred) : occurred.GetString() ?? string.Empty;
            if (!UtcInstant().IsMatch(timestamp) || !DateTime.TryParseExact(timestamp,
                    ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFF'Z'"],
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var utc))
                throw new BeneficiaryWireException("INVALID_REQUEST");
            if (linked)
                return StatusCode(StatusCodes.Status201Created, await service.RecordLinkedAsync(id, utc,
                    WireString(note), WireString(key), LinkedAction(action), token));
            var result = await service.RecordAsync(id, utc, note.GetString()!, key.GetString()!, token);
            return StatusCode(StatusCodes.Status201Created, result);
        }
    }

    private static FollowUpActionInput LinkedAction(JsonElement action)
    {
        if (action.ValueKind != JsonValueKind.Object || action.EnumerateObject().Count() != 3
            || !action.TryGetProperty("purpose", out var purpose)
            || !action.TryGetProperty("dueLocalDate", out var due)
            || !action.TryGetProperty("assigneeMembershipId", out var assignee)
            || purpose.ValueKind != JsonValueKind.String || due.ValueKind != JsonValueKind.String
            || assignee.ValueKind != JsonValueKind.String)
            throw new BeneficiaryWireException("INVALID_REQUEST");
        var date = WireString(due);
        var membership = WireString(assignee);
        if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var day) || day == default
            || day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) != date
            || !Guid.TryParseExact(membership, "D", out var member) || member == Guid.Empty
            || member.ToString("D") != membership)
            throw new BeneficiaryWireException("INVALID_REQUEST");
        return new FollowUpActionInput(WireString(purpose), day, member);
    }

    private static string WireString(JsonElement value)
    {
        try { return value.GetString() ?? string.Empty; }
        catch (InvalidOperationException) { throw new BeneficiaryWireException("INVALID_REQUEST"); }
        catch (ArgumentException) { throw new BeneficiaryWireException("INVALID_REQUEST"); }
    }

    private static int Id(string text) => PositiveDecimal().IsMatch(text)
        && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
        && value > 0 ? value : throw new BeneficiaryWireException("INVALID_REQUEST");
}

public sealed class BeneficiaryB1ProblemMiddleware(RequestDelegate next)
{
    internal static bool IsClosureFence(Exception error) => error is
        InvalidOperationException { Message: "organisation_closure_fence_active" }
        or PostgresException { SqlState: "P0001", MessageText: "organisation_closure_fence_active" }
        or DbUpdateException
        {
            InnerException: PostgresException
            {
                SqlState: "P0001", MessageText: "organisation_closure_fence_active"
            }
        };

    public async Task InvokeAsync(HttpContext http)
    {
        try { await next(http); }
        catch (Exception error) when (error is BeneficiaryWireException or TenantAccessException
            or ActionWriteConflictException or TenantCommitIndeterminateException
            || IsClosureFence(error))
        {
            var status = error switch
            {
                BeneficiaryWireException { Code: "REQUEST_TOO_LARGE" } => 413,
                BeneficiaryWireException { Code: "NOT_FOUND" } => 404,
                BeneficiaryWireException => 400,
                TenantAccessException { Failure: AccessFailure.Unauthenticated } => 401,
                TenantAccessException { Failure: AccessFailure.Unavailable } => 503,
                TenantAccessException => 403,
                ActionWriteConflictException { Code: "CONTEXT_REQUIRED" or "CONTEXT_CONFLICT" or "IDEMPOTENCY_CONFLICT" }
                    => 409,
                ActionWriteConflictException => 400,
                _ => 503
            };
            http.Response.Clear();
            http.Response.StatusCode = status;
            http.Response.ContentType = "application/problem+json";
            await http.Response.WriteAsync(JsonSerializer.Serialize(new
            {
                type = "about:blank",
                title = status switch
                {
                    400 => "Invalid request", 401 => "Unauthenticated", 403 => "Forbidden",
                    404 => "Not found", 409 => "Conflict", 413 => "Request too large",
                    _ => "Service unavailable"
                },
                status
            }), http.RequestAborted);
        }
    }
}
