using System.Text.Json;
using LanTodo.Core;

namespace LanTodo.Windows;

internal static class DataLocation
{
    public static string PortableRoot => AppContext.BaseDirectory;
    public static string ConfigPath => Path.Combine(PortableRoot, "LanTodo.location");
    public static string Read(string? configFile = null, string? defaultPath = null)
    {
        configFile ??= ConfigPath;
        if (!File.Exists(configFile)) return defaultPath ?? PortableRoot;
        var path = JsonSerializer.Deserialize<string>(File.ReadAllText(configFile));
        if (string.IsNullOrWhiteSpace(path)) throw new IOException("数据位置设置损坏：" + configFile);
        path = Path.GetFullPath(path, Path.GetDirectoryName(configFile)!);
        if (!File.Exists(Path.Combine(path, SqliteProfile.FileName)))
            throw new IOException("已选择的数据目录不可用。请连接对应磁盘并检查：" + path + "\n位置设置文件：" + configFile);
        return path;
    }
    public static void Save(string path, string? configFile = null)
    {
        configFile ??= ConfigPath;
        var full = Path.GetFullPath(path);
        if (Path.TrimEndingDirectorySeparator(full).Equals(Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(configFile)!), StringComparison.OrdinalIgnoreCase))
        { if (File.Exists(configFile)) File.Delete(configFile); return; }
        var relative = Path.GetRelativePath(Path.GetDirectoryName(configFile)!, full);
        AtomicFile.Write(configFile, JsonSerializer.SerializeToUtf8Bytes(relative), true);
    }
    public static string? PreviousProfile()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var config = Path.Combine(local, "LanTodo-location.json");
        var path = File.Exists(config) ? JsonSerializer.Deserialize<string>(File.ReadAllText(config)) : Path.Combine(local, "LanTodo");
        if (File.Exists(config) && (string.IsNullOrWhiteSpace(path) || !LegacyProfile.Exists(path)))
            throw new IOException("旧版自定义数据目录不可用，请连接原磁盘后重试：" + path);
        return path is not null && LegacyProfile.Exists(path) ? path : null;
    }
}
