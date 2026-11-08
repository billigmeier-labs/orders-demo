using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Orders.Api.Data;
using Orders.Api.Domain;
using Orders.Api.Features.Orders;
using Orders.Api.Infrastructure;

namespace Orders.Api.Features.Customers;

public sealed record CustomerResponse(Guid Id, string Name, string Email, DateTimeOffset CreatedAt)
{
    internal static CustomerResponse From(Customer c) => new(c.Id, c.Name, c.Email, c.CreatedAt);
}

public sealed record CreateCustomerRequest(
    [Required, StringLength(200)] string Name,
    [Required, EmailAddress, StringLength(320)] string Email);

internal static class CustomerEndpoints
{
    public static void MapCustomers(this IEndpointRouteBuilder api)
    {
        var customers = api.MapGroup("/customers").WithTags("Customers");
        customers.MapGet("/", List);
        customers.MapGet("/{id:guid}", Get);
        customers.MapGet("/{id:guid}/orders", ListOrders);
        customers.MapPost("/", Create);
    }

    private static async Task<Ok<List<CustomerResponse>>> List(OrdersDbContext db, CancellationToken ct) =>
        TypedResults.Ok(await db.Customers
            .OrderBy(c => c.Name)
            .Select(c => new CustomerResponse(c.Id, c.Name, c.Email, c.CreatedAt))
            .ToListAsync(ct));

    private static async Task<Results<Ok<CustomerResponse>, ProblemHttpResult>> Get(Guid id, OrdersDbContext db, CancellationToken ct) =>
        await db.Customers.FindAsync([id], ct) is { } customer
            ? TypedResults.Ok(CustomerResponse.From(customer))
            : Problems.NotFound("Customer", id);

    private static async Task<Results<Ok<PagedResponse<OrderSummaryResponse>>, ProblemHttpResult>> ListOrders(
        Guid id,
        OrdersDbContext db,
        CancellationToken ct,
        [Range(1, 100_000)] int page = 1,
        [Range(1, 100)] int pageSize = 20)
    {
        if (!await db.Customers.AnyAsync(c => c.Id == id, ct))
        {
            return Problems.NotFound("Customer", id);
        }

        return TypedResults.Ok(await OrderEndpoints.QueryAsync(db, null, id, page, pageSize, ct));
    }

    private static async Task<Results<Created<CustomerResponse>, ProblemHttpResult>> Create(
        CreateCustomerRequest request, OrdersDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        if (await db.Customers.AnyAsync(c => c.Email == email, ct))
        {
            return Problems.Conflict($"A customer with email {email} already exists.");
        }

        var customer = new Customer(Guid.CreateVersion7(), request.Name.Trim(), email, clock.GetUtcNow());
        db.Customers.Add(customer);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/customers/{customer.Id}", CustomerResponse.From(customer));
    }
}
