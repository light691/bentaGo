using System.IO;
using Microsoft.Data.Sqlite;

namespace BentaGo.Tests.Persistence;

/// <summary>Raw SQLite read helpers shared by the Persistence buckets.</summary>
internal static class SqliteTest
{
    public static async Task<long> CountAsync(string dbPath, string table)
    {
        await using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(1) FROM \"{table}\"";
        long count = Convert.ToInt64(await command.ExecuteScalarAsync());
        await connection.CloseAsync();
        return count;
    }

    public static async Task<long?> MinAsync(string dbPath, string table, string column)
    {
        await using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT MIN(\"{column}\") FROM \"{table}\" WHERE \"{column}\" IS NOT NULL";
        object? result = await command.ExecuteScalarAsync();
        await connection.CloseAsync();
        return result is DBNull or null ? null : Convert.ToInt64(result);
    }

    public static async Task<long> HeadSequenceAsync(string dbPath)
    {
        await using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT \"last_sequence\" FROM \"LedgerHead\" WHERE \"id\" = 1";
        long value = Convert.ToInt64(await command.ExecuteScalarAsync());
        await connection.CloseAsync();
        return value;
    }

    public static async Task<string> QuickCheckAsync(string dbPath)
    {
        await using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check";
        string? result = await command.ExecuteScalarAsync() as string;
        await connection.CloseAsync();
        return result ?? "unknown";
    }
}
