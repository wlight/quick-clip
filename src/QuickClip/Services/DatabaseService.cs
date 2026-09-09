using System.IO;
using Microsoft.Data.Sqlite;
using QuickClip.Models;

namespace QuickClip.Services;

/// <summary>SQLite 本地存储：剪贴板历史；超出条数上限时淘汰最旧非置顶。</summary>
public sealed class DatabaseService : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _dedupGate = new(1, 1);
    private bool _dedupKeysReady;

    /// <summary>当前数据库文件路径（本地文件或网络 UNC 路径）。</summary>
    public string CurrentPath { get; }

    public DatabaseService(string dbPath)
    {
        CurrentPath = dbPath;
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString());
        _connection.Open();
        EnsureSchema();
    }

    private void EnsureSchema()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS clipboard_items (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                content_type TEXT NOT NULL,
                text_content TEXT,
                preview_path TEXT,
                qr_content TEXT,
                char_count INTEGER,
                is_pinned INTEGER DEFAULT 0,
                dedup_key TEXT,
                created_at DATETIME DEFAULT CURRENT_TIMESTAMP
            );
            CREATE INDEX IF NOT EXISTS idx_created_at ON clipboard_items(created_at);
            """;
        cmd.ExecuteNonQuery();

        // 老库迁移：补齐去重键列（新库建表时已包含，此步忽略重复列错误）
        try
        {
            using var migrate = _connection.CreateCommand();
            migrate.CommandText = "ALTER TABLE clipboard_items ADD COLUMN dedup_key TEXT;";
            migrate.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            // 列已存在（新库或已迁移过的老库）
        }

        using (var index = _connection.CreateCommand())
        {
            index.CommandText = "CREATE INDEX IF NOT EXISTS idx_dedup_key ON clipboard_items(dedup_key);";
            index.ExecuteNonQuery();
        }
    }

    public async Task<long> InsertAsync(ClipboardItem item, string? dedupKey)
    {
        await _gate.WaitAsync();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO clipboard_items
                    (content_type, text_content, preview_path, qr_content, char_count, is_pinned, dedup_key)
                VALUES ($type, $text, $preview, $qr, $charCount, $pinned, $dedupKey);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$type", item.ContentType.ToString());
            cmd.Parameters.AddWithValue("$text", (object?)item.TextContent ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$preview", (object?)item.PreviewPath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$qr", (object?)item.QrContent ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$charCount", item.CharCount);
            cmd.Parameters.AddWithValue("$pinned", item.IsPinned ? 1 : 0);
            cmd.Parameters.AddWithValue("$dedupKey", (object?)dedupKey ?? DBNull.Value);
            item.Id = (long)(await cmd.ExecuteScalarAsync())!;
            return item.Id;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 一次性为老库中缺少去重键的历史条目补齐 dedup_key（文本/文件按内容哈希、图片按文件哈希）。
    /// 幂等：同一实例只执行一次；新库无历史时立即完成。
    /// </summary>
    public async Task EnsureDedupKeysReadyAsync()
    {
        if (_dedupKeysReady)
        {
            return;
        }

        await _dedupGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_dedupKeysReady)
            {
                return;
            }

            List<(long Id, ClipboardContentType Type, string? Text, string? Preview)> pending;
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                pending = new List<(long, ClipboardContentType, string?, string?)>();
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = """
                    SELECT id, content_type, text_content, preview_path
                    FROM clipboard_items
                    WHERE dedup_key IS NULL;
                    """;
                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    long id = reader.GetInt64(0);
                    var type = Enum.TryParse<ClipboardContentType>(reader.GetString(1), out var parsed)
                        ? parsed
                        : ClipboardContentType.Text;
                    string? text = reader.IsDBNull(2) ? null : reader.GetString(2);
                    string? preview = reader.IsDBNull(3) ? null : reader.GetString(3);
                    pending.Add((id, type, text, preview));
                }
            }
            finally
            {
                _gate.Release();
            }

            if (pending.Count == 0)
            {
                _dedupKeysReady = true;
                return;
            }

            // 哈希计算放线程池，避免在调用方（如 UI 线程）上做耗时的图片哈希
            var updates = await Task.Run(() =>
            {
                var list = new List<(long Id, string Key)>(pending.Count);
                foreach (var (id, type, text, preview) in pending)
                {
                    string key = type switch
                    {
                        ClipboardContentType.Text or ClipboardContentType.Link =>
                            ClipboardDataExtractor.TextDedupKey(text) ?? string.Empty,
                        ClipboardContentType.File =>
                            ClipboardDataExtractor.FileTextDedupKey(text) ?? string.Empty,
                        ClipboardContentType.Image =>
                            HashImageKey(preview) ?? string.Empty,
                        _ => string.Empty
                    };
                    list.Add((id, key));
                }

                return list;
            }).ConfigureAwait(false);

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                foreach (var (id, key) in updates)
                {
                    using var cmd = _connection.CreateCommand();
                    cmd.CommandText = "UPDATE clipboard_items SET dedup_key = $key WHERE id = $id;";
                    cmd.Parameters.AddWithValue("$key", key);
                    cmd.Parameters.AddWithValue("$id", id);
                    await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                }

                _dedupKeysReady = true;
            }
            finally
            {
                _gate.Release();
            }
        }
        finally
        {
            _dedupGate.Release();
        }
    }

    /// <summary>
    /// 全历史去重命中：按去重键找到最近一条记录并刷新 created_at（置顶），返回该记录；
    /// 未命中返回 null，调用方应新增入库。
    /// replacementPreview 供图片去重时传入本次新生成的预览图：若旧记录预览文件已丢失，
    /// 则改为引用新文件，避免把唯一可用的图片删掉。
    /// </summary>
    public async Task<ClipboardItem?> TouchByDedupKeyAsync(
        string dedupKey, long charCount, string? replacementPreview = null)
    {
        await _gate.WaitAsync();
        try
        {
            ClipboardItem? item = null;
            using (var find = _connection.CreateCommand())
            {
                find.CommandText = """
                    SELECT id, content_type, text_content, preview_path, qr_content,
                           char_count, is_pinned, created_at
                    FROM clipboard_items
                    WHERE dedup_key = $key
                    ORDER BY is_pinned DESC, created_at DESC, id DESC
                    LIMIT 1;
                    """;
                find.Parameters.AddWithValue("$key", dedupKey);
                using var reader = await find.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    item = ReadItem(reader);
                }
            }

            if (item == null)
            {
                return null;
            }

            bool adoptPreview = !string.IsNullOrEmpty(replacementPreview)
                && (string.IsNullOrEmpty(item.PreviewPath) || !File.Exists(item.PreviewPath));

            using var update = _connection.CreateCommand();
            update.CommandText = """
                UPDATE clipboard_items
                SET created_at = datetime('now'),
                    char_count = $charCount,
                    preview_path = CASE WHEN $adopt = 1 THEN $preview ELSE preview_path END
                WHERE id = $id;
                """;
            update.Parameters.AddWithValue("$charCount", charCount);
            update.Parameters.AddWithValue("$adopt", adoptPreview ? 1 : 0);
            update.Parameters.AddWithValue("$preview", (object?)replacementPreview ?? DBNull.Value);
            update.Parameters.AddWithValue("$id", item.Id);
            await update.ExecuteNonQueryAsync();

            if (adoptPreview)
            {
                item.PreviewPath = replacementPreview;
            }

            item.CreatedAt = DateTime.Now;
            return item;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<List<ClipboardItem>> GetRecentAsync(int limit = 300)
    {
        await _gate.WaitAsync();
        try
        {
            var items = new List<ClipboardItem>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT id, content_type, text_content, preview_path, qr_content,
                       char_count, is_pinned, created_at
                FROM clipboard_items
                ORDER BY is_pinned DESC, created_at DESC, id DESC
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$limit", limit);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                items.Add(ReadItem(reader));
            }

            return items;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(long id)
    {
        await _gate.WaitAsync();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM clipboard_items WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpdateQrContentAsync(long id, string qrContent)
    {
        await _gate.WaitAsync();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE clipboard_items SET qr_content = $qr WHERE id = $id;";
            cmd.Parameters.AddWithValue("$qr", qrContent);
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task TogglePinAsync(long id, bool pinned)
    {
        await _gate.WaitAsync();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE clipboard_items SET is_pinned = $pinned WHERE id = $id;";
            cmd.Parameters.AddWithValue("$pinned", pinned ? 1 : 0);
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 按最大条数淘汰：保留全部置顶 + 最新的非置顶，使总数不超过 maxItems。
    /// 返回删除的 id 与 preview 路径，便于清理缓存/文件。
    /// </summary>
    public async Task<List<(long Id, string? PreviewPath)>> TrimToMaxItemsAsync(int maxItems)
    {
        if (maxItems < 1)
        {
            maxItems = 1;
        }

        await _gate.WaitAsync();
        try
        {
            long total;
            using (var countCmd = _connection.CreateCommand())
            {
                countCmd.CommandText = "SELECT COUNT(*) FROM clipboard_items;";
                total = (long)(await countCmd.ExecuteScalarAsync())!;
            }

            long excess = total - maxItems;
            if (excess <= 0)
            {
                return new List<(long, string?)>();
            }

            var doomed = new List<(long Id, string? PreviewPath)>();
            using (var sel = _connection.CreateCommand())
            {
                sel.CommandText = """
                    SELECT id, preview_path FROM clipboard_items
                    WHERE is_pinned = 0
                    ORDER BY created_at ASC, id ASC
                    LIMIT $n;
                    """;
                sel.Parameters.AddWithValue("$n", excess);
                using var reader = await sel.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    long id = reader.GetInt64(0);
                    string? preview = reader.IsDBNull(1) ? null : reader.GetString(1);
                    doomed.Add((id, preview));
                }
            }

            if (doomed.Count == 0)
            {
                return doomed;
            }

            using var del = _connection.CreateCommand();
            del.CommandText = $"DELETE FROM clipboard_items WHERE id IN ({string.Join(",", doomed.Select(d => d.Id))});";
            await del.ExecuteNonQueryAsync();
            return doomed;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>清除今日非置顶历史（本地日历日）。</summary>
    public async Task<int> DeleteTodayUnpinnedAsync()
    {
        await _gate.WaitAsync();
        try
        {
            string day = DateTime.Now.ToString("yyyy-MM-dd");
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                DELETE FROM clipboard_items
                WHERE is_pinned = 0
                  AND substr(created_at, 1, 10) = $day;
                """;
            cmd.Parameters.AddWithValue("$day", day);
            return await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 清空全部非置顶历史，置顶保留。
    /// 返回删除条数与 preview 路径，便于清理缩略图文件。
    /// </summary>
    public async Task<(int Count, List<string> PreviewPaths)> DeleteAllUnpinnedAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var previews = new List<string>();
            using (var sel = _connection.CreateCommand())
            {
                sel.CommandText = """
                    SELECT preview_path FROM clipboard_items
                    WHERE is_pinned = 0 AND preview_path IS NOT NULL AND preview_path != '';
                    """;
                using var reader = await sel.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    if (!reader.IsDBNull(0) && reader.GetString(0) is { Length: > 0 } path)
                    {
                        previews.Add(path);
                    }
                }
            }

            using var del = _connection.CreateCommand();
            del.CommandText = "DELETE FROM clipboard_items WHERE is_pinned = 0;";
            int n = await del.ExecuteNonQueryAsync();
            return (n, previews);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>清理数据库中不再引用的孤儿预览图片文件。</summary>
    public async Task CleanupOrphanPreviewsAsync(AppPaths paths)
    {
        await _gate.WaitAsync();
        List<string> referenced;
        try
        {
            referenced = new List<string>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT preview_path FROM clipboard_items WHERE preview_path IS NOT NULL;";
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (reader.GetString(0) is { Length: > 0 } path)
                {
                    referenced.Add(path);
                }
            }
        }
        finally
        {
            _gate.Release();
        }

        var set = new HashSet<string>(referenced, StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var file in Directory.EnumerateFiles(paths.PreviewDir))
            {
                if (!set.Contains(file))
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch
                    {
                        // 忽略删除失败（文件可能正被占用）
                    }
                }
            }
        }
        catch
        {
            // 目录不存在等异常忽略
        }
    }

    /// <summary>压缩数据库文件，回收已删除记录占用的磁盘空间（每日执行一次即可）。</summary>
    public async Task VacuumAsync()
    {
        await _gate.WaitAsync();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "VACUUM;";
            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _connection.Dispose();

    private static string? HashImageKey(string? previewPath)
    {
        if (string.IsNullOrEmpty(previewPath) || !File.Exists(previewPath))
        {
            return null;
        }

        try
        {
            return "img:" + ClipboardDataExtractor.ComputeHash(previewPath);
        }
        catch
        {
            return null;
        }
    }

    private static ClipboardItem ReadItem(SqliteDataReader reader)
    {
        return new ClipboardItem
        {
            Id = reader.GetInt64(0),
            ContentType = Enum.TryParse<ClipboardContentType>(reader.GetString(1), out var type)
                ? type
                : ClipboardContentType.Text,
            TextContent = reader.IsDBNull(2) ? null : reader.GetString(2),
            PreviewPath = reader.IsDBNull(3) ? null : reader.GetString(3),
            QrContent = reader.IsDBNull(4) ? null : reader.GetString(4),
            CharCount = reader.GetInt64(5),
            IsPinned = reader.GetInt64(6) != 0,
            CreatedAt = reader.IsDBNull(7) ? DateTime.Now : ParseDate(reader.GetString(7))
        };
    }

    private static DateTime ParseDate(string value)
    {
        return DateTime.TryParse(value, out var dt) ? dt : DateTime.Now;
    }
}



