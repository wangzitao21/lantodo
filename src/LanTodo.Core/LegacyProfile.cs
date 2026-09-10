namespace LanTodo.Core;

public static class LegacyProfile
{
    private static readonly string[] Metadata = ["identity.pfx", "trusted.json", "name.json", "sync-settings.json"];
    public static bool Exists(string root) => Directory.Exists(Path.Combine(root, "revisions")) || Metadata.Any(n => File.Exists(Path.Combine(root, n)));
    public static void Upgrade(string root) => Upgrade(root, root);
    public static void Upgrade(string source, string destination)
    {
        if (File.Exists(Path.Combine(destination, SqliteProfile.FileName)) || !Exists(source)) return;
        Directory.CreateDirectory(destination);
        var temporary = Path.Combine(destination, ".import-" + Guid.NewGuid().ToString("N"));
        var imported = new List<string>();
        using var oldLock = new FileStream(Path.Combine(source, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            using (var store = new TodoStore(temporary))
            {
                var folder = Path.Combine(source, "revisions");
                var records = new List<Revision>();
                if (Directory.Exists(folder)) foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
                {
                    if (new FileInfo(file).Length > 256 * 1024) throw new InvalidDataException("旧版记录异常过大。");
                    var record = Json.Read<Revision>(File.ReadAllBytes(file)); record.Validate();
                    if (Path.GetFileNameWithoutExtension(file) != record.Id) throw new InvalidDataException("旧版记录文件名校验失败。");
                    records.Add(record); imported.Add(file);
                }
                store.Import(records);
                foreach (var name in Metadata)
                {
                    var file = Path.Combine(source, name);
                    if (File.Exists(file)) { store.Database.WriteMetadata(name, File.ReadAllBytes(file)); imported.Add(file); }
                }
                if (store.Database.ReadMetadata("identity.pfx") is not null || store.Database.ReadMetadata("trusted.json") is not null)
                { using var identity = new DeviceIdentity(store.Database, "LanTodo"); }
                if (store.Database.ReadMetadata("sync-settings.json") is byte[] settings) _ = Json.Read<int>(settings);
                var backups = Path.Combine(source, "backups");
                if (Directory.Exists(backups)) foreach (var file in Directory.EnumerateFiles(backups, "*.lantodo.zip"))
                { store.Database.WriteMetadata("legacy-backup/" + Path.GetFileName(file), File.ReadAllBytes(file)); imported.Add(file); }
            }
            using (var verified = new TodoStore(temporary)) _ = verified.Export();
            File.Move(Path.Combine(temporary, SqliteProfile.FileName), Path.Combine(destination, SqliteProfile.FileName));
            foreach (var file in imported) File.Delete(file);
            foreach (var name in new[] { "revisions", "backups" })
            {
                var folder = Path.Combine(source, name);
                if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
            }
        }
        finally
        {
            var tempFile = Path.Combine(temporary, SqliteProfile.FileName);
            if (File.Exists(tempFile)) File.Delete(tempFile);
            if (Directory.Exists(temporary) && !Directory.EnumerateFileSystemEntries(temporary).Any()) Directory.Delete(temporary);
            oldLock.Dispose(); File.Delete(Path.Combine(source, "instance.lock"));
        }
    }
}
