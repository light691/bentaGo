namespace BentaGo.Core.Domain;

/// <summary>
/// Money is a count of Philippine centavos in a <see cref="long"/>. No float
/// ever crosses the domain: prices are parsed once at the boundary by
/// <see cref="FromPesos"/> and every calculation stays in integer centavos.
/// </summary>
public readonly record struct Money(long Centavos)
{
    public static Money Zero => new(0);

    public static Money FromPesos(decimal pesos)
    {
        decimal scaled = pesos * 100m;
        if (scaled != decimal.Truncate(scaled))
        {
            throw new ArgumentException(
                $"Money accepts at most two decimal places: {pesos}", nameof(pesos));
        }

        return new Money((long)scaled);
    }

    public decimal ToPesos() => Centavos / 100m;

    public Money Plus(Money other) => new(checked(Centavos + other.Centavos));

    public Money Minus(Money other) => new(checked(Centavos - other.Centavos));

    public Money Times(int factor) => new(checked(Centavos * factor));

    public Money Negate() => new(checked(-Centavos));

    public bool IsNegative => Centavos < 0;

    public bool IsZero => Centavos == 0;

    public override string ToString() => ToPesos().ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
}
