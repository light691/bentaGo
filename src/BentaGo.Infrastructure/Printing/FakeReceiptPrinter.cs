using BentaGo.Core.Domain.Receipts;

namespace BentaGo.Infrastructure.Printing;

/// <summary>
/// The default printer behind <see cref="IReceiptPrinter"/> until the real
/// USB ESC/POS device pass (BG-007 software-first decision): it captures the
/// rendered documents so lanes and tests can assert what WOULD have printed,
/// and it can be switched to fail (an unplugged printer) or to delay (the
/// perf probe measures the transport). The adapter is the ONLY component that
/// talks to hardware; the app never sees the difference.
/// </summary>
public sealed class FakeReceiptPrinter : IReceiptPrinter
{
    private readonly object _gate = new();
    private readonly List<ReceiptDocument> _printed = new();
    private readonly List<byte[]> _encoded = new();
    private bool _failNext;
    private bool _alwaysFail;
    private TimeSpan _delay = TimeSpan.Zero;
    private int _encodeCalls;
    private readonly IReceiptEncoder _encoder = new EscPos80mmEncoder();

    public Task<ReceiptPrintResult> PrintAsync(ReceiptDocument document, CancellationToken cancellationToken = default)
    {
        if (_delay > TimeSpan.Zero)
        {
            Thread.Sleep(_delay);
        }

        lock (_gate)
        {
            if (_alwaysFail || _failNext)
            {
                _failNext = false;
                _encoded.Add(_encoder.Encode(document)); // the bytes ARE produced even on failure
                return Task.FromResult(ReceiptPrintResult.Failed(
                    "UNPLUGGED", DateTimeOffset.UtcNow));
            }

            _printed.Add(document);
            _encoded.Add(_encoder.Encode(document));
            _encodeCalls++;
            return Task.FromResult(ReceiptPrintResult.Printed(42, DateTimeOffset.UtcNow));
        }
    }

    /// <summary>Test/lane control: the next print fails (an unplugged printer).</summary>
    public void FailNext() => SetFail(always: false);

    /// <summary>Test/lane control: every print fails until cleared.</summary>
    public void SetFail(bool always = true) => Set(always);

    private void Set(bool always)
    {
        lock (_gate)
        {
            _alwaysFail = always;
            _failNext = !always;
        }
    }

    public void ClearFailures()
    {
        lock (_gate)
        {
            _alwaysFail = false;
            _failNext = false;
        }
    }

    /// <summary>Transport delay for the perf probe (fake-adapter latency).</summary>
    public void SetDelay(TimeSpan delay)
    {
        lock (_gate)
        {
            _delay = delay;
        }
    }

    public IReadOnlyList<ReceiptDocument> Printed
    {
        get
        {
            lock (_gate)
            {
                return _printed.ToArray();
            }
        }
    }

    public IReadOnlyList<byte[]> Encoded
    {
        get
        {
            lock (_gate)
            {
                return _encoded.ToArray();
            }
        }
    }

    public int EncodeCalls
    {
        get
        {
            lock (_gate)
            {
                return _encodeCalls;
            }
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _printed.Clear();
            _encoded.Clear();
            _alwaysFail = false;
            _failNext = false;
            _delay = TimeSpan.Zero;
            _encodeCalls = 0;
        }
    }
}
