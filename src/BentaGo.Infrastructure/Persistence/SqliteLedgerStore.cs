using BentaGo.Application.Contracts;
using BentaGo.Core.Common;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Builders;
using BentaGo.Core.Domain.Documents;
using BentaGo.Core.Domain.Ledger;
using BentaGo.Core.Domain.Payments;
using BentaGo.Infrastructure.Configuration;
using Microsoft.Data.Sqlite;

namespace BentaGo.Infrastructure.Persistence;

/// <summary>
/// The SQLite implementation of <see cref="ILedgerStore"/> (BG-003 D2/D3):
/// one long-lived connection (Pooling=False), WAL + synchronous=FULL +
/// busy_timeout=5000, a hot <see cref="LedgerState"/> as the guard/report
/// engine, and five durable projection tables written in the SAME
/// <see cref="Append(AppendBatch)"/> transaction from the validated fold
/// clone. Movements are the source of truth; <see cref="RebuildAsync"/>
/// replays them through the real <see cref="LedgerState.Fold"/> and rewrites
/// the projections.
///
/// Lock discipline (BG-003 §6): the write path issues a literal BEGIN
/// IMMEDIATE (Microsoft.Data.Sqlite.BeginTransaction is deferred, which
/// under WAL can busy-snapshot in the two-writers race), reads the
/// LedgerHead watermark INSIDE the transaction, re-folds from movements when
/// it is stale, and runs the fold guards against the re-folded state before
/// inserting anything. Two competing appends therefore can never both
/// validate against the same pre-commit state.
/// </summary>
public sealed partial class SqliteLedgerStore : ILedgerStore, IDisposable, IAsyncDisposable
{
    private const int BusyTimeoutMs = 5000;
    private const long DefaultDelayBeforeCommitMs = 3000;

    private readonly IDataPathProvider _paths;
    private readonly DebugOptions _debug;
    private readonly string _connectionString;

    private SqliteConnection? _connection;
    private bool _inTransaction;
    private LedgerContext _context;

    private LedgerState _state = new();
    private readonly List<Movement> _movements = new();
    private readonly List<AuditEvent> _audits = new();

    // Committed document sets, loaded at Bind/refresh; authoritative reads
    // happen inside the append transaction on a stale head. The day-lifecycle
    // documents are movement-less and commit immediately at Register into the
    // day tables (BG-P1 baseline); a refresh reloads them from the database.
    private readonly List<DayOpen> _docsDayOpens = new();
    private readonly List<DayClose> _docsDayCloses = new();
    private readonly List<DayReopen> _docsDayReopens = new();
    private IReadOnlyList<Sale> _docsSales = Array.Empty<Sale>();
    private IReadOnlyList<Return> _docsReturns = Array.Empty<Return>();
    private IReadOnlyList<SaleVoid> _docsVoids = Array.Empty<SaleVoid>();
    private IReadOnlyList<PhysicalCount> _docsCounts = Array.Empty<PhysicalCount>();
    private IReadOnlyList<GoodsReceipt> _docsGoodsReceipts = Array.Empty<GoodsReceipt>();

    // Open-attempt staging: documents registered since the last Bind (D5).
    private readonly List<Sale> _stagedSales = new();
    private readonly List<Return> _stagedReturns = new();
    private readonly List<SaleVoid> _stagedVoids = new();
    private readonly List<Adjustment> _stagedAdjustments = new();
    private readonly List<PhysicalCount> _stagedCounts = new();
    private readonly List<GoodsReceipt> _stagedGoodsReceipts = new();

    private long _headLastSequence;
    private long _headDocumentsVersion;
    private bool _synced;

    public SqliteLedgerStore(IDataPathProvider paths, DebugOptions? debug = null)
    {
        _paths = paths;
        _debug = debug ?? new DebugOptions();
        _connectionString = $"Data Source={DbPath()};Pooling=False";
        _context = new LedgerContext(_state, _docsSales, _docsReturns, _docsVoids, _docsCounts, _docsDayOpens, _docsDayCloses, _docsDayReopens, _docsGoodsReceipts);
    }

    public LedgerContext Context
    {
        get
        {
            EnsureSynced();
            return _context;
        }
        private set => _context = value;
    }

    public LedgerState State
    {
        get
        {
            EnsureSynced();
            return _state;
        }
        private set => _state = value;
    }

    public IReadOnlyList<Movement> Movements
    {
        get
        {
            EnsureSynced();
            return _movements;
        }
    }

    public IReadOnlyList<AuditEvent> Audits
    {
        get
        {
            EnsureSynced();
            return _audits;
        }
    }

    private string DbPath()
    {
        _paths.EnsureCreated();
        return Path.Combine(_paths.DataDir, "bentago.db");
    }

    // ------------------------------------------------------------------
    // Document registry contract (C13, D5)
    // ------------------------------------------------------------------

    /// <summary>
    /// Day documents are movement-less by design: Register* commits the
    /// document immediately (D5 asymmetry — no batch to fail), keeping the
    /// document in the store's in-process registry AND persisting it to the
    /// day tables created by the BG-P1 baseline migration, so a restart or
    /// restore re-folds the lifecycle.
    /// </summary>
    public void RegisterDayOpen(DayOpen day)
    {
        _docsDayOpens.Add(day);
        PersistDayDocument(registry => InsertDayOpen(registry, day));
    }

    public void RegisterDayClose(DayClose close)
    {
        _docsDayCloses.Add(close);
        PersistDayDocument(registry => InsertDayClose(registry, close));
    }

    public void RegisterDayReopen(DayReopen reopen)
    {
        _docsDayReopens.Add(reopen);
        PersistDayDocument(registry => InsertDayReopen(registry, reopen));
    }

    /// <summary>
    /// Commits one day document in its own tiny transaction (audit-row
    /// pattern): the registry entry never depends on the insert failing, and
    /// a duplicate registration is a no-op.
    /// </summary>
    private void PersistDayDocument(Action<SqliteConnection> insert)
    {
        EnsureOpen();
        SqliteConnection connection = _connection!;
        try
        {
            Execute(connection, "BEGIN IMMEDIATE");
            _inTransaction = true;
            insert(connection);
            Execute(connection, "COMMIT");
            _inTransaction = false;
        }
        catch
        {
            RollbackQuietly(connection);
            throw;
        }
    }

    public void RegisterSale(Sale sale) => _stagedSales.Add(sale);

    public void RegisterReturn(Return @return) => _stagedReturns.Add(@return);

    public void RegisterVoid(SaleVoid @void) => _stagedVoids.Add(@void);

    public void RegisterAdjustment(Adjustment adjustment) => _stagedAdjustments.Add(adjustment);

    public void RegisterCount(PhysicalCount count) => _stagedCounts.Add(count);

    public void RegisterGoodsReceipt(GoodsReceipt receipt) => _stagedGoodsReceipts.Add(receipt);

    /// <summary>
    /// Binds the committed document sets (read from the database, so a
    /// standalone registration committed by any store is visible) plus the
    /// staged open attempt into the fold, and refreshes the context snapshot.
    /// </summary>
    public void Bind()
    {
        EnsureSynced();
        RefreshDocumentSets(_connection!);

        IReadOnlyList<Sale> sales = _docsSales.Concat(_stagedSales).ToList();
        IReadOnlyList<Return> returns = _docsReturns.Concat(_stagedReturns).ToList();
        IReadOnlyList<SaleVoid> voids = _docsVoids.Concat(_stagedVoids).ToList();

        _state.BindDocuments(sales, returns, voids);
        _state.BindDays(_docsDayOpens, _docsDayCloses, _docsDayReopens);
        Context = RefreshContext(sales, returns, voids);
    }

    // ------------------------------------------------------------------
    // Append: the one durable write unit (BG-003 §6)
    // ------------------------------------------------------------------

    public Result<AppendOutcome> Append(AppendBatch batch)
    {
        EnsureSynced();
        SqliteConnection connection = _connection!;

        try
        {
            Execute(connection, "BEGIN IMMEDIATE");
            _inTransaction = true;

            try
            {
                // 1. Idempotency replay inside the txn: the write lock is held,
                // so a key seen here can never be inserted between this select
                // and our own insert — the replay check is exact (D1).
                if (batch.Idempotency is { } mapping)
                {
                    string? stored = ReadIdempotencyPayload(connection, mapping.Key);
                    if (stored is not null)
                    {
                        return OkReplay(connection, stored);
                    }
                }

                if (batch.Movements.Count == 0 && batch.Audits is not { Count: > 0 })
                {
                    return FailRollback(connection, new DomainError("EMPTY_APPEND", "nothing to append"));
                }

                // 2. Watermark probe INSIDE the txn (R3): a stale head means
                // another writer committed since our hot fold; re-fold from the
                // persisted stream before guarding (BG-003 §6 steps 2-4).
                (long lastSeq, long docsVersion) = ReadHead(connection);
                if (lastSeq != _headLastSequence || docsVersion != _headDocumentsVersion)
                {
                    RefreshFromDatabase(connection, lastSeq, docsVersion);
                }

                // 3. Clone-apply guards with the bound document set.
                LedgerState next = _state.Clone();
                IReadOnlyList<Sale> sales = _docsSales.Concat(_stagedSales).ToList();
                IReadOnlyList<Return> returns = _docsReturns.Concat(_stagedReturns).ToList();
                IReadOnlyList<SaleVoid> voids = _docsVoids.Concat(_stagedVoids).ToList();
                next.BindDocuments(sales, returns, voids);
                next.BindDays(_docsDayOpens, _docsDayCloses, _docsDayReopens);

                long first = _state.LastSequence + 1;
                long sequence = first;
                var sequenced = new List<Movement>(batch.Movements.Count);
                foreach (Movement movement in batch.Movements)
                {
                    Movement withSequence = movement with { Sequence = sequence };
                    DomainError? violation = next.ApplyCommitted(withSequence);
                    if (violation is not null)
                    {
                        return FailRollback(connection, violation);
                    }

                    sequenced.Add(withSequence);
                    sequence++;
                }

                try
                {
                    next.VerifyCrossChecks(sales);
                }
                catch (LedgerIntegrityException ex)
                {
                    return FailRollback(connection, new DomainError("INTEGRITY", ex.Message));
                }

                // 4. Inserts: staged documents, movements, audits, key row.
                InsertStagedDocuments(connection);
                foreach (Movement movement in sequenced)
                {
                    InsertMovement(connection, movement);
                }

                var persistedAudits = new List<AuditEvent>();
                if (batch.Audits is { Count: > 0 } audits)
                {
                    foreach (AuditEvent audit in audits)
                    {
                        persistedAudits.Add(audit with { Sequence = InsertAudit(connection, audit) });
                    }
                }

                if (batch.Idempotency is { } idem)
                {
                    InsertIdempotency(connection, idem.Key, idem.Payload);
                }

                // 5. Debug seam: forced mid-transaction failure (lane 3 unit).
                if (_debug.ConsumeFailNextAppendAfterInsert())
                {
                    return FailRollback(connection, new DomainError(
                        "FORCED_ROLLBACK", "debug seam: forced rollback after insert"));
                }

                // 6. Projections from the validated fold clone (the batch's
                // affected keys only — untouched cells keep their rows).
                WriteProjections(connection, next, sequenced);

                // 7. Bump the watermark; commit (synchronous=FULL survives
                // power loss, because the receipt prints after this commit).
                long newDocsVersion = _headDocumentsVersion + (HasStagedDocuments() ? 1 : 0);
                UpdateHead(connection, sequence - 1, newDocsVersion);

                if (_debug.ConsumeDelayBeforeCommit())
                {
                    _debug.PreCommit?.Invoke();
                    Thread.Sleep(TimeSpan.FromMilliseconds(DefaultDelayBeforeCommitMs));
                }

                Execute(connection, "COMMIT");
                _inTransaction = false;

                // Post-commit: adopt the fold, mirrors, and watermark.
                _state = next;
                _headLastSequence = sequence - 1;
                _headDocumentsVersion = newDocsVersion;
                _movements.AddRange(sequenced);
                if (persistedAudits.Count > 0)
                {
                    _audits.AddRange(persistedAudits);
                }

                Context = RefreshContext(sales, returns, voids);
                ClearStaged();
                return Result<AppendOutcome>.Ok(new AppendOutcome(
                    new AppendReceipt(first, sequence - 1), Replayed: false, ReplayedPayload: null));
            }
            catch (LedgerIntegrityException ex)
            {
                return FailRollback(connection, new DomainError("INTEGRITY", ex.Message));
            }
            catch (LedgerCorruptException ex)
            {
                return FailRollback(connection, new DomainError("LEDGER_CORRUPT", ex.Message));
            }
            catch (SqliteException ex) when (IsBusy(ex))
            {
                return FailRollback(connection, new DomainError(
                    "LOCK_TIMEOUT", "the ledger is busy; retry the operation"));
            }
            catch (SqliteException ex)
            {
                return FailRollback(connection, MapConstraint(ex));
            }
            catch (Exception)
            {
                RollbackQuietly(connection);
                ClearStaged();
                throw;
            }
        }
        finally
        {
            _inTransaction = false;
        }
    }

    public Result<AppendReceipt> Append(IReadOnlyList<Movement> movements) =>
        Append(new AppendBatch(movements)).Map(outcome => outcome.Receipt!);

    // ------------------------------------------------------------------
    // Audit + idempotency + rebuild (lanes 4, 6, 9-style)
    // ------------------------------------------------------------------

    /// <summary>
    /// A standalone tiny transaction (audit rows are not in a batch). The
    /// AuditEvents PK on Sequence is the SQLite rowid identity: the caller's
    /// -1 placeholder is replaced by the assigned identity, which is returned
    /// and mirrored so the in-memory audit list matches the database.
    /// </summary>
    public long RecordAudit(AuditEvent audit)
    {
        EnsureSynced();
        SqliteConnection connection = _connection!;

        try
        {
            Execute(connection, "BEGIN IMMEDIATE");
            _inTransaction = true;
            long sequence;
            try
            {
                sequence = InsertAudit(connection, audit);
                Execute(connection, "COMMIT");
                _inTransaction = false;
            }
            catch
            {
                RollbackQuietly(connection);
                throw;
            }

            _audits.Add(audit with { Sequence = sequence });
            return sequence;
        }
        finally
        {
            _inTransaction = false;
        }
    }

    public Result<CheckoutReference?> FindCheckout(string idempotencyKey)
    {
        EnsureSynced();

        try
        {
            string? payload = ReadIdempotencyPayload(_connection!, idempotencyKey);
            if (payload is null)
            {
                return Result<CheckoutReference?>.Ok(null);
            }

            try
            {
                return Result<CheckoutReference?>.Ok(CheckoutReference.FromPayload(payload));
            }
            catch (InvalidOperationException)
            {
                return Result<CheckoutReference?>.Fail(new DomainError(
                    "LEDGER_CORRUPT", $"stored idempotency payload for key '{idempotencyKey}' is not a checkout reference"));
            }
        }
        catch (SqliteException ex) when (IsMissingTable(ex))
        {
            return Result<CheckoutReference?>.Fail(new DomainError(
                "LEDGER_CORRUPT",
                $"schema predates the idempotency table (migration {LedgerSchemaFixture.Migration01Id}); run 'foundation migrate'"));
        }
    }

    /// <summary>
    /// Rebuilds every balance from the persisted movements through the real
    /// <see cref="LedgerState.Fold"/> and rewrites the five projection tables
    /// (lane 6): after the rebuild the projections equal the hot state
    /// exactly and the watermark lands on the stream max.
    /// </summary>
    public async Task<RebuildReceipt> RebuildAsync(CancellationToken ct = default)
    {
        EnsureSynced();
        SqliteConnection connection = _connection!;

        try
        {
            Execute(connection, "BEGIN IMMEDIATE");
            _inTransaction = true;

            ct.ThrowIfCancellationRequested();
            List<Movement> movements = ReadMovements(connection);
            DocumentSets docs = ReadDocumentSets(connection);
            LedgerState rebuilt = LedgerState.Fold(
                movements, docs.Sales, docs.Returns, docs.Voids);
            rebuilt.BindDays(_docsDayOpens, _docsDayCloses, _docsDayReopens);

            RewriteAllProjections(connection, rebuilt, movements);

            long newDocsVersion = ReadHead(connection).DocumentsVersion + 1;
            UpdateHead(connection, rebuilt.LastSequence, newDocsVersion);
            Execute(connection, "COMMIT");
            _inTransaction = false;

            // Adopt the rebuilt world (post-commit).
            _state = rebuilt;
            _headLastSequence = rebuilt.LastSequence;
            _headDocumentsVersion = newDocsVersion;
            _movements.Clear();
            _movements.AddRange(movements);
            _audits.Clear();
            _audits.AddRange(ReadAudits(connection));
            _docsSales = docs.Sales;
            _docsReturns = docs.Returns;
            _docsVoids = docs.Voids;
            _docsCounts = docs.Counts;
            _docsGoodsReceipts = docs.GoodsReceipts;
            ReadDayDocuments(connection);
            Context = RefreshContext();
            return new RebuildReceipt(
                rebuilt.LastSequence,
                movements.Count,
                docs.Sales.Count + docs.Returns.Count + docs.Voids.Count);
        }
        catch (LedgerIntegrityException ex)
        {
            RollbackQuietly(connection);
            throw new InvalidOperationException($"INTEGRITY: ledger replay failed: {ex.Message}", ex);
        }
        catch (LedgerCorruptException ex)
        {
            RollbackQuietly(connection);
            throw new InvalidOperationException($"LEDGER_CORRUPT: {ex.Message}", ex);
        }
        catch (SqliteException ex) when (IsBusy(ex))
        {
            RollbackQuietly(connection);
            throw new InvalidOperationException("LEDGER: the ledger is busy; retry the rebuild", ex);
        }
        catch
        {
            RollbackQuietly(connection);
            throw;
        }
        finally
        {
            _inTransaction = false;
        }
    }

    // ------------------------------------------------------------------
    // Lifecycle
    // ------------------------------------------------------------------

    /// <summary>
    /// Closes the long-lived connection so the restore hook can move the
    /// -wal/-shm companions (R1). A subsequent operation lazily reopens.
    /// </summary>
    public void Close()
    {
        if (_connection is not null)
        {
            _connection.Dispose();
            _connection = null;
            _inTransaction = false;
        }

        _synced = false;
    }

    public ValueTask DisposeAsync()
    {
        Close();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Synchronous dispose for the MS.DI sync ServiceProvider.Dispose() path.
    /// LoginService (auth audits) depends on <see cref="ILedgerStore"/>, so the
    /// store is instantiated inside the container whenever the login/account
    /// chain is resolved; an IAsyncDisposable-ONLY type makes the container's
    /// sync dispose throw. Identical work to <see cref="DisposeAsync"/>: close
    /// the long-lived connection.
    /// </summary>
    public void Dispose()
    {
        Close();
    }

    private void EnsureOpen()
    {
        if (_connection is not null)
        {
            return;
        }

        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA journal_mode=WAL";
            command.ExecuteNonQuery();
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"PRAGMA busy_timeout={BusyTimeoutMs}";
            command.ExecuteNonQuery();
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA synchronous=FULL";
            command.ExecuteNonQuery();
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA foreign_keys=ON";
            command.ExecuteNonQuery();
        }

        // A fresh database has no watermark row until the first write; the
        // store keeps the row present so every probe reads exactly one row.
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "INSERT OR IGNORE INTO LedgerHead (id, last_sequence, documents_version) VALUES (1, 0, 0)";
            command.ExecuteNonQuery();
        }

        _connection = connection;
    }

    // ------------------------------------------------------------------
    // Internal plumbing
    // ------------------------------------------------------------------

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private void RollbackQuietly(SqliteConnection connection)
    {
        if (!_inTransaction)
        {
            return;
        }

        try
        {
            Execute(connection, "ROLLBACK");
        }
        catch (SqliteException)
        {
            // The transaction may already be gone (connection-level error).
        }
        finally
        {
            _inTransaction = false;
        }
    }

    private Result<AppendOutcome> OkReplay(SqliteConnection connection, string storedPayload)
    {
        Execute(connection, "ROLLBACK");
        _inTransaction = false;
        ClearStaged();
        return Result<AppendOutcome>.Ok(new AppendOutcome(null, Replayed: true, ReplayedPayload: storedPayload));
    }

    private Result<AppendOutcome> FailRollback(SqliteConnection connection, DomainError error)
    {
        Execute(connection, "ROLLBACK");
        _inTransaction = false;
        ClearStaged();
        return Result<AppendOutcome>.Fail(error);
    }

    private bool HasStagedDocuments() =>
        _stagedSales.Count > 0 || _stagedReturns.Count > 0 ||
        _stagedVoids.Count > 0 || _stagedAdjustments.Count > 0 || _stagedCounts.Count > 0 ||
        _stagedGoodsReceipts.Count > 0;

    /// <summary>
    /// Re-folds the persisted stream into the hot state and reloads the
    /// document sets and mirrors. Runs INSIDE the append transaction (after
    /// BEGIN IMMEDIATE), so the re-fold sees every movement committed before
    /// the write lock was acquired (BG-003 §6). Also called by the cold sync,
    /// so the context snapshot is rebuilt here too (BG-005c: a reopened
    /// store's <see cref="Context"/> must expose the reloaded documents, not
    /// the constructor-time empty sets).
    /// </summary>
    private void RefreshFromDatabase(SqliteConnection connection, long lastSeq, long docsVersion)
    {
        List<Movement> movements = ReadMovements(connection);
        DocumentSets docs = ReadDocumentSets(connection);
        LedgerState folded = LedgerState.Fold(
            movements, docs.Sales, docs.Returns, docs.Voids);
        folded.BindDays(_docsDayOpens, _docsDayCloses, _docsDayReopens);

        _state = folded;
        _movements.Clear();
        _movements.AddRange(movements);
        _audits.Clear();
        _audits.AddRange(ReadAudits(connection));
        _docsSales = docs.Sales;
        _docsReturns = docs.Returns;
        _docsVoids = docs.Voids;
        _docsCounts = docs.Counts;
        _docsGoodsReceipts = docs.GoodsReceipts;
        ReadDayDocuments(connection);
        _headLastSequence = lastSeq;
        _headDocumentsVersion = docsVersion;
        Context = RefreshContext();
    }

    private void RefreshDocumentSets(SqliteConnection connection)
    {
        DocumentSets docs = ReadDocumentSets(connection);
        _docsSales = docs.Sales;
        _docsReturns = docs.Returns;
        _docsVoids = docs.Voids;
        _docsCounts = docs.Counts;
        _docsGoodsReceipts = docs.GoodsReceipts;
        ReadDayDocuments(connection);
    }

    private LedgerContext RefreshContext() =>
        RefreshContext(_docsSales, _docsReturns, _docsVoids);

    private LedgerContext RefreshContext(IReadOnlyList<Sale> sales, IReadOnlyList<Return> returns, IReadOnlyList<SaleVoid> voids) =>
        new(_state, sales, returns, voids, _docsCounts, _docsDayOpens, _docsDayCloses, _docsDayReopens, _docsGoodsReceipts);

    private void ClearStaged()
    {
        _stagedSales.Clear();
        _stagedReturns.Clear();
        _stagedVoids.Clear();
        _stagedAdjustments.Clear();
        _stagedCounts.Clear();
        _stagedGoodsReceipts.Clear();
    }

    /// <summary>
    /// The cold first touch after an open/close: folds the persisted stream
    /// into the hot state so a fresh store renders the persisted day without
    /// re-running it (R7; lane 6/lane 10). Advisory read — the append's
    /// in-transaction probe remains the authoritative staleness check.
    /// </summary>
    public void EnsureSynced()
    {
        EnsureOpen();
        if (_synced)
        {
            return;
        }

        HeadWatermark head = ReadHead(_connection!);
        if (head.LastSequence != _headLastSequence || head.DocumentsVersion != _headDocumentsVersion)
        {
            RefreshFromDatabase(_connection!, head.LastSequence, head.DocumentsVersion);
        }

        _synced = true;
    }

    private static bool IsBusy(SqliteException ex) =>
        ex.SqliteErrorCode is 5 or 6; // SQLITE_BUSY, SQLITE_LOCKED

    private static bool IsMissingTable(SqliteException ex) =>
        ex.SqliteErrorCode == 1 && ex.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Maps a constraint that bypassed the fold (raw SQL, manual edits,
    /// future features) to a structured code (BG-003 §4 graft): the fold
    /// almost always fires first with the exact code.
    /// </summary>
    private static DomainError MapConstraint(SqliteException ex)
    {
        const int sqliteConstraint = 19;
        const int sqliteConstraintUnique = 2067;
        const int sqliteConstraintPrimaryKey = 1555;

        if (ex.SqliteErrorCode != sqliteConstraint)
        {
            return new DomainError("INTEGRITY", ex.Message);
        }

        int extended = ex.SqliteExtendedErrorCode;
        string message = ex.Message;
        if (extended == sqliteConstraintPrimaryKey)
        {
            return message.Contains("Movements", StringComparison.Ordinal)
                ? new DomainError("SEQ_ORDER", $"sequence collision on Movements: {message}")
                : new DomainError("INTEGRITY", message);
        }

        if (extended == sqliteConstraintUnique)
        {
            if (message.Contains("Sales.receipt_number", StringComparison.Ordinal))
            {
                return new DomainError("DUP_RECEIPT_NUMBER", $"receipt number already used: {message}");
            }

            if (message.Contains("StockLoads.load_number", StringComparison.Ordinal))
            {
                return new DomainError("DUP_LOAD", $"load number already used: {message}");
            }

            return new DomainError("INTEGRITY", message);
        }

        return new DomainError("INTEGRITY", message);
    }
}
