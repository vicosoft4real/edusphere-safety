using EduSphere.Safety.Shared;

namespace EduSphere.Safety.Api;

/// <summary>
/// Normative Task 5 error mapping: not_found→404, concurrency_conflict→409,
/// validation.failed→400, other incident.* domain codes→422, anything else→500.
/// </summary>
public static class ErrorMapper
{
    public static int StatusCode(string code) => code switch
    {
        "incident.not_found" => StatusCodes.Status404NotFound,
        "incident.concurrency_conflict" => StatusCodes.Status409Conflict,
        "validation.failed" => StatusCodes.Status400BadRequest,
        "device.not_found" => StatusCodes.Status404NotFound,
        "device.enrol_invalid" => StatusCodes.Status400BadRequest,
        "device.campus_mismatch" => StatusCodes.Status403Forbidden,
        "camera.not_found" => StatusCodes.Status404NotFound,
        "evidence.not_found" => StatusCodes.Status404NotFound,
        _ when code.StartsWith("incident.", StringComparison.Ordinal) => StatusCodes.Status422UnprocessableEntity,
        _ => StatusCodes.Status500InternalServerError,
    };

    public static IResult ToResult(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return Results.Json(new { code = error.Code, message = error.Message }, statusCode: StatusCode(error.Code));
    }
}
