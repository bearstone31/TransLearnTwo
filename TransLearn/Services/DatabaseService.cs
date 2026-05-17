// ============================================================
// DatabaseService.cs
// 역할 : SQLite 데이터베이스 접근 서비스. 모든 DB 읽기/쓰기의 단일 진입점.
//
// 테이블
//   translations   — OCR/STT로 캡처된 번역 기록
//   learned_words  — NLP 분석으로 추출된 단어장
//
// 주요 메서드
//   InitializeAsync()              — DB 생성 + 스키마 마이그레이션
//   InsertTranslationAsync()       — 번역 기록 저장
//   GetUnanalyzedTranslationsAsync()— IsAnalyzed=0인 미분석 기록 조회
//   MarkAnalyzedAsync()            — 분석 완료 플래그 일괄 설정
//   UpsertWordAsync()              — 단어 INSERT 또는 빈도 UPDATE
//   RateWordAsync()                — 👍/👎 평가 누적
//   GetWordEntriesAsync()          — LearnPriority 기준 단어장 조회
// ============================================================
using Microsoft.Data.Sqlite;
using TransLearn.Models;

namespace TransLearn.Services;

public class DatabaseService : IAsyncDisposable
{
    private readonly string _connStr;

    public DatabaseService(string dbPath)
    {
        _connStr = $"Data Source={dbPath};Cache=Shared";
    }

    // ── 스키마 초기화 ────────────────────────────────────────────────────
    public async Task InitializeAsync()
    {
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();

        cmd.CommandText = @"
PRAGMA journal_mode = WAL;
PRAGMA synchronous  = NORMAL;
PRAGMA foreign_keys = ON;
PRAGMA cache_size   = -8000;

-- ── 번역 기록 테이블 ────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS translations (
    Id           INTEGER PRIMARY KEY AUTOINCREMENT,
    CapturedAt   TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%f','now')),
    OriginalText TEXT    NOT NULL,
    Translated   TEXT    NOT NULL,
    CaptureType  INTEGER NOT NULL CHECK(CaptureType IN (0,1)),
    AppName      TEXT    NOT NULL DEFAULT '',
    QualityScore REAL    DEFAULT NULL,
    IsLearned    INTEGER NOT NULL DEFAULT 0,
    IsAnalyzed   INTEGER NOT NULL DEFAULT 0
);

CREATE INDEX IF NOT EXISTS idx_type_date
    ON translations(CaptureType, CapturedAt DESC);
CREATE INDEX IF NOT EXISTS idx_app_date
    ON translations(AppName, CapturedAt DESC);
CREATE INDEX IF NOT EXISTS idx_analyzed
    ON translations(IsAnalyzed);

-- ── 단어장 테이블 ────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS learned_words (
    Id                INTEGER PRIMARY KEY AUTOINCREMENT,
    Lemma             TEXT    NOT NULL UNIQUE,
    Pos               TEXT    NOT NULL,
    Frequency         INTEGER NOT NULL DEFAULT 1,
    GdexScore         REAL    DEFAULT 0,
    CreatedAt         TEXT,
    ExampleId         INTEGER REFERENCES translations(Id),
    ExampleTranslated TEXT    DEFAULT NULL,
    ThumbsUp          INTEGER NOT NULL DEFAULT 0,
    ThumbsDown        INTEGER NOT NULL DEFAULT 0
);

CREATE INDEX IF NOT EXISTS idx_word_freq
    ON learned_words(Frequency DESC);

-- ── 사용자 메모장 테이블 ─────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS user_memos (
    Id          INTEGER PRIMARY KEY AUTOINCREMENT,
    Content     TEXT    NOT NULL,
    Description TEXT    NOT NULL DEFAULT '',
    MemoDate    TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%f','now','localtime'))
);

CREATE INDEX IF NOT EXISTS idx_user_memos_date
    ON user_memos(MemoDate DESC);
";
        await cmd.ExecuteNonQueryAsync();

        // 기존 DB 마이그레이션: 컬럼이 없으면 추가
        await MigrateAsync(conn);
    }

    private static async Task MigrateAsync(SqliteConnection conn)
    {
        // translations.IsAnalyzed
        await TryAddColumnAsync(conn, "translations", "IsAnalyzed", "INTEGER NOT NULL DEFAULT 0");

        // learned_words.CreatedAt
        await TryAddColumnAsync(conn, "learned_words", "CreatedAt", "TEXT");

        // 기존 단어들 중 CreatedAt이 비어있는 데이터에 현재 시간 넣기
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
            UPDATE learned_words
            SET CreatedAt = strftime('%Y-%m-%dT%H:%M:%f','now','localtime')
            WHERE CreatedAt IS NULL OR CreatedAt = '';
        ";
            await cmd.ExecuteNonQueryAsync();
        }

        // learned_words.ThumbsUp / ThumbsDown
        await TryAddColumnAsync(conn, "learned_words", "ThumbsUp", "INTEGER NOT NULL DEFAULT 0");
        await TryAddColumnAsync(conn, "learned_words", "ThumbsDown", "INTEGER NOT NULL DEFAULT 0");
    }

    private static async Task TryAddColumnAsync(SqliteConnection conn,
        string table, string column, string definition)
    {
        try
        {
            await using var c = conn.CreateCommand();
            c.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
            await c.ExecuteNonQueryAsync();
        }
        catch { /* 이미 존재하면 무시 */ }
    }

    // ── 번역 INSERT ──────────────────────────────────────────────────────
    public async Task<long> InsertTranslationAsync(
        string original, string translated,
        CaptureType type, string appName = "")
    {
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO translations (OriginalText, Translated, CaptureType, AppName)
            VALUES (@o, @t, @ct, @app)
            RETURNING Id;";
        cmd.Parameters.AddWithValue("@o", original);
        cmd.Parameters.AddWithValue("@t", translated);
        cmd.Parameters.AddWithValue("@ct", (int)type);
        cmd.Parameters.AddWithValue("@app", appName);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    // ── 번역 기록 조회 ───────────────────────────────────────────────────
    public async Task<List<TranslationRecord>> GetTranslationsAsync(
        CaptureType? filter = null, int limit = 500, string? dateFrom = null)
    {
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();

        var where = new List<string>();
        if (filter.HasValue) where.Add("CaptureType = @ct");
        if (dateFrom is not null) where.Add("CapturedAt >= @from");

        var sql = "SELECT Id, CapturedAt, OriginalText, Translated, CaptureType, AppName, " +
                  "QualityScore, IsLearned, IsAnalyzed FROM translations ";
        if (where.Count > 0) sql += "WHERE " + string.Join(" AND ", where) + " ";
        sql += "ORDER BY CapturedAt DESC LIMIT @lim;";

        cmd.CommandText = sql;
        if (filter.HasValue) cmd.Parameters.AddWithValue("@ct", (int)filter.Value);
        if (dateFrom is not null) cmd.Parameters.AddWithValue("@from", dateFrom);
        cmd.Parameters.AddWithValue("@lim", limit);

        var list = new List<TranslationRecord>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            list.Add(ReadTranslation(reader));
        return list;
    }

    /// <summary>IsAnalyzed = 0 인 미분석 레코드만 반환</summary>
    public async Task<List<TranslationRecord>> GetUnanalyzedTranslationsAsync(int limit = 500)
    {
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT Id, CapturedAt, OriginalText, Translated, CaptureType, AppName,
                   QualityScore, IsLearned, IsAnalyzed
            FROM translations
            WHERE IsAnalyzed = 0
            ORDER BY CapturedAt DESC
            LIMIT @lim;";
        cmd.Parameters.AddWithValue("@lim", limit);

        var list = new List<TranslationRecord>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            list.Add(ReadTranslation(reader));
        return list;
    }

    /// <summary>분석 완료 표시</summary>
    public async Task MarkAnalyzedAsync(IEnumerable<long> ids)
    {
        var idList = string.Join(",", ids);
        if (string.IsNullOrEmpty(idList)) return;

        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"UPDATE translations SET IsAnalyzed = 1 WHERE Id IN ({idList});";
        await cmd.ExecuteNonQueryAsync();
    }

    private static TranslationRecord ReadTranslation(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        CapturedAt = DateTime.Parse(r.GetString(1)),
        OriginalText = r.GetString(2),
        Translated = r.GetString(3),
        CaptureType = (CaptureType)r.GetInt32(4),
        AppName = r.GetString(5),
        QualityScore = r.IsDBNull(6) ? null : r.GetDouble(6),
        IsLearned = r.GetInt32(7) == 1,
        IsAnalyzed = r.GetInt32(8) == 1,
    };

    // ── 단어 UPSERT ──────────────────────────────────────────────────────
    public async Task UpsertWordAsync(WordEntry w, long? exampleId = null)
    {
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO learned_words
                (Lemma, Pos, Frequency, GdexScore, CreatedAt, ExampleId, ExampleTranslated, ThumbsUp, ThumbsDown)
            VALUES (@lemma, @pos, 1, @gdex, strftime('%Y-%m-%dT%H:%M:%f','now','localtime'), @eid, @et, 0, 0)
            ON CONFLICT(Lemma) DO UPDATE SET
                Frequency  = Frequency + 1,
                GdexScore  = CASE WHEN excluded.GdexScore > GdexScore
                                  THEN excluded.GdexScore ELSE GdexScore END,
                ExampleId  = CASE WHEN excluded.GdexScore > GdexScore
                                  THEN excluded.ExampleId ELSE ExampleId END,
                ExampleTranslated = CASE WHEN excluded.GdexScore > GdexScore
                                         THEN excluded.ExampleTranslated
                                         ELSE ExampleTranslated END;";
        cmd.Parameters.AddWithValue("@lemma", w.Lemma);
        cmd.Parameters.AddWithValue("@pos", w.Pos);
        cmd.Parameters.AddWithValue("@gdex", w.GdexScore);
        cmd.Parameters.AddWithValue("@eid", exampleId.HasValue ? (object)exampleId.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@et", w.ExampleTranslated ?? (object)DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>단어 평가 반영 (+1 ThumbsUp 또는 ThumbsDown)</summary>
    public async Task RateWordAsync(long wordId, bool thumbsUp)
    {
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        var col = thumbsUp ? "ThumbsUp" : "ThumbsDown";
        cmd.CommandText = $"UPDATE learned_words SET {col} = {col} + 1 WHERE Id = @id;";
        cmd.Parameters.AddWithValue("@id", wordId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<List<WordEntry>> GetWordEntriesAsync(int limit = 200, string? appName = null)
    {
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();

        var useAppFilter =
            !string.IsNullOrWhiteSpace(appName) &&
            appName != "전체";

        cmd.CommandText = @"
        SELECT lw.Id, lw.Lemma, lw.Pos, lw.Frequency, lw.GdexScore,
               lw.CreatedAt,
               t.OriginalText, lw.ExampleTranslated,
               lw.ThumbsUp, lw.ThumbsDown
        FROM learned_words lw
        LEFT JOIN translations t ON t.Id = lw.ExampleId
        WHERE (@app IS NULL OR t.AppName = @app)
        ORDER BY lw.CreatedAt DESC
        LIMIT @lim;";

        cmd.Parameters.AddWithValue("@app", useAppFilter ? appName! : DBNull.Value);
        cmd.Parameters.AddWithValue("@lim", limit);

        var list = new List<WordEntry>();

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(ReadWordEntry(reader));
        }

        return list;
    }

    public async Task<List<string>> GetWordSourceAppsAsync()
    {
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();

        cmd.CommandText = @"
        SELECT DISTINCT AppName
        FROM translations
        WHERE AppName IS NOT NULL
          AND TRIM(AppName) <> ''
        ORDER BY AppName ASC;";

        var list = new List<string>();

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(reader.GetString(0));
        }

        return list;
    }

    /// <summary>
    /// 번역 기록에 저장된 대상 앱 목록을 가져온다.
    /// </summary>
    public async Task<List<string>> GetTranslationSourceAppsAsync()
    {
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();

        cmd.CommandText = @"
        SELECT DISTINCT AppName
        FROM translations
        WHERE AppName IS NOT NULL
          AND AppName <> ''
        ORDER BY AppName ASC;";

        var list = new List<string>();

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(reader.GetString(0));
        }

        return list;
    }

    private static WordEntry ReadWordEntry(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        Lemma = r.GetString(1),
        Pos = r.GetString(2),
        Frequency = r.GetInt32(3),
        GdexScore = r.GetDouble(4),
        CreatedAt = DateTime.TryParse(r.IsDBNull(5) ? null : r.GetString(5), out var createdAt)
            ? createdAt
            : DateTime.Now,
        ExampleSentence = r.IsDBNull(6) ? "" : r.GetString(6),
        ExampleTranslated = r.IsDBNull(7) ? null : r.GetString(7),
        ThumbsUp = r.IsDBNull(8) ? 0 : r.GetInt32(8),
        ThumbsDown = r.IsDBNull(9) ? 0 : r.GetInt32(9),
    };

    // ── 삭제 / 유지보수 ──────────────────────────────────────────────────
    public async Task DeleteTranslationAsync(long id)
    {
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync();
        // learned_words.ExampleId가 이 번역을 참조하고 있으면
        // FK 제약 위반이 발생하므로, 먼저 NULL로 초기화한다.
        await using (var pre = conn.CreateCommand())
        {
            pre.CommandText = @"
            UPDATE learned_words
            SET ExampleId = NULL, ExampleTranslated = NULL
            WHERE ExampleId = @id;";
            pre.Parameters.AddWithValue("@id", id);
            await pre.ExecuteNonQueryAsync();
        }
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM translations WHERE Id = @id;";
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync();
    }
    /// <summary>단어장에서 단어 1개를 삭제한다. translations는 그대로 유지.</summary>
    public async Task DeleteWordAsync(long id)
    {
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM learned_words WHERE Id = @id;";
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    // ── 사용자 메모장 ────────────────────────────────────────────────────
    public async Task<long> InsertMemoAsync(string content, string description)
    {
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
        INSERT INTO user_memos (Content, Description, MemoDate)
        VALUES (@content, @description, strftime('%Y-%m-%dT%H:%M:%f','now','localtime'))
        RETURNING Id;";

        cmd.Parameters.AddWithValue("@content", content);
        cmd.Parameters.AddWithValue("@description", description ?? "");

        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    public async Task UpdateMemoAsync(long id, string content, string description)
    {
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
        UPDATE user_memos
        SET Content = @content,
            Description = @description,
            MemoDate = strftime('%Y-%m-%dT%H:%M:%f','now','localtime')
        WHERE Id = @id;";

        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@content", content);
        cmd.Parameters.AddWithValue("@description", description ?? "");

        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<List<MemoItem>> GetMemosAsync(
        string? searchText = null,
        bool sortAscending = false)
    {
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();

        var sql = @"
        SELECT Id, Content, Description, MemoDate
        FROM user_memos
        WHERE (@search IS NULL
               OR Content LIKE '%' || @search || '%'
               OR Description LIKE '%' || @search || '%')
    ";

        sql += sortAscending
            ? " ORDER BY MemoDate ASC;"
            : " ORDER BY MemoDate DESC;";

        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue(
            "@search",
            string.IsNullOrWhiteSpace(searchText) ? DBNull.Value : searchText.Trim());

        var list = new List<MemoItem>();

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(ReadMemoItem(reader));
        }

        return list;
    }

    public async Task DeleteMemoAsync(long id)
    {
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM user_memos WHERE Id = @id;";
        cmd.Parameters.AddWithValue("@id", id);

        await cmd.ExecuteNonQueryAsync();
    }

    private static MemoItem ReadMemoItem(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        Content = r.GetString(1),
        Description = r.IsDBNull(2) ? "" : r.GetString(2),
        MemoDate = DateTime.TryParse(r.IsDBNull(3) ? null : r.GetString(3), out var memoDate)
            ? memoDate
            : DateTime.Now
    };
    /// <summary>
    /// 번역 기록 + 단어장 전체를 삭제하고 AUTOINCREMENT 시퀀스를 초기화한다.
    /// 이 작업은 되돌릴 수 없다.
    /// </summary>
    public async Task ResetAllAsync()
    {
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        // FK 일시 비활성화 후 순서 무관하게 전체 삭제
        cmd.CommandText = @"
        PRAGMA foreign_keys = OFF;
        DELETE FROM learned_words;
        DELETE FROM translations;
        DELETE FROM sqlite_sequence WHERE name IN ('learned_words','translations');
        PRAGMA foreign_keys = ON;
        VACUUM;";
        await cmd.ExecuteNonQueryAsync();
    }
    public async Task RunMaintenanceAsync()
    {
        await using var conn = new SqliteConnection(_connStr);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA optimize; ANALYZE;";
        await cmd.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await RunMaintenanceAsync();
        SqliteConnection.ClearAllPools();
    }
}
