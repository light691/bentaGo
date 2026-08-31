namespace BentaGo.Infrastructure.Persistence;

/// <summary>
/// Test seam for failure-injection buckets and lanes (BG-003 §4 B graft):
/// <see cref="FailNextAppendAfterInsert"/> makes lane 3 (rollback) and lane 8
/// (crash mid-transaction) deterministic at the unit level; the live lane
/// still uses a real child-process kill; <see cref="FailNextPrint"/> makes
/// C5 (failed print changes print state, not the sale) testable. Flags are
/// latched (consume-once).
/// </summary>
public sealed class DebugOptions
{
    private bool _failNextAppendAfterInsert;
    private bool _delayBeforeCommit;
    private bool _failNextPrint;

    /// <summary>
    /// The next Append(AppendBatch) rolls back AFTER its inserts and returns
    /// the FORCED_ROLLBACK DomainError; the flag then clears.
    /// </summary>
    public bool ConsumeFailNextAppendAfterInsert()
    {
        bool value = _failNextAppendAfterInsert;
        _failNextAppendAfterInsert = false;
        return value;
    }

    public void FailNextAppendAfterInsert() => _failNextAppendAfterInsert = true;

    /// <summary>Delays the next Append before COMMIT (crash window for lane 8).</summary>
    public bool ConsumeDelayBeforeCommit()
    {
        bool value = _delayBeforeCommit;
        _delayBeforeCommit = false;
        return value;
    }

    public void DelayBeforeCommit() => _delayBeforeCommit = true;

    /// <summary>
    /// Optional callback invoked right before COMMIT when the delay seam is
    /// armed: lane 8's crash-worker writes its ready-file there, proving the
    /// transaction is open with inserts done but uncommitted.
    /// </summary>
    public Action? PreCommit { get; set; }

    /// <summary>
    /// The next print fails (an injected hardware failure) so C5 — a failed
    /// print records PRINT_FAILED and changes nothing about the sale — is
    /// testable; the flag then clears.
    /// </summary>
    public bool ConsumeFailNextPrint()
    {
        bool value = _failNextPrint;
        _failNextPrint = false;
        return value;
    }

    public void FailNextPrint() => _failNextPrint = true;
}
