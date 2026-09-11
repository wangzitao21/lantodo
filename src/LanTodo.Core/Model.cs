using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace LanTodo.Core;

public sealed record TodoData(string Title, string Notes = "", string? Date = null,
    string? Time = null, bool Completed = false, bool Deleted = false,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] Attachment[]? Attachments = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] bool Purged = false)
{
    public void Validate()
    {
        if (Purged && !Deleted) throw new InvalidDataException("彻底清除的记录必须处于删除状态。");
        if (Attachments is { Length: > 32 }) throw new InvalidDataException("每条消息最多添加 32 个附件。");
        if (Attachments is not null) foreach (var attachment in Attachments) attachment.Validate();
        if (string.IsNullOrWhiteSpace(Title) || Title.Length > 500 || Notes is null || Notes.Length > 32000)
            throw new InvalidDataException("标题不能为空且不能超过 500 字；备注不能超过 32000 字。");
        if (Date is not null && !DateOnly.TryParseExact(Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            throw new InvalidDataException("日期格式应为 yyyy-MM-dd。");
        if (Time is not null && (Date is null || !TimeOnly.TryParseExact(Time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)))
            throw new InvalidDataException("请先填写日期；时间格式应为 HH:mm。");
    }
}

public sealed record RevisionBody(int Schema, string TodoId, string Actor, string DeviceName,
    string Nonce, string CreatedUtc, string[] Parents, TodoData Data);
public sealed record Revision(string Id, RevisionBody Body)
{
    public static Revision Create(RevisionBody body) => new(Hash(body), body);
    public static string Hash(RevisionBody body) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(body, Json.Options))).ToLowerInvariant();
    public void Validate()
    {
        if (Body is null || Body.Data is null || Body.Parents is null || Body.Schema != 1 || !Json.IsHash(Id) || Hash(Body) != Id ||
            !Guid.TryParseExact(Body.TodoId, "N", out _) || !Guid.TryParseExact(Body.Nonce, "N", out _) || !Json.IsHash(Body.Actor) ||
            string.IsNullOrWhiteSpace(Body.DeviceName) || Body.DeviceName.Length > 100 || Body.Parents.Length > 256 ||
            Body.Parents.Any(p => !Json.IsHash(p)) || Body.Parents.Distinct().Count() != Body.Parents.Length ||
            !Body.Parents.SequenceEqual(Body.Parents.OrderBy(p => p, StringComparer.Ordinal)) ||
            !DateTimeOffset.TryParse(Body.CreatedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _))
            throw new InvalidDataException("版本数据无效或校验失败。原数据保持不变。");
        Body.Data.Validate();
    }
}

public sealed record TodoView(string Id, Revision[] Heads)
{
    public bool Conflict => Heads.Length > 1;
    public TodoData Data => Heads[0].Body.Data;
    public string[] VersionIds => Heads.Select(h => h.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray();
}

public sealed class StaleEditException : Exception
{
    public StaleEditException() : base("编辑期间收到新版本。你的输入仍保留，请重新查看最新版本后再保存。") { }
}

public static class Json
{
    public const int MaxRevisionBytes = 512 * 1024;
    public static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, MaxDepth = 32 };
    public static bool IsHash(string? s) => s is { Length: 64 } && s.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    public static T Read<T>(byte[] data) => JsonSerializer.Deserialize<T>(data, Options) ?? throw new InvalidDataException("空数据。");
}
