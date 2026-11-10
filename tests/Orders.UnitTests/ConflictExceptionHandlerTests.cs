using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Orders.Api.Infrastructure;

namespace Orders.UnitTests;

public class ConflictExceptionHandlerTests
{
    private sealed class CapturingProblemDetails : IProblemDetailsService
    {
        public ProblemDetailsContext? Context { get; private set; }

        public ValueTask WriteAsync(ProblemDetailsContext context)
        {
            Context = context;
            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> TryWriteAsync(ProblemDetailsContext context)
        {
            Context = context;
            return ValueTask.FromResult(true);
        }
    }

    private static async Task<(bool Handled, DefaultHttpContext Http, CapturingProblemDetails Problems)> Handle(Exception ex)
    {
        var problems = new CapturingProblemDetails();
        var http = new DefaultHttpContext();
        var handled = await new ConflictExceptionHandler(problems).TryHandleAsync(http, ex, CancellationToken.None);
        return (handled, http, problems);
    }

    [Fact]
    public async Task ConcurrencyException_Writes409WithRetryDetail()
    {
        var ex = new DbUpdateConcurrencyException("race");

        var (handled, http, problems) = await Handle(ex);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status409Conflict, http.Response.StatusCode);
        var pd = problems.Context!.ProblemDetails;
        Assert.Equal(StatusCodes.Status409Conflict, pd.Status);
        Assert.Equal("Conflict.", pd.Title);
        Assert.Equal("The data changed while handling the request. Try again.", pd.Detail);
        Assert.Same(ex, problems.Context.Exception);
    }

    [Fact]
    public async Task UniqueViolation_Writes409WithDuplicateDetail()
    {
        var inner = new PostgresException("duplicate", "ERROR", "ERROR", PostgresErrorCodes.UniqueViolation);

        var (handled, http, problems) = await Handle(new DbUpdateException("save failed", inner));

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status409Conflict, http.Response.StatusCode);
        var pd = problems.Context!.ProblemDetails;
        Assert.Equal("Conflict.", pd.Title);
        Assert.Equal("A record with the same unique value already exists.", pd.Detail);
    }

    [Fact]
    public async Task OtherDatabaseError_IsNotHandled()
    {
        var inner = new PostgresException("fk", "ERROR", "ERROR", PostgresErrorCodes.ForeignKeyViolation);

        var (handled, _, problems) = await Handle(new DbUpdateException("save failed", inner));

        Assert.False(handled);
        Assert.Null(problems.Context);
    }

    [Fact]
    public async Task UnrelatedException_IsNotHandled()
    {
        var (handled, _, problems) = await Handle(new InvalidOperationException("boom"));

        Assert.False(handled);
        Assert.Null(problems.Context);
    }
}
