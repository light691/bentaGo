namespace BentaGo.Core.Domain;

/// <summary>
/// A whole, non-negative number of units. The only public constructor is
/// <see cref="Of"/>, which rejects negatives, so a negative quantity is
/// unrepresentable at the type level.
/// </summary>
public readonly struct Quantity : IEquatable<Quantity>
{
    private readonly int _units;

    private Quantity(int units) => _units = units;

    public int Units => _units;

    public static Quantity Zero => new(0);

    public static Quantity Of(int units) =>
        units >= 0
            ? new Quantity(units)
            : throw new ArgumentOutOfRangeException(nameof(units), $"Quantity cannot be negative: {units}");

    public Quantity Plus(Quantity other) => new(checked(_units + other._units));

    public bool IsZero => _units == 0;

    public bool Equals(Quantity other) => _units == other._units;

    public override bool Equals(object? obj) => obj is Quantity other && Equals(other);

    public override int GetHashCode() => _units;

    public static bool operator ==(Quantity left, Quantity right) => left.Equals(right);

    public static bool operator !=(Quantity left, Quantity right) => !left.Equals(right);

    public override string ToString() => _units.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
