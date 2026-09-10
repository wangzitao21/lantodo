namespace LanTodo.Core;

public static class AtomicFile
{
    // Temporary files are never treated as committed records. Commit is an atomic same-directory rename.
    public static void Write(string path, byte[] bytes, bool replace = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            File.Move(temp, path, replace);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
