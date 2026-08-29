using BentaGo.Application.Contracts;
using BentaGo.Application.Services;
using BentaGo.Core.Common;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Commands;
using BentaGo.Core.Domain.Payments;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BentaGo.Tests.Sales;

public class ReceiptNumberPolicyTests : SalesTestBase
{
    [Fact]
    public async Task Committed_Counts_Day_Prefix_Only()
    {
        var (host, trip, _, user, productId, _) = await SeededAsync();
        var numbers = new CommittedSaleNumberSource(host.Store);

        // Seed a 15th-of-January sale directly through the use case.
        NewSaleCommand sale = OneUnitSale(trip, productId, "R-20260115-001", user);
        Result<CheckoutOutcome> outcome = host.Checkout.Checkout(new CheckoutKey(Guid.NewGuid()), sale);
        Assert.True(outcome.IsSuccess, outcome.Errors.FirstOrDefault()?.Code ?? "seed sale failed");

        Assert.Equal("R-20260115-002", numbers.Next(SeedWhen.AddHours(2), Array.Empty<string>()));
    }

    [Fact]
    public async Task Other_Day_Prefixes_Do_Not_Count()
    {
        var (host, trip, _, user, productId, _) = await SeededAsync();
        var numbers = new CommittedSaleNumberSource(host.Store);

        NewSaleCommand sale = OneUnitSale(trip, productId, "R-20260114-001", user);
        Result<CheckoutOutcome> outcome = host.Checkout.Checkout(new CheckoutKey(Guid.NewGuid()), sale);
        Assert.True(outcome.IsSuccess);

        Assert.Equal("R-20260115-001", numbers.Next(SeedWhen.AddHours(2), Array.Empty<string>()));
    }

    [Fact]
    public void Fixed_Skips_Already_Attempted()
    {
        var numbers = new FixedNumberSource(new Queue<string>(new[] { "R-A", "R-B", "R-C" }));
        // R-A is already attempted (a prior collision) -> the source must not
        // hand out the same number again; it pops the next.
        Assert.Equal("R-B", numbers.Next(SeedWhen, new[] { "R-A" }.ToList()));
        Assert.Equal("R-C", numbers.Next(SeedWhen, Array.Empty<string>()));
    }
}
