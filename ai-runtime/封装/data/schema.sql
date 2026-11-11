-- ============================================================================
-- AI Runtime 知识库 schema
-- 目标：SQLite 3.34+（trigram tokenizer 与 FTS5 外部内容表）
-- 生成：sqlite3 data/knowledge_seed.db ".read data/schema.sql"
-- 本文件是权威定义；knowledge_seed.db 是它的产物，任何时候以本文件为准。
--
-- 注意：C# 层 MemoryRepository 内置了同一份 DDL，并在首次连接时执行。
-- 修改这里时必须同步修改 MemoryRepository.SchemaSql，否则两条初始化路径会分叉。
-- ============================================================================

PRAGMA user_version = 1;

-- ---------------------------------------------------------------------------
-- raw_dialogue：原始对话全文，回退时按 id 范围拉取
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS raw_dialogue (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    session_id TEXT    NOT NULL,
    turn_index INTEGER NOT NULL,
    full_text  TEXT    NOT NULL,
    timestamp  TEXT    NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_raw_session_turn ON raw_dialogue(session_id, turn_index);

-- ---------------------------------------------------------------------------
-- memory_index：压缩后的记忆条目
-- raw_start_id / raw_end_id 指向 raw_dialogue.id，是回退链路定位原文的唯一凭据
-- ---------------------------------------------------------------------------
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

-- ---------------------------------------------------------------------------
-- memory_fts：外部内容 FTS5 表
-- 使用 trigram tokenizer：中文以 3 字符为最小检索单元，
-- 对"夹具模具"这类短语搜索的效果远好于 unicode61（后者把每个汉字切成单 token）。
-- 代价：MATCH 查询字符串长度必须 >= 3。
--
-- 触发器是唯一的同步路径：任何人不得绕过触发器直接写 memory_fts，
-- 否则外部内容表与 memory_index 会不一致。
-- ---------------------------------------------------------------------------
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

-- ---------------------------------------------------------------------------
-- knowledge_facts：已确认事实，用于开放域一致性校验
-- ---------------------------------------------------------------------------
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
