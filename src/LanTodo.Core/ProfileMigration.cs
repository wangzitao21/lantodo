namespace LanTodo.Core;

public static class ProfileMigration
{
    // Pause networking/editing first. Persist the locator in activateLocation and restore it on failure.
    public static void MoveTo(TodoStore source, string destination, Action? activateLocation = null)
    {
        var origin = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source.Root));
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        if (target.Equals(origin, StringComparison.OrdinalIgnoreCase)
            || target.StartsWith(origin + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || origin.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("请选择与原数据目录分开的文件夹。");
        for (string? current = target; current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("请选择普通文件夹，不使用目录链接。");
        if (File.Exists(Path.Combine(target, SqliteProfile.FileName)) || LegacyProfile.Exists(target))
            throw new IOException("目标文件夹已有LanTodo数据，请选择其他位置，避免覆盖。");
        Directory.CreateDirectory(target);
        if (source.Database is not SqliteProfile database) throw new NotSupportedException("当前存储不支持位置迁移。");
        database.MoveTo(target, activateLocation ?? (() => { }));
    }
}
