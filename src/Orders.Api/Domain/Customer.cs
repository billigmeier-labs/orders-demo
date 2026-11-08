namespace Orders.Api.Domain;

internal sealed class Customer
{
    public Customer(Guid id, string name, string email, DateTimeOffset createdAt)
    {
        Id = id;
        Name = name;
        Email = email;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }
    public string Name { get; private set; }
    public string Email { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
}
