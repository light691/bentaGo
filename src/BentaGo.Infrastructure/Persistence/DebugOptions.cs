namespace BentaGo.Infrastructure.Persistence;

/// <summary>
/// Test seam for failure-injection buckets and lanes (BG-003 §4 B graft):
/// <see cref="FailNextAppendAfterInsert"/> makes lane 3 (rollback) and lane 8
/// (crash mid-transaction) deterministic at the unit level; the live lane
/// still uses a real child-process kill. Flags are latched (consume-once).
/// </summary>
public sealed class DebugOptions
{
    private bool _failNextAppendAfterInsert;
    private bool _delayBeforeCommit;

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
}
