using System.Data;
using System.IO;
using Microsoft.Data.Sqlite;

namespace SoundVault.App.Services;

/// <summary>音频扩展名白名单（扫描与拖拽共用一份）。</summary>
public static class AudioFormats
{
    public static readonly HashSet<string> Exts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".wav", ".aif", ".aiff", ".mp3", ".flac", ".m4a", ".ogg", ".opus", ".wma"
    };

    public static bool IsAudio(string path) => Exts.Contains(Path.GetExtension(path));
}

/// <summary>索引里的一行文件记录（不依赖 UI）。</summary>
public sealed class FileRow
{
    public string Path { get; init; } = "";
    public string Dir { get; init; } = "";
    public string Name { get; init; } = "";
    public long Size { get; set; }
    public long Mtime { get; set; }
    public int SampleRate { get; set; }
    public int Channels { get; set; }
    public int Bits { get; set; }
    public double DurationMs { get; set; }
    public byte[]? Peaks { get; set; }
    public bool Ok { get; set; } = true;
    public bool Missing { get; set; }        // 索引里有、磁盘上没了（硬盘没插 / 文件被挪走）
}

/// <summary>
/// SQLite 索引库：只存"能从文件本身读出来的事实"（元信息 + 波形峰值），
/// 用户数据（评分/收藏/库列表）仍在 store.json，两者通过绝对路径关联。
/// 位置：%LocalAppData%\SoundVault\index.db
/// </summary>
public sealed class IndexDb : IDisposable
{
    private const int SchemaVersion = 3;
    private readonly SqliteConnection _cn;
    private readonly object _gate = new();

    public string FilePath { get; }

    private IndexDb(SqliteConnection cn, string path)
    {
        _cn = cn;
        FilePath = path;
    }

    public static string DefaultPath
    {
        get
        {
            var dir = AppPaths.DataDir;   // 便携模式：exe 旁边的 data\
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "index.db");
        }
    }

    public static IndexDb Open(string? path = null)
    {
        path ??= DefaultPath;
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();

        var cn = new SqliteConnection(cs);
        cn.Open();
        var db = new IndexDb(cn, path);
        db.Init();
        return db;
    }

    private void Init()
    {
        Exec("PRAGMA journal_mode=WAL;");
        Exec("PRAGMA synchronous=NORMAL;");
        Exec("PRAGMA temp_store=MEMORY;");
        Exec("""
            CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT);
            CREATE TABLE IF NOT EXISTS files(
                id     INTEGER PRIMARY KEY,
                lib    TEXT NOT NULL,
                dir    TEXT NOT NULL,
                name   TEXT NOT NULL,
                path   TEXT NOT NULL UNIQUE,
                ext    TEXT NOT NULL,
                size   INTEGER NOT NULL,
                mtime  INTEGER NOT NULL,
                sr     INTEGER NOT NULL DEFAULT 0,
                ch     INTEGER NOT NULL DEFAULT 0,
                bits   INTEGER NOT NULL DEFAULT 0,
                dur    REAL    NOT NULL DEFAULT 0,
                peaks  BLOB,
                ok     INTEGER NOT NULL DEFAULT 1,
                missing INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS ix_files_lib     ON files(lib);
            CREATE INDEX IF NOT EXISTS ix_files_lib_dir ON files(lib, dir);
            CREATE INDEX IF NOT EXISTS ix_files_name    ON files(name);

            -- v0.6 组织层：标签与保存的查询（智能文件夹）
            CREATE TABLE IF NOT EXISTS tags(
                id    INTEGER PRIMARY KEY,
                name  TEXT NOT NULL UNIQUE COLLATE NOCASE
            );
            CREATE TABLE IF NOT EXISTS asset_tags(
                path    TEXT NOT NULL,
                tag_id  INTEGER NOT NULL,
                PRIMARY KEY(path, tag_id)
            );
            CREATE INDEX IF NOT EXISTS ix_asset_tags_tag  ON asset_tags(tag_id);
            CREATE INDEX IF NOT EXISTS ix_asset_tags_path ON asset_tags(path);

            CREATE TABLE IF NOT EXISTS saved_queries(
                id     INTEGER PRIMARY KEY,
                name   TEXT NOT NULL UNIQUE COLLATE NOCASE,
                query  TEXT NOT NULL,
                ord    INTEGER NOT NULL DEFAULT 0
            );
            """);

        // 老库补列（v3：missing）
        try
        {
            bool hasMissing = false;
            lock (_gate)
            {
                using var cmd = _cn.CreateCommand();
                cmd.CommandText = "PRAGMA table_info(files);";
                using var r = cmd.ExecuteReader();
                while (r.Read()) if (string.Equals(r.GetString(1), "missing", StringComparison.OrdinalIgnoreCase)) hasMissing = true;
            }
            if (!hasMissing) Exec("ALTER TABLE files ADD COLUMN missing INTEGER NOT NULL DEFAULT 0;");
        }
        catch { /* 补列失败不拦启动，读的时候按 0 处理 */ }

        var v = ScalarLong("SELECT CAST(value AS INTEGER) FROM meta WHERE key='schema';");
        if (v != SchemaVersion)
            Exec($"INSERT INTO meta(key,value) VALUES('schema','{SchemaVersion}') ON CONFLICT(key) DO UPDATE SET value=excluded.value;");
    }

    // ── 底层辅助 ────────────────────────────────────────────
    private void Exec(string sql)
    {
        lock (_gate)
        {
            using var cmd = _cn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
    }

    private long ScalarLong(string sql, params (string, object?)[] args)
    {
        lock (_gate)
        {
            using var cmd = _cn.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
            var r = cmd.ExecuteScalar();
            return r is null or DBNull ? 0 : Convert.ToInt64(r);
        }
    }

    // ── 查询 ────────────────────────────────────────────────
    /// <summary>库里每个子文件夹的文件数（侧栏与文件夹卡片用）。</summary>
    public List<(string Dir, int Count)> FolderCounts(string lib)
    {
        var list = new List<(string, int)>();
        lock (_gate)
        {
            using var cmd = _cn.CreateCommand();
            cmd.CommandText = "SELECT dir, COUNT(*) FROM files WHERE lib=$lib GROUP BY dir ORDER BY dir;";
            cmd.Parameters.AddWithValue("$lib", lib);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add((r.GetString(0), r.GetInt32(1)));
        }
        return list;
    }

    public int CountUnder(string lib) =>
        (int)ScalarLong("SELECT COUNT(*) FROM files WHERE lib=$lib;", ("$lib", lib));

    public int CountInDir(string dir) =>
        (int)ScalarLong("SELECT COUNT(*) FROM files WHERE dir=$dir;", ("$dir", dir));

    /// <summary>某一层文件夹里的音频文件。</summary>
    public List<FileRow> FilesInDir(string dir) =>
        Query("SELECT path,dir,name,size,mtime,sr,ch,bits,dur,peaks,ok,missing FROM files WHERE dir=$p ORDER BY path;",
              ("$p", dir));

    /// <summary>整个库（递归）的音频文件。</summary>
    public List<FileRow> FilesUnder(string lib) =>
        Query("SELECT path,dir,name,size,mtime,sr,ch,bits,dur,peaks,ok,missing FROM files WHERE lib=$p ORDER BY path;",
              ("$p", lib));

    /// <summary>最近导入：按文件修改时间倒序（拖动导入后 mtime 通常就是下载/复制的时间）。</summary>
    public List<FileRow> FilesRecent(string lib, int limit) =>
        Query("SELECT path,dir,name,size,mtime,sr,ch,bits,dur,peaks,ok,missing FROM files WHERE lib=$p " +
              "ORDER BY mtime DESC, path LIMIT $n;",
              ("$p", lib), ("$n", limit));

    /// <summary>深度模式：某文件夹及其所有子文件夹。</summary>
    public List<FileRow> FilesUnderDir(string dir)
    {
        var prefix = dir.EndsWith('\\') ? dir : dir + "\\";
        return Query("SELECT path,dir,name,size,mtime,sr,ch,bits,dur,peaks,ok,missing FROM files " +
                     "WHERE dir=$p OR dir LIKE $like ESCAPE '~' ORDER BY path;",
                     ("$p", dir), ("$like", EscapeLike(prefix) + "%"));
    }

    private static string EscapeLike(string s) => s.Replace("~", "~~").Replace("%", "~%").Replace("_", "~_");

    private List<FileRow> Query(string sql, params (string, object?)[] args)
    {
        var list = new List<FileRow>();
        lock (_gate)
        {
            using var cmd = _cn.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new FileRow
                {
                    Path = r.GetString(0),
                    Dir = r.GetString(1),
                    Name = r.GetString(2),
                    Size = r.GetInt64(3),
                    Mtime = r.GetInt64(4),
                    SampleRate = r.GetInt32(5),
                    Channels = r.GetInt32(6),
                    Bits = r.GetInt32(7),
                    DurationMs = r.GetDouble(8),
                    Peaks = r.IsDBNull(9) ? null : (byte[])r[9],
                    Ok = r.GetInt32(10) != 0,
                      Missing = r.FieldCount > 11 && r.GetInt32(11) != 0
                });
            }
        }
        return list;
    }

    /// <summary>增量扫描用：库里已有文件的 (路径 → 大小/修改时间)，用于跳过没变的文件。</summary>
    public Dictionary<string, (long Size, long Mtime)> Stamps(string lib)
    {
        var map = new Dictionary<string, (long, long)>(StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            using var cmd = _cn.CreateCommand();
            cmd.CommandText = "SELECT path,size,mtime FROM files WHERE lib=$lib;";
            cmd.Parameters.AddWithValue("$lib", lib);
            using var r = cmd.ExecuteReader();
            while (r.Read()) map[r.GetString(0)] = (r.GetInt64(1), r.GetInt64(2));
        }
        return map;
    }

    // ── 写入 ────────────────────────────────────────────────
    /// <summary>
    /// 一个事务里：写入新/变更行、标记本次见到的路径、删掉不再存在的文件。
    /// 全部路径都在 <paramref name="seen"/> 里（包括没变而跳过的）。
    /// </summary>
    public void ApplyScan(string lib, IReadOnlyList<FileRow> changed, IReadOnlyCollection<string> seen)
    {
        lock (_gate)
        {
            using var tx = _cn.BeginTransaction();

            using (var cmd = _cn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO files(lib,dir,name,path,ext,size,mtime,sr,ch,bits,dur,peaks,ok)
                    VALUES($lib,$dir,$name,$path,$ext,$size,$mtime,$sr,$ch,$bits,$dur,$peaks,$ok)
                    ON CONFLICT(path) DO UPDATE SET
                        lib=excluded.lib, dir=excluded.dir, name=excluded.name, ext=excluded.ext,
                        size=excluded.size, mtime=excluded.mtime, sr=excluded.sr, ch=excluded.ch,
                        bits=excluded.bits, dur=excluded.dur, peaks=excluded.peaks, ok=excluded.ok;
                    """;
                var pLib = cmd.Parameters.Add("$lib", SqliteType.Text);
                var pDir = cmd.Parameters.Add("$dir", SqliteType.Text);
                var pName = cmd.Parameters.Add("$name", SqliteType.Text);
                var pPath = cmd.Parameters.Add("$path", SqliteType.Text);
                var pExt = cmd.Parameters.Add("$ext", SqliteType.Text);
                var pSize = cmd.Parameters.Add("$size", SqliteType.Integer);
                var pMtime = cmd.Parameters.Add("$mtime", SqliteType.Integer);
                var pSr = cmd.Parameters.Add("$sr", SqliteType.Integer);
                var pCh = cmd.Parameters.Add("$ch", SqliteType.Integer);
                var pBits = cmd.Parameters.Add("$bits", SqliteType.Integer);
                var pDur = cmd.Parameters.Add("$dur", SqliteType.Real);
                var pPeaks = cmd.Parameters.Add("$peaks", SqliteType.Blob);
                var pOk = cmd.Parameters.Add("$ok", SqliteType.Integer);

                foreach (var f in changed)
                {
                    pLib.Value = lib;
                    pDir.Value = f.Dir;
                    pName.Value = f.Name;
                    pPath.Value = f.Path;
                    pExt.Value = Path.GetExtension(f.Path).ToLowerInvariant();
                    pSize.Value = f.Size;
                    pMtime.Value = f.Mtime;
                    pSr.Value = f.SampleRate;
                    pCh.Value = f.Channels;
                    pBits.Value = f.Bits;
                    pDur.Value = f.DurationMs;
                    pPeaks.Value = (object?)f.Peaks ?? DBNull.Value;
                    pOk.Value = f.Ok ? 1 : 0;
                    cmd.ExecuteNonQuery();
                }
            }

            // 先清空临时表，再批量灌入本次见到的路径，最后删掉不在其中的行
            ExecNoLock(tx, "CREATE TEMP TABLE IF NOT EXISTS seen(path TEXT PRIMARY KEY);");
            ExecNoLock(tx, "DELETE FROM seen;");

            using (var cmd = _cn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT OR IGNORE INTO seen(path) VALUES($p);";
                var p = cmd.Parameters.Add("$p", SqliteType.Text);
                foreach (var path in seen)
                {
                    p.Value = path;
                    cmd.ExecuteNonQuery();
                }
            }

            using (var cmd = _cn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"UPDATE files SET missing=1 WHERE lib=$lib AND path NOT IN (SELECT path FROM seen);
                                    UPDATE files SET missing=0 WHERE lib=$lib AND path IN (SELECT path FROM seen);";
                cmd.Parameters.AddWithValue("$lib", lib);
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    private void ExecNoLock(SqliteTransaction tx, string sql)
    {
        using var cmd = _cn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 把"lib 记成了某个子文件夹"的行归回它所属的库根（自包含：互相比较表里的 lib）。
    /// 不修的话，那些文件会从"全部音效/搜索"里消失——因为查询都按 lib 过滤。
    /// </summary>
    public int RepairNestedLibraries()
    {
        var libs = new List<string>();
        lock (_gate)
        {
            using var cmd = _cn.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT lib FROM files;";
            using var r = cmd.ExecuteReader();
            while (r.Read()) libs.Add(r.GetString(0));
        }

        int fixedRows = 0;
        foreach (var child in libs)
        {
            var owner = libs
                .Where(c => !string.Equals(c, child, StringComparison.OrdinalIgnoreCase))
                .Where(c => child.StartsWith(c.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(c => c.Length)
                .FirstOrDefault();
            if (owner is null) continue;

            lock (_gate)
            {
                using var cmd = _cn.CreateCommand();
                cmd.CommandText = "UPDATE files SET lib=$owner WHERE lib=$child;";
                cmd.Parameters.AddWithValue("$owner", owner);
                cmd.Parameters.AddWithValue("$child", child);
                fixedRows += cmd.ExecuteNonQuery();
            }
        }
        return fixedRows;
    }

    public long DiagSchema() => ScalarLong("SELECT CAST(value AS INTEGER) FROM meta WHERE key='schema';");

    /// <summary>文件改名后同步索引里的路径（评分/标签是按路径挂的，必须跟着走）。</summary>
    public void RenamePath(string oldPath, string newPath)
    {
        lock (_gate)
        {
            using var tx = _cn.BeginTransaction();

            using (var c1 = _cn.CreateCommand())
            {
                c1.Transaction = tx;
                c1.CommandText = "UPDATE files SET path=$new WHERE path=$old;";
                c1.Parameters.AddWithValue("$new", newPath);
                c1.Parameters.AddWithValue("$old", oldPath);
                c1.ExecuteNonQuery();
            }

            using (var c2 = _cn.CreateCommand())
            {
                c2.Transaction = tx;
                c2.CommandText = "UPDATE OR REPLACE asset_tags SET path=$new WHERE path=$old;";
                c2.Parameters.AddWithValue("$new", newPath);
                c2.Parameters.AddWithValue("$old", oldPath);
                c2.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }
    public bool DiagHasMissingColumn()
    {
        lock (_gate)
        {
            using var cmd = _cn.CreateCommand();
            cmd.CommandText = "PRAGMA table_info(files);";
            using var r = cmd.ExecuteReader();
            while (r.Read()) if (string.Equals(r.GetString(1), "missing", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public long DiagMissingCount() => ScalarLong("SELECT COUNT(*) FROM files WHERE missing=1;");

    public long DiagTotalRows() => ScalarLong("SELECT COUNT(*) FROM files;");

    public List<string> DiagLibs()
    {
        var list = new List<string>();
        lock (_gate)
        {
            using var cmd = _cn.CreateCommand();
            cmd.CommandText = "SELECT lib || ' | ' || COUNT(*) FROM files GROUP BY lib;";
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(r.GetString(0));
        }
        return list;
    }
    /// <summary>从库里移除整个素材包（重连 / 移除库时用）。</summary>
    public void DropLibrary(string lib) =>
        Exec("DELETE FROM files WHERE lib=$lib;".Replace("$lib", "'" + lib.Replace("'", "''") + "'"));

    /// <summary>
    /// 找疑似重复：同一个库里"文件大小相同 + 时长相同（0.1 秒精度）"的算一组。
    /// 不比对内容（读 1 万个文件的哈希太慢），先给个可人工确认的候选列表。
    /// </summary>
    public List<List<FileRow>> DuplicateGroups(string lib, int maxGroups = 200)
    {
        var rows = new List<FileRow>();
        lock (_gate)
        {
            using var cmd = _cn.CreateCommand();
            cmd.CommandText = @"SELECT path,dir,name,size,mtime,sr,ch,bits,dur,peaks,ok,missing FROM files
                                WHERE lib=$lib AND missing=0 ORDER BY size, dur;";
            cmd.Parameters.AddWithValue("$lib", lib);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                rows.Add(new FileRow
                {
                    Path = r.GetString(0),
                    Dir = r.GetString(1),
                    Name = r.GetString(2),
                    Size = r.GetInt64(3),
                    Mtime = r.GetInt64(4),
                    SampleRate = r.GetInt32(5),
                    Channels = r.GetInt32(6),
                    Bits = r.GetInt32(7),
                    DurationMs = r.GetDouble(8),
                    Peaks = r.IsDBNull(9) ? null : (byte[])r[9],
                    Ok = r.GetInt32(10) != 0,
                    Missing = r.FieldCount > 11 && r.GetInt32(11) != 0
                });
            }
        }

        return rows
            .GroupBy(f => (f.Size, Bucket: (long)Math.Round(f.DurationMs / 100)))
            .Where(g => g.Count() > 1)
            .OrderByDescending(g => g.Count())
            .Take(maxGroups)
            .Select(g => g.OrderBy(f => f.Path).ToList())
            .ToList();
    }
    // ── 标签 ────────────────────────────────────────────────
    /// <summary>所有标签 + 各自用了几次（侧栏/右键菜单用）。</summary>
    public List<(string Tag, int Count)> TagCounts()
    {
        var list = new List<(string, int)>();
        lock (_gate)
        {
            using var cmd = _cn.CreateCommand();
            cmd.CommandText = @"SELECT t.name, COUNT(a.path) FROM tags t
                                LEFT JOIN asset_tags a ON a.tag_id = t.id
                                GROUP BY t.id ORDER BY t.name;";
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add((r.GetString(0), r.GetInt32(1)));
        }
        return list;
    }

    /// <summary>一批文件各自的标签（一次查完，别逐个查）。</summary>
    public Dictionary<string, List<string>> TagsByPath(IEnumerable<string> paths)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var wanted = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0) return map;

        lock (_gate)
        {
            using var cmd = _cn.CreateCommand();
            cmd.CommandText = @"SELECT a.path, t.name FROM asset_tags a
                                JOIN tags t ON t.id = a.tag_id
                                ORDER BY t.name;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var p = r.GetString(0);
                if (!wanted.Contains(p)) continue;
                if (!map.TryGetValue(p, out var list)) map[p] = list = new List<string>();
                list.Add(r.GetString(1));
            }
        }
        return map;
    }

    /// <summary>给一个文件加标签（标签不存在就建）。</summary>
    public void AddTag(string path, string tag)
    {
        tag = tag.Trim();
        if (tag.Length == 0) return;

        lock (_gate)
        {
            using var tx = _cn.BeginTransaction();

            long id;
            using (var cmd = _cn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO tags(name) VALUES($n) ON CONFLICT(name) DO NOTHING;";
                cmd.Parameters.AddWithValue("$n", tag);
                cmd.ExecuteNonQuery();
            }
            using (var cmd = _cn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT id FROM tags WHERE name=$n;";
                cmd.Parameters.AddWithValue("$n", tag);
                id = Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
            }
            if (id != 0)
            {
                using var cmd = _cn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT OR IGNORE INTO asset_tags(path, tag_id) VALUES($p, $t);";
                cmd.Parameters.AddWithValue("$p", path);
                cmd.Parameters.AddWithValue("$t", id);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>把一个文件的标签整体换成给定的一组（右键菜单里勾选/取消用）。</summary>
    public void SetTags(string path, IEnumerable<string> tags)
    {
        var wanted = tags.Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        lock (_gate)
        {
            using var tx = _cn.BeginTransaction();

            using (var cmd = _cn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM asset_tags WHERE path=$p;";
                cmd.Parameters.AddWithValue("$p", path);
                cmd.ExecuteNonQuery();
            }

            foreach (var tag in wanted)
            {
                using (var cmd = _cn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "INSERT INTO tags(name) VALUES($n) ON CONFLICT(name) DO NOTHING;";
                    cmd.Parameters.AddWithValue("$n", tag);
                    cmd.ExecuteNonQuery();
                }
                using (var cmd = _cn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = @"INSERT OR IGNORE INTO asset_tags(path, tag_id)
                                        SELECT $p, id FROM tags WHERE name=$n;";
                    cmd.Parameters.AddWithValue("$p", path);
                    cmd.Parameters.AddWithValue("$n", tag);
                    cmd.ExecuteNonQuery();
                }
            }

            tx.Commit();
        }
    }

    /// <summary>给标签改名；目标名字已存在时就是"合并"。</summary>
    public void RenameTag(string from, string to)
    {
        from = from.Trim();
        to = to.Trim();
        if (from.Length == 0 || to.Length == 0 || from.Equals(to, StringComparison.OrdinalIgnoreCase)) return;

        lock (_gate)
        {
            using var tx = _cn.BeginTransaction();

            long fromId = 0, toId = 0;
            using (var cmd = _cn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT id FROM tags WHERE name=$n;";
                cmd.Parameters.AddWithValue("$n", from);
                fromId = Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
            }
            if (fromId == 0) { tx.Rollback(); return; }

            using (var cmd = _cn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO tags(name) VALUES($n) ON CONFLICT(name) DO NOTHING;";
                cmd.Parameters.AddWithValue("$n", to);
                cmd.ExecuteNonQuery();
            }
            using (var cmd = _cn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT id FROM tags WHERE name=$n;";
                cmd.Parameters.AddWithValue("$n", to);
                toId = Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
            }

            // 撞了的（同一个文件已经有两个标签）忽略；再把旧标签摘掉
            using (var cmd = _cn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE OR IGNORE asset_tags SET tag_id=$to WHERE tag_id=$from;";
                cmd.Parameters.AddWithValue("$to", toId);
                cmd.Parameters.AddWithValue("$from", fromId);
                cmd.ExecuteNonQuery();
            }
            using (var cmd = _cn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM asset_tags WHERE tag_id=$from;";
                cmd.Parameters.AddWithValue("$from", fromId);
                cmd.ExecuteNonQuery();
            }
            using (var cmd = _cn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM tags WHERE id=$from;";
                cmd.Parameters.AddWithValue("$from", fromId);
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    /// <summary>删掉一个标签，并把它的关联清掉（音效本身不动）。</summary>
    public void DeleteTag(string name)
    {
        lock (_gate)
        {
            using var tx = _cn.BeginTransaction();
            using (var cmd = _cn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM asset_tags WHERE tag_id = (SELECT id FROM tags WHERE name=$n);";
                cmd.Parameters.AddWithValue("$n", name);
                cmd.ExecuteNonQuery();
            }
            using (var cmd = _cn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM tags WHERE name=$n;";
                cmd.Parameters.AddWithValue("$n", name);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }
    /// <summary>某个标签下的所有文件路径（过滤语法 #tag 用）。</summary>
    public HashSet<string> PathsWithTag(string tag)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            using var cmd = _cn.CreateCommand();
            cmd.CommandText = "SELECT a.path FROM asset_tags a JOIN tags t ON t.id=a.tag_id WHERE t.name=$n;";
            cmd.Parameters.AddWithValue("$n", tag);
            using var r = cmd.ExecuteReader();
            while (r.Read()) set.Add(r.GetString(0));
        }
        return set;
    }

    // ── 保存的查询（智能文件夹）────────────────────────────
    public List<(int Id, string Name, string Query)> SavedQueries()
    {
        var list = new List<(int, string, string)>();
        lock (_gate)
        {
            using var cmd = _cn.CreateCommand();
            cmd.CommandText = "SELECT id, name, query FROM saved_queries ORDER BY ord, name;";
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add((r.GetInt32(0), r.GetString(1), r.GetString(2)));
        }
        return list;
    }

    public void SaveQuery(string name, string query)
    {
        name = name.Trim();
        query = query.Trim();
        if (name.Length == 0 || query.Length == 0) return;

        Exec("INSERT INTO saved_queries(name, query) VALUES('" +
             name.Replace("'", "''") + "', '" + query.Replace("'", "''") + "') " +
             "ON CONFLICT(name) DO UPDATE SET query=excluded.query;");
    }

    public void DeleteQuery(string name) =>
        Exec("DELETE FROM saved_queries WHERE name='" + name.Replace("'", "''") + "';");


    public void Dispose()
    {
        lock (_gate) { try { _cn.Close(); } catch { } _cn.Dispose(); }
    }
}

