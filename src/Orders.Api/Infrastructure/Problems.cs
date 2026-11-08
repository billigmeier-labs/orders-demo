using Microsoft.AspNetCore.Http.HttpResults;

namespace Orders.Api.Infrastructure;

/// <summary>RFC 7807 problem responses used across the endpoints.</summary>
internal static class Problems
{
    public static ProblemHttpResult NotFound(string what, Guid id) =>
        TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: $"{what} not found.", detail: $"No {what.ToLowerInvariant()} has id {id}.");

    public static ProblemHttpResult Conflict(string detail) =>
        TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Conflict.", detail: detail);

    public static ValidationProblem Invalid(string field, string error) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { [field] = [error] });
}
