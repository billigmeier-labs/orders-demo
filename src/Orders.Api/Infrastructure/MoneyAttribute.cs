using System.ComponentModel.DataAnnotations;

namespace Orders.Api.Infrastructure;

/// <summary>A positive USD amount with at most 2 decimal places, up to 1,000,000.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
internal sealed class MoneyAttribute : ValidationAttribute
{
    public MoneyAttribute()
        : base("The {0} field must be greater than 0, at most 1000000, with at most 2 decimal places.")
    {
    }

    public override bool IsValid(object? value) =>
        value is null || value is decimal amount && amount > 0 && amount <= 1_000_000 && decimal.Round(amount, 2) == amount;
}
