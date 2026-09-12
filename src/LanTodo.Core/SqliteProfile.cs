using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LanTodo.Core;

public sealed class SqliteProfile : IProfileDatabase
{
    public const string FileName = "LanTodo.sqlite";
    private readonly object gate = new();
    private ProfileLease lease;
    private SqliteConnection connection = null!;
    public string FilePath { get; private set; }

    public SqliteProfile(string root)
    {
        Directory.CreateDirectory(root);
        FilePath = Path.Combine(Path.GetFullPath(root), FileName);
        lease = new ProfileLease(FilePath);
        try
        {
            bool existing = File.Exists(FilePath);
            connection = Open(FilePath);
            using var version = connection.CreateCommand(); version.CommandText = "PRAGMA user_version";
            var schema = Convert.ToInt32(version.ExecuteScalar());
            if (schema > 1) throw new InvalidDataException("数据库由更新版本创建，请升级LanTodo后打开。");
            if (schema == 0)
            {
                if (existing) throw new InvalidDataException("数据库缺少版本信息，请保留文件并恢复备份。");
                using var tx = connection.BeginTransaction();
                using var create = connection.CreateCommand(); create.Transaction = tx;
                create.CommandText = """
                    CREATE TABLE revisions(id TEXT PRIMARY KEY, todo_id TEXT NOT NULL, payload BLOB NOT NULL);
                    CREATE INDEX revisions_todo ON revisions(todo_id);
                    CREATE TABLE metadata(key TEXT PRIMARY KEY, value BLOB NOT NULL);
                    PRAGMA application_id=1279349828;
                    PRAGMA user_version=1;
                    """;
                create.ExecuteNonQuery(); tx.Commit();
            }
            using var check = connection.CreateCommand(); check.CommandText = "PRAGMA application_id";
            if (Convert.ToInt32(check.ExecuteScalar()) != 1279349828) throw new InvalidDataException("这不是LanTodo数据库。");
            check.CommandText = "PRAGMA journal_mode=DELETE; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON;";
            check.ExecuteNonQuery();
            check.CommandText = "PRAGMA quick_check";
            if (!Equals(check.ExecuteScalar(), "ok")) throw new InvalidDataException("SQLite 完整性检查失败，请保留数据库并恢复备份。");
        }
        catch { connection?.Dispose(); lease.Dispose(); throw; }
    }
    private static SqliteConnection Open(string path)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        try
        {
            c.Open(); return c;
        }
        catch { c.Dispose(); throw; }
    }
    public Revision[] ReadRevisions()
    {
        lock (gate)
        {
            using var cmd = connection.CreateCommand(); cmd.CommandText = "SELECT id,todo_id,payload FROM revisions";
            using var reader = cmd.ExecuteReader(); var result = new List<Revision>();
            while (reader.Read())
            {
                var bytes = (byte[])reader[2];
                if (bytes.Length > Json.MaxRevisionBytes) throw new InvalidDataException("数据库记录过大。");
                var r = Json.Read<Revision>(bytes); r.Validate();
                if (r.Id != reader.GetString(0) || r.Body.TodoId != reader.GetString(1)) throw new InvalidDataException("数据库记录校验失败。");
                result.Add(r);
            }
            return result.ToArray();
        }
    }
    public void Append(IReadOnlyList<Revision> revisions)
        => AppendCore(revisions, null, null);
    public void AppendWithMetadata(IReadOnlyList<Revision> revisions, string key, byte[] value)
        => AppendCore(revisions, key, value);
    private void AppendCore(IReadOnlyList<Revision> revisions, string? key, byte[]? value)
    {
        if (revisions.Count == 0 && key is null) return;
        lock (gate)
        {
            using var tx = connection.BeginTransaction();
            foreach (var r in revisions)
            {
                using var cmd = connection.CreateCommand(); cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO revisions(id,todo_id,payload) VALUES($id,$todo,$payload)";
                cmd.Parameters.AddWithValue("$id", r.Id); cmd.Parameters.AddWithValue("$todo", r.Body.TodoId);
                cmd.Parameters.AddWithValue("$payload", JsonSerializer.SerializeToUtf8Bytes(r, Json.Options)); cmd.ExecuteNonQuery();
            }
            if (key is not null)
            {
                using var metadata = connection.CreateCommand(); metadata.Transaction = tx;
                metadata.CommandText = "INSERT INTO metadata(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
                metadata.Parameters.AddWithValue("$key", key); metadata.Parameters.AddWithValue("$value", value!);
                metadata.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }
    public byte[]? ReadMetadata(string key)
    {
        lock (gate)
        {
            using var cmd = connection.CreateCommand(); cmd.CommandText = "SELECT value FROM metadata WHERE key=$key";
            cmd.Parameters.AddWithValue("$key", key); return cmd.ExecuteScalar() as byte[];
        }
    }
    public void WriteMetadata(string key, byte[] value)
    {
        lock (gate)
        {
            using var cmd = connection.CreateCommand(); cmd.CommandText = "INSERT INTO metadata(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
            cmd.Parameters.AddWithValue("$key", key); cmd.Parameters.AddWithValue("$value", value); cmd.ExecuteNonQuery();
        }
    }
    private void CopyTo(string destinationFile)
    {
        lock (gate)
        {
            using var target = Open(destinationFile);
            connection.BackupDatabase(target);
        }
    }
    internal void ClearRevisions()
    {
        lock (gate)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "PRAGMA secure_delete=ON; DELETE FROM revisions;";
            cmd.ExecuteNonQuery();
        }
    }
    internal void MoveTo(string destination, Action activateLocation)
    {
        lock (gate)
        {
            var targetFile = Path.Combine(destination, FileName);
            SqliteProfile? target = null; bool closed = false, created = false;
            try
            {
                using (new FileStream(targetFile, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
                created = true;
                CopyTo(targetFile); target = new SqliteProfile(destination);
                if (!ReadRevisions().Select(r => r.Id).ToHashSet().SetEquals(target.ReadRevisions().Select(r => r.Id)))
                    throw new IOException("迁移校验失败，原数据库保留。");
                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = "SELECT key,value FROM metadata"; using var reader = cmd.ExecuteReader();
                    while (reader.Read()) if (!((byte[])reader[1]).SequenceEqual(target.ReadMetadata(reader.GetString(0)) ?? []))
                        throw new IOException("设备资料迁移校验失败。");
                }
                activateLocation();
                connection.Dispose(); closed = true;
                File.Delete(FilePath);
                lease.Dispose();
                connection = target.connection; lease = target.lease; FilePath = target.FilePath;
                target = null; closed = false;
            }
            catch
            {
                if (closed) connection = Open(FilePath);
                target?.Dispose();
                if (created && File.Exists(targetFile)) File.Delete(targetFile);
                throw;
            }
        }
    }
    public void Dispose() { lock (gate) { connection.Dispose(); lease.Dispose(); } }

    // No lock file beside the database. A dedicated thread owns/releases the Windows mutex,
    // because runtime disposal may resume on a different thread after an await.
    private sealed class ProfileLease : IDisposable
    {
        private static readonly HashSet<string> Active = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        private readonly string path;
        private readonly ManualResetEventSlim stop = new();
        private Thread? thread;
        private FileStream? linuxLease;
        private bool disposed;
        public ProfileLease(string path)
        {
            this.path = path;
            lock (Active) if (!Active.Add(path)) throw new IOException("该数据库已在使用中。");
            if (!OperatingSystem.IsWindows())
            {
                // NAS containers must never share a live profile. Android has one private runtime.
                if (!OperatingSystem.IsAndroid())
                {
                    try { linuxLease = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                    catch { Dispose(); throw; }
                }
                return;
            }
            using var ready = new ManualResetEventSlim(); Exception? error = null;
            thread = new Thread(() =>
            {
                bool owned = false;
                Mutex? mutex = null;
                try
                {
                    mutex = new Mutex(false, "Local\\LanTodo.Database." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant()))));
                    try { owned = mutex.WaitOne(0); } catch (AbandonedMutexException) { owned = true; }
                    if (!owned) throw new IOException("该数据库已在另一个进程中使用。");
                }
                catch (Exception ex) { error = ex; }
                finally { ready.Set(); }
                if (owned) { stop.Wait(); mutex!.ReleaseMutex(); }
                mutex?.Dispose();
            }) { IsBackground = true, Name = "LanTodo database lease" };
            thread.Start(); ready.Wait();
            if (error is not null) { Dispose(); throw error; }
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            stop.Set(); thread?.Join(); linuxLease?.Dispose(); stop.Dispose(); lock (Active) Active.Remove(path);
        }
    }
}
