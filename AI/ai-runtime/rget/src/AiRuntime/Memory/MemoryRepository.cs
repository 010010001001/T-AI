using Microsoft.Data.Sqlite;

public sealed record MemoryEntry(long Id, string CompressedText, int Level,
    long? ParentId, long RawStartId, long RawEndId, int TokenCount, DateTimeOffset Timestamp);

public sealed class MemoryRepository
{
    private readonly string _connStr;
    public MemoryRepository(string dbPath) => _connStr = $"Data Source={dbPath}";

    public void InsertMemoryEntry(string compressedText, int level, long? parentId,
        long rawStartId, long rawEndId, int tokenCount, DateTimeOffset timestamp)
    {
        using var conn = new SqliteConnection(_connStr);
        conn.Open();
        using var tx = conn.BeginTransaction();

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO memory_index
                    (compressed_text, level, parent_id, raw_start_id, raw_end_id, token_count, timestamp)
                VALUES ($t, $l, $p, $rs, $re, $tc, $ts);
                """;
            cmd.Parameters.AddWithValue("$t", compressedText);
            cmd.Parameters.AddWithValue("$l", level);
            cmd.Parameters.AddWithValue("$p", (object?)parentId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$rs", rawStartId);
            cmd.Parameters.AddWithValue("$re", rawEndId);
            cmd.Parameters.AddWithValue("$tc", tokenCount);
            cmd.Parameters.AddWithValue("$ts", timestamp.ToString("O"));
            cmd.ExecuteNonQuery();
        }

        // 显式同步外部内容 FTS5 表
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO memory_fts(rowid, compressed_text)
                SELECT id, compressed_text FROM memory_index
                WHERE id = last_insert_rowid();
                """;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void InsertRawDialogue(long id, string sessionId, int turnIndex,
        string fullText, DateTimeOffset at)
    {
        using var conn = new SqliteConnection(_connStr);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO raw_dialogue(id, session_id, turn_index, full_text, timestamp)
            VALUES ($id, $s, $ti, $ft, $ts);
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$s", sessionId);
        cmd.Parameters.AddWithValue("$ti", turnIndex);
        cmd.Parameters.AddWithValue("$ft", fullText);
        cmd.Parameters.AddWithValue("$ts", at.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public List<MemoryEntry> GetMemoryEntries(int level) { /* SELECT ... WHERE level=$l */ }
    public void DeleteMemoryEntries(IEnumerable<long> ids) { /* DELETE + memory_fts 同步 */ }
    public List<MemoryEntry> SearchByKeyword(string keyword) { /* FTS5 MATCH 查询 */ }
    public List<string> GetRawRange(long startId, long endId) { /* SELECT full_text WHERE id BETWEEN */ }
}