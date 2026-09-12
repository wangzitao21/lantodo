using System.IO.Compression;
using System.Security.Cryptography;

namespace LanTodo.Core;

public sealed record Attachment(string Hash, string Name, long Size, string Kind = "file",
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Instance = null)
{
    public void Validate()
    {
        if (Instance is not null && !Guid.TryParseExact(Instance, "N", out _)) throw new InvalidDataException("附件版本无效。");
        if (!Json.IsHash(Hash) || Size < 0 || string.IsNullOrWhiteSpace(Name) || Name.Length > 255 ||
            Name is "." or ".." || Name.IndexOfAny(['/', '\\', ':', '\0']) >= 0 || Name.Any(char.IsControl) ||
            Kind is not ("file" or "image" or "folder")) throw new InvalidDataException("附件信息无效。");
    }
    [System.Text.Json.Serialization.JsonIgnore]
    public string Key => Hash + (Instance is null ? "" : ":" + Instance);
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsAudio => Kind == "file" && Path.GetExtension(Name).ToLowerInvariant() is ".m4a" or ".aac" or ".mp3" or ".wav";
    public string Description => $"{(Kind == "image" ? "图片" : Kind == "folder" ? "文件夹" : "文件")} · {Name} · {(Size < 1048576 ? $"{Size / 1024d:0.#} KB" : $"{Size / 1048576d:0.#} MB")}";
}

// Content addressed originals, outside revision JSON. Only verified complete files are visible.
public sealed class AttachmentStore(Func<string> root, Func<IProfileDatabase>? database = null)
{
    internal const string InvalidMetadata = "attachment-invalidations-v1";
    public const int ChunkSize = 256 * 1024;
    public string DirectoryPath => Path.Combine(root(), "attachments");
    private sealed record Index(Dictionary<string,string> Files, string[] Invalid, string[]? Seen = null);
    private Dictionary<string,string> names = new();
    private readonly HashSet<string> invalid = new();
    private readonly HashSet<string> seen = new();
    private string? loadedRoot;
    public event Action? Changed;
    private string TransferPath => Path.Combine(root(), ".transfers");
    private void LoadIndex()
    {
        if (loadedRoot == root()) return;
        var path = Path.Combine(root(), "attachment-index.json");
        var index = File.Exists(path) ? Json.Read<Index>(File.ReadAllBytes(path)) : new(new(), []);
        if(index.Files.Any(p => !Json.IsHash(p.Key) || Path.GetFileName(p.Value) != p.Value || p.Value is "." or ".." || p.Value.IndexOfAny(['/', '\\', ':']) >= 0)) throw new InvalidDataException("附件索引无效。");
        names = index.Files; invalid.Clear(); foreach(var key in index.Invalid) { ValidateKey(key); invalid.Add(key); }
        if (database?.Invoke().ReadMetadata(InvalidMetadata) is { } committed)
            foreach (var key in Json.Read<string[]>(committed)) { ValidateKey(key); invalid.Add(key); }
        seen.Clear();foreach(var key in index.Seen??[]){ValidateKey(key);seen.Add(key);}
        loadedRoot = root();
    }
    private void PersistIndex()
    {
        var path = Path.Combine(root(), "attachment-index.json");
        var temp = path + ".tmp";
        using(var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        { System.Text.Json.JsonSerializer.Serialize(file, new Index(names, invalid.Order().ToArray(),seen.Order().ToArray()), Json.Options); file.Flush(true); }
        File.Move(temp,path,true);
    }
    public string PathFor(string hash)
    {
        if (!Json.IsHash(hash)) throw new InvalidDataException("附件标识无效。");
        lock(gate)
        {
            LoadIndex();
            if(names.TryGetValue(hash,out var name)) return Path.Combine(DirectoryPath,name);
            var legacy = Path.Combine(DirectoryPath,hash);
            return Directory.Exists(legacy) ? Directory.EnumerateFiles(legacy).FirstOrDefault() ?? legacy : legacy;
        }
    }
    private string ReadyPath(string hash) => Path.Combine(TransferPath, hash + ".ready");
    private string PartialPath(string hash) { Directory.CreateDirectory(TransferPath); return Path.Combine(TransferPath,hash + ".part"); }
    internal static void ValidateKey(string key)
    { if (key is null || !(Json.IsHash(key) || key.Length == 97 && key[64] == ':' && Json.IsHash(key[..64]) && Guid.TryParseExact(key[65..],"N",out _))) throw new InvalidDataException("附件失效记录无效。"); }
    public string[] InvalidKeys { get { lock(gate) { LoadIndex(); return invalid.Order().ToArray(); } } }
    public bool IsInvalid(Attachment item) { lock(gate) { LoadIndex(); return invalid.Contains(item.Key); } }
    internal void RememberAvailable(IEnumerable<Attachment> items)
    {
        lock (gate)
        {
            LoadIndex(); bool changed = false;
            foreach (var item in items) if (Has(item)) changed |= seen.Add(item.Key);
            if (changed) PersistIndex();
        }
    }
    public void Invalidate(IEnumerable<string> keys)
    {
        var incoming=keys.ToArray(); foreach(var key in incoming) ValidateKey(key);
        bool changed=false;
        lock(gate)
        {
            LoadIndex(); var next = invalid.Union(incoming).ToArray(); changed = next.Length != invalid.Count;
            if (changed) CommitInvalidations(next, bytes =>
            {
                if (database is not null) database().WriteMetadata(InvalidMetadata, bytes);
            });
        }
        if(changed)Changed?.Invoke();
    }
    // The database commit and the in-memory invalidation set advance together. The JSON file
    // remains a compatibility mirror; a mirror failure cannot undo an acknowledged SQL commit.
    internal void CommitInvalidations(IEnumerable<string> keys, Action<byte[]> commit)
    {
        lock (gate)
        {
            LoadIndex(); var next = invalid.Union(keys).ToArray(); foreach (var key in next) ValidateKey(key);
            commit(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(next, Json.Options));
            var previous = invalid.ToArray(); invalid.UnionWith(next);
            try { PersistIndex(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (database is null) { invalid.Clear(); invalid.UnionWith(previous); throw; }
            }
        }
    }
    public void DetectMissing(IEnumerable<Attachment> items)
    {
        var missing = new List<string>(); bool changed=false;
        lock(gate)
        {
            LoadIndex();
            foreach(var item in items)
            {
                if(invalid.Contains(item.Key))continue;
                if(Has(item)) changed |= seen.Add(item.Key);
                // A damaged or temporarily inaccessible original is not a user deletion.
                else if(seen.Contains(item.Key) && IsDefinitelyMissing(item))missing.Add(item.Key);
            }
            if(changed)PersistIndex();
        }
        Invalidate(missing);
    }
    private void StoreOriginal(string source, Attachment item)
    {
        LoadIndex(); Directory.CreateDirectory(DirectoryPath);
        if(!names.TryGetValue(item.Hash,out var name))
        {
            name = string.Concat(item.Name.Select(c => "<>:\"/\\|?*".Contains(c) ? '_' : c)).TrimEnd('.', ' ');
            if(string.IsNullOrEmpty(name)) name="attachment";
            var stem=Path.GetFileNameWithoutExtension(name); var ext=Path.GetExtension(name);
            if (new[]{"CON","PRN","AUX","NUL","COM1","COM2","COM3","COM4","COM5","COM6","COM7","COM8","COM9","LPT1","LPT2","LPT3","LPT4","LPT5","LPT6","LPT7","LPT8","LPT9"}.Contains(stem.ToUpperInvariant())) { stem="_"+stem; name=stem+ext; }
            if(stem.Length>180) { stem=stem[..180]; name=stem+ext; }
            int suffix=1;
            while(File.Exists(Path.Combine(DirectoryPath,name)) || Directory.Exists(Path.Combine(DirectoryPath,name)) || names.Values.Contains(name,StringComparer.OrdinalIgnoreCase)) name=stem+" ("+(suffix++)+")"+ext;
            names[item.Hash]=name;
        }
        var target=Path.Combine(DirectoryPath,name);
        Directory.CreateDirectory(TransferPath);
        var ready=ReadyPath(item.Hash);
        if(!Path.GetFullPath(source).Equals(Path.GetFullPath(ready),StringComparison.OrdinalIgnoreCase))File.Move(source,ready,true);
        seen.Add(item.Key);PersistIndex();
        File.Move(ready,target,true);
    }
    public void MigrateOriginalNames(IEnumerable<Attachment> items)
    {
        lock(gate)
        {
            LoadIndex();
            foreach(var group in items.GroupBy(a=>a.Hash))
            {
                var item=group.First();
                var ready=ReadyPath(item.Hash);
                if(File.Exists(ready))
                {
                    using(var input=File.OpenRead(ready))
                        if(input.Length!=item.Size || Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant()!=item.Hash)throw new InvalidDataException("中断的附件提交校验失败，请保留资料。");
                    StoreOriginal(ready,item);
                }
                if(names.ContainsKey(item.Hash))continue;
                var legacy=Path.Combine(DirectoryPath,item.Hash);
                var original=Directory.Exists(legacy)?Directory.EnumerateFiles(legacy).FirstOrDefault():File.Exists(legacy)?legacy:File.Exists(legacy+".original")?legacy+".original":null;
                if(original is not null) StoreOriginal(original,item);
                else if(File.Exists(legacy+".seen") || Directory.Exists(legacy)) foreach(var lost in group) invalid.Add(lost.Key);
                if(Directory.Exists(legacy) && !Directory.EnumerateFileSystemEntries(legacy).Any()) Directory.Delete(legacy);
                if(File.Exists(legacy+".seen"))File.Delete(legacy+".seen");
                if(File.Exists(legacy+".part"))File.Move(legacy+".part",PartialPath(item.Hash),true);
            }
            if(names.Count>0 || invalid.Count>0)PersistIndex();
        }
    }
    public void Delete(string hash)
    {
        lock(gate)
        {
            LoadIndex(); var path=PathFor(hash); if(File.Exists(path))File.Delete(path);
            if (names.Remove(hash)) PersistIndex();
            verified.Remove(hash);
            var directory=Path.Combine(DirectoryPath,hash);
            if(Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())Directory.Delete(directory);
            var partial = Path.Combine(TransferPath, hash + ".part");
            if(File.Exists(partial))File.Delete(partial);
            if(File.Exists(ReadyPath(hash)))File.Delete(ReadyPath(hash));
            if(File.Exists(directory+".seen"))File.Delete(directory+".seen");
        }
    }
    public bool Has(Attachment item)
    {
        if(IsInvalid(item)) return false;
        try { var file=new FileInfo(PathFor(item.Hash)); return file.Exists && file.Length==item.Size; }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){return false;}
    }
    private bool IsDefinitelyMissing(Attachment item)
    {
        try { _ = File.GetAttributes(PathFor(item.Hash)); return false; }
        catch (FileNotFoundException) { return Directory.Exists(DirectoryPath); }
        catch (DirectoryNotFoundException) { return false; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
    private readonly Dictionary<string, (long Size, DateTime Modified)> verified = new();
    public void Verify(Attachment item)
    {
        lock (gate)
        {
            if (!Has(item)) throw new IOException("附件原件暂不可用，请稍后重试。");
            var info = new FileInfo(PathFor(item.Hash));
            if (verified.TryGetValue(item.Hash, out var stamp) && stamp == (info.Length, info.LastWriteTimeUtc)) return;
            using var input = File.OpenRead(info.FullName);
            if (Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant() != item.Hash)
                throw new InvalidDataException("附件校验失败：" + item.Name + "。请保留原件并从可靠副本恢复。");
            verified[item.Hash] = (info.Length, info.LastWriteTimeUtc);
        }
    }
    internal void ClearLocalFiles()
    {
        lock(gate)
        {
            // Only this profile's attachment areas. Never follow directory links.
            void ClearDirectory(string path)
            {
                if(!Directory.Exists(path))return;
                if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new IOException("附件目录是链接，请先移除链接后重试。");
                foreach(var entry in Directory.EnumerateFileSystemEntries(path))
                {
                    var attributes=File.GetAttributes(entry);
                    if((attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("附件目录包含链接，请先移除链接后重试。");
                    if((attributes&FileAttributes.Directory)!=0)ClearDirectory(entry);else File.Delete(entry);
                }
                Directory.Delete(path);
            }
            ClearDirectory(DirectoryPath);ClearDirectory(TransferPath);
            names.Clear();invalid.Clear();seen.Clear();loadedRoot=root();
            File.Delete(Path.Combine(root(),"attachment-index.json"));
            File.Delete(Path.Combine(root(),"attachment-index.json.tmp"));
        }
    }
    public string Availability(Attachment item) { lock(gate) return IsInvalid(item) ? "已失效 · 原件已被移除" : Has(item) ? "可用" : seen.Contains(item.Key) ? IsDefinitelyMissing(item) ? "已失效 · 原件已被移除" : "原件暂不可用" : "等待同步"; }
    public Attachment Add(Stream input, string name, string? kind = null)
    {
        Directory.CreateDirectory(TransferPath);
        var temp = Path.Combine(TransferPath, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long size = 0;
            using (var output = File.Create(temp))
            {
                var buffer = new byte[128 * 1024]; int count;
                while ((count = input.Read(buffer, 0, buffer.Length)) > 0) { output.Write(buffer, 0, count); digest.AppendData(buffer, 0, count); size += count; }
            }
            var hash = Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant();
            var item = new Attachment(hash, name, size, kind ?? (new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp" }.Contains(Path.GetExtension(name).ToLowerInvariant()) ? "image" : "file"));
            item = item with { Instance = Guid.NewGuid().ToString("N") };
            item.Validate(); lock (gate)
            {
                bool usable = Has(item);
                if (usable) { try { Verify(item); } catch (InvalidDataException) { usable = false; } }
                if (!usable) { StoreOriginal(temp, item); verified.Remove(item.Hash); }
                if(seen.Add(item.Key))PersistIndex();
            }
            return item;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public Attachment AddPath(string path)
    {
        if (!Directory.Exists(path)) { using var file = File.OpenRead(path); return Add(file, Path.GetFileName(path)); }
        Directory.CreateDirectory(TransferPath);
        var temp = Path.Combine(TransferPath, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var folder = Path.GetFullPath(path);
            if (Path.GetFullPath(DirectoryPath).StartsWith(Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("不能发送包含当前 LanTodo 数据目录的文件夹，请选择其中的普通文件。");
            if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) throw new IOException("请选择普通文件夹。");
            // Store entries with no compression and preserve empty directories. Never follow directory links.
            using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
                Pack(folder, "", zip);
            using var file = File.OpenRead(temp);
            return Add(file, Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)) + ".zip", "folder");
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static void Pack(string folder, string prefix, ZipArchive zip)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("文件夹包含链接，请移除链接后发送。");
            var name = prefix + Path.GetFileName(entry);
            if ((attributes & FileAttributes.Directory) != 0) { zip.CreateEntry(name + "/", CompressionLevel.NoCompression); Pack(entry, name + "/", zip); }
            else zip.CreateEntryFromFile(entry, name, CompressionLevel.NoCompression);
        }
    }
    public byte[] ReadChunk(Attachment item, long offset)
    {
        item.Validate();
        if (!Has(item) || offset < 0 || offset > item.Size) throw new IOException("附件尚未就绪。");
        Verify(item);
        using var file = File.OpenRead(PathFor(item.Hash)); file.Position = offset;
        var bytes = new byte[(int)Math.Min(ChunkSize, item.Size - offset)]; file.ReadExactly(bytes); return bytes;
    }
    public long Received(Attachment item)
    {
        var path = PartialPath(item.Hash);
        return Has(item) ? item.Size : File.Exists(path) ? new FileInfo(path).Length : 0;
    }
    private readonly object gate = new();
    public void Receive(Attachment item, long offset, byte[] bytes)
    {
        item.Validate();
        lock (gate)
        {
            if (IsInvalid(item)) throw new IOException("附件已失效，不能由其他设备自动恢复。");
            if (Has(item)) { if(seen.Add(item.Key))PersistIndex(); return; }
            if (offset < 0 || offset > item.Size || bytes.Length > ChunkSize || bytes.Length > item.Size - offset || (bytes.Length == 0 && offset != item.Size)) throw new InvalidDataException("附件分块无效。");
            Directory.CreateDirectory(DirectoryPath);
            var path = PartialPath(item.Hash);
            using (var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                if (file.Length != offset) throw new IOException("附件进度变化，请重试。");
                file.Position = offset; file.Write(bytes); file.Flush(true);
            }
            if (offset + bytes.Length == item.Size)
            {
                using var file = File.OpenRead(path);
                var hash = Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant(); file.Dispose();
                if (hash != item.Hash) { File.Delete(path); throw new InvalidDataException("附件校验失败，请重新同步。"); }
                StoreOriginal(path, item);
            }
        }
    }
}
