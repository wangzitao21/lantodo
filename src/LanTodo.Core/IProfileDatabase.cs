namespace LanTodo.Core;

// Local persistence contract v1. SQL, schema upgrades and file lifecycle stay behind this boundary.
public interface IProfileDatabase : IDisposable
{
    string FilePath { get; }
    Revision[] ReadRevisions();
    void Append(IReadOnlyList<Revision> revisions);
    byte[]? ReadMetadata(string key);
    void WriteMetadata(string key, byte[] value);
}

// Revision/backup format v1 is independent of the local SQLite schema version.
public interface ITodoStore
{
    event Action? Changed;
    TodoView[] List();
    Revision[] Export();
    Revision[] History(string todoId);
    Revision Save(string actor, string deviceName, TodoData data, string? todoId = null, string[]? expectedHeads = null);
    int Import(IEnumerable<Revision> incoming);
    (int Deleted, int Skipped) DeleteCompleted(string actor, string deviceName, IReadOnlyList<TodoView> confirmed);
    void Backup(string path);
    int Restore(string path);
}
