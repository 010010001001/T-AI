// src/AiRuntime/Memory/MemoryRepository.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;

public sealed record MemoryEntry(
    long Id,
    string CompressedText,
    int Level,
    long? ParentId,
    long RawStartId,
    long RawEndId,
    int TokenCount,
    DateTimeOffset Timestamp);

public sealed record RawTurnRow(long Id, int TurnIndex, string FullText, DateTimeOffset Timestamp);

public sealed record SessionInfo(
    string SessionId,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    int Turns);

/// <summary>
/// 知识库仓储：memory_index（压缩记忆）、memory_fts（FTS5 外部内容表）、
/// raw_dialogue（原文）、knowledge_facts（一致性校验用事实）。
/// </summary>
/// <remarks>
/// 每次调用使用独立连接；SQLite 在 WAL 模式下允许读写并发。
/// FTS5 同步由 schema 中的触发器完成，调用方不得手工往 memory_fts 写行，
/// 否则会与触发器重复插入。
/// </remarks>
public sealed class MemoryRepository
{
    private readonly string _connectionString;
    private readonly object _initLock = new();
    private bool _initialized;

    public MemoryRepository(string dbPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(dbPath);

        string? dir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();
    }

    // ------------------------------------------------------------------
    // schema
    // ------------------------------------------------------------------

    private const string SchemaSql = """
        PRAGMA user_version = 1;

        CREATE TABLE IF NOT EXISTS raw_dialogue (
            id         INTEGER PRIMARY KEY AUTOINCREMENT,
            session_id TEXT    NOT NULL,
            turn_index INTEGER NOT NULL,
            full_text  TEXT    NOT NULL,
            timestamp  TEXT    NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_raw_session_turn ON raw_dialogue(session_id, turn_index);

        CREATE TABLE IF NOT EXISTS memory_index (
            id              INTEGER PRIMARY KEY AUTOINCREMENT,
            compressed_text TEXT    NOT NULL,
            level           INTEGER NOT NULL CHECK (level BETWEEN 1 AND 4),
            parent_id       INTEGER REFERENCES memory_index(id) ON DELETE SET NULL,
            raw_start_id    INTEGER NOT NULL,
            raw_end_id      INTEGER NOT NULL,
            token_count     INTEGER NOT NULL CHECK (token_count > 0),
            timestamp       TEXT    NOT NULL,
            CHECK (raw_start_id <= raw_end_id)
        );
        CREATE INDEX IF NOT EXISTS idx_memory_level     ON memory_index(level);
        CREATE INDEX IF NOT EXISTS idx_memory_timestamp ON memory_index(timestamp);

        CREATE VIRTUAL TABLE IF NOT EXISTS memory_fts USING fts5(
            compressed_text,
            content='memory_index',
            content_rowid='id',
            tokenize='trigram'
        );

        CREATE TRIGGER IF NOT EXISTS memory_ai AFTER INSERT ON memory_index BEGIN
            INSERT INTO memory_fts(rowid, compressed_text) VALUES (new.id, new.compressed_text);
        END;
        CREATE TRIGGER IF NOT EXISTS memory_ad AFTER DELETE ON memory_index BEGIN
            INSERT INTO memory_fts(memory_fts, rowid, compressed_text)
            VALUES ('delete', old.id, old.compressed_text);
        END;
        CREATE TRIGGER IF NOT EXISTS memory_au AFTER UPDATE ON memory_index BEGIN
            INSERT INTO memory_fts(memory_fts, rowid, compressed_text)
            VALUES ('delete', old.id, old.compressed_text);
            INSERT INTO memory_fts(rowid, compressed_text) VALUES (new.id, new.compressed_text);
        END;

        CREATE TABLE IF NOT EXISTS knowledge_facts (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            fact_text   TEXT NOT NULL,
            source      TEXT,
            verified_at TEXT NOT NULL
        );

        CREATE VIRTUAL TABLE IF NOT EXISTS facts_fts USING fts5(
            fact_text,
            content='knowledge_facts',
            content_rowid='id',
            tokenize='trigram'
        );
        """;

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();

        if (!_initialized)
        {
            lock (_initLock)
            {
                if (!_initialized)
                {
                    using (var pragma = conn.CreateCommand())
                    {
                        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
                        pragma.ExecuteNonQuery();
                    }
                    using (var schema = conn.CreateCommand())
                    {
                        schema.CommandText = SchemaSql;
                        schema.ExecuteNonQuery();
                    }
                    _initialized = true;
                }
            }
        }
        return conn;
    }

    // ------------------------------------------------------------------
    // 原文
    // ------------------------------------------------------------------

    /// <summary>写入一轮原文，返回自增主键（供 DialogueTurn.RawId 使用）。</summary>
    public long InsertRawDialogue(string sessionId, int turnIndex, string fullText, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        ArgumentNullException.ThrowIfNull(fullText);

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO raw_dialogue(session_id, turn_index, full_text, timestamp)
            VALUES ($s, $ti, $ft, $ts)
            RETURNING id;
            """;
        cmd.Parameters.AddWithValue("$s", sessionId);
        cmd.Parameters.AddWithValue("$ti", turnIndex);
        cmd.Parameters.AddWithValue("$ft", fullText);
        cmd.Parameters.AddWithValue("$ts", at.ToString("O"));

        object? scalar = cmd.ExecuteScalar();
        return Convert.ToInt64(scalar);
    }

    /// <summary>按 id 范围拉取原文，用于回退链路重建完整上下文。</summary>
    public List<string> GetRawRange(long startId, long endId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT full_text FROM raw_dialogue
            WHERE id BETWEEN $a AND $b
            ORDER BY id;
            """;
        cmd.Parameters.AddWithValue("$a", startId);
        cmd.Parameters.AddWithValue("$b", endId);

        var result = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    // ------------------------------------------------------------------
    // 记忆条目
    // ------------------------------------------------------------------

    public long InsertMemoryEntry(
        string compressedText, int level, long? parentId,
        long rawStartId, long rawEndId, int tokenCount, DateTimeOffset timestamp)
    {
        ArgumentNullException.ThrowIfNull(compressedText);

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO memory_index
                (compressed_text, level, parent_id, raw_start_id, raw_end_id, token_count, timestamp)
            VALUES ($t, $l, $p, $rs, $re, $tc, $ts)
            RETURNING id;
            """;
        cmd.Parameters.AddWithValue("$t", compressedText);
        cmd.Parameters.AddWithValue("$l", level);
        cmd.Parameters.AddWithValue("$p", (object?)parentId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$rs", rawStartId);
        cmd.Parameters.AddWithValue("$re", rawEndId);
        cmd.Parameters.AddWithValue("$tc", Math.Max(1, tokenCount));
        cmd.Parameters.AddWithValue("$ts", timestamp.ToString("O"));

        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public List<MemoryEntry> GetMemoryEntries(int level)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, compressed_text, level, parent_id, raw_start_id, raw_end_id, token_count, timestamp
            FROM memory_index
            WHERE level = $l
            ORDER BY timestamp, id;
            """;
        cmd.Parameters.AddWithValue("$l", level);

        var result = new List<MemoryEntry>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new MemoryEntry(
                Id: reader.GetInt64(0),
                CompressedText: reader.GetString(1),
                Level: reader.GetInt32(2),
                ParentId: reader.IsDBNull(3) ? null : reader.GetInt64(3),
                RawStartId: reader.GetInt64(4),
                RawEndId: reader.GetInt64(5),
                TokenCount: reader.GetInt32(6),
                Timestamp: DateTimeOffset.Parse(reader.GetString(7))));
        }
        return result;
    }

    /// <summary>删除指定记忆条目；memory_fts 由 AFTER DELETE 触发器同步。</summary>
    public void DeleteMemoryEntries(IEnumerable<long> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);

        var list = ids.Distinct().ToArray();
        if (list.Length == 0) return;

        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;

        cmd.CommandText = $"DELETE FROM memory_index WHERE id IN ({string.Join(",", list.Select((_, i) => "$p" + i))});";
        for (int i = 0; i < list.Length; i++)
            cmd.Parameters.AddWithValue("$p" + i, list[i]);

        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    // ------------------------------------------------------------------
    // 检索
    // ------------------------------------------------------------------

    /// <summary>
    /// FTS5 关键字检索。trigram 分词器要求查询串至少 3 个字符；
    /// 更短的词改用 LIKE，保证回退链路不会因为查询串过短而静默失败。
    /// </summary>
    public List<MemoryEntry> SearchByKeyword(string keyword, int limit = 16)
    {
        ArgumentNullException.ThrowIfNull(keyword);

        string trimmed = keyword.Trim();
        if (trimmed.Length == 0) return new List<MemoryEntry>();

        using var conn = Open();
        using var cmd = conn.CreateCommand();

        if (trimmed.Length >= 3)
        {
            cmd.CommandText = """
                SELECT m.id, m.compressed_text, m.level, m.parent_id,
                       m.raw_start_id, m.raw_end_id, m.token_count, m.timestamp
                FROM memory_fts
                JOIN memory_index AS m ON m.id = memory_fts.rowid
                WHERE memory_fts MATCH $q
                ORDER BY memory_fts.rank
                LIMIT $n;
                """;
            cmd.Parameters.AddWithValue("$q", "\"" + trimmed.Replace("\"", "\"\"") + "\"");
        }
        else
        {
            cmd.CommandText = """
                SELECT id, compressed_text, level, parent_id,
                       raw_start_id, raw_end_id, token_count, timestamp
                FROM memory_index
                WHERE compressed_text LIKE '%' || $q || '%'
                ORDER BY timestamp DESC
                LIMIT $n;
                """;
            cmd.Parameters.AddWithValue("$q", trimmed);
        }
        cmd.Parameters.AddWithValue("$n", limit);

        var result = new List<MemoryEntry>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new MemoryEntry(
                Id: reader.GetInt64(0),
                CompressedText: reader.GetString(1),
                Level: reader.GetInt32(2),
                ParentId: reader.IsDBNull(3) ? null : reader.GetInt64(3),
                RawStartId: reader.GetInt64(4),
                RawEndId: reader.GetInt64(5),
                TokenCount: reader.GetInt32(6),
                Timestamp: DateTimeOffset.Parse(reader.GetString(7))));
        }
        return result;
    }

    // ------------------------------------------------------------------
    // 会话级恢复（跨天持久化）
    // ------------------------------------------------------------------

    /// <summary>最近一次写入原文的会话 ID；没有任何对话时返回 null。</summary>
    public string? GetLatestSessionId()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT session_id FROM raw_dialogue
            ORDER BY timestamp DESC
            LIMIT 1;
            """;
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>按最后活跃时间倒序列出会话，用于按时间戳恢复。</summary>
    public List<SessionInfo> ListSessions(int limit = 50)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT session_id, MIN(timestamp), MAX(timestamp), COUNT(*)
            FROM raw_dialogue
            GROUP BY session_id
            ORDER BY MAX(timestamp) DESC
            LIMIT $n;
            """;
        cmd.Parameters.AddWithValue("$n", limit);

        var result = new List<SessionInfo>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new SessionInfo(
                SessionId: reader.GetString(0),
                StartedAt: DateTimeOffset.Parse(reader.GetString(1)),
                EndedAt: DateTimeOffset.Parse(reader.GetString(2)),
                Turns: reader.GetInt32(3)));
        }
        return result;
    }

    /// <summary>读取某会话的全部原文轮次，按写入顺序升序。</summary>
    public List<RawTurnRow> GetRawTurns(string sessionId)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, turn_index, full_text, timestamp
            FROM raw_dialogue
            WHERE session_id = $s
            ORDER BY id;
            """;
        cmd.Parameters.AddWithValue("$s", sessionId);

        var result = new List<RawTurnRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new RawTurnRow(
                Id: reader.GetInt64(0),
                TurnIndex: reader.GetInt32(1),
                FullText: reader.GetString(2),
                Timestamp: DateTimeOffset.Parse(reader.GetString(3))));
        }
        return result;
    }

    /// <summary>
    /// 所有压缩摘要，层级高（更宏观）优先、同层新近优先。
    /// 调用方按 token 预算截断后再注入提示词。
    /// </summary>
    public List<MemoryEntry> GetAllSummaries()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, compressed_text, level, parent_id, raw_start_id, raw_end_id, token_count, timestamp
            FROM memory_index
            ORDER BY level DESC, timestamp DESC;
            """;

        var result = new List<MemoryEntry>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new MemoryEntry(
                Id: reader.GetInt64(0),
                CompressedText: reader.GetString(1),
                Level: reader.GetInt32(2),
                ParentId: reader.IsDBNull(3) ? null : reader.GetInt64(3),
                RawStartId: reader.GetInt64(4),
                RawEndId: reader.GetInt64(5),
                TokenCount: reader.GetInt32(6),
                Timestamp: DateTimeOffset.Parse(reader.GetString(7))));
        }
        return result;
    }
}
