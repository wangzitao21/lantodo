namespace LanTodo.Core;

public static class MessageStyle
{
    public static readonly (string? Key, string Name, string Hex)[] Colors =
    [ (null, "默认", "#FFFFFF"), ("sand", "浅黄", "#FFF5D9"), ("mint", "薄荷", "#E5F4EB"),
      ("sky", "浅蓝", "#E7F1FC"), ("rose", "浅粉", "#FCEAEC"), ("lavender", "浅紫", "#F0EAFA") ];
    public static string Background(string? key) => Colors.FirstOrDefault(c => c.Key == key).Hex ?? "#FFFFFF";
}
