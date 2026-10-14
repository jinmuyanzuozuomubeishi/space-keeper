using System.Text;
using System.Text.Json;

namespace SpaceKeeper.Core;

public sealed class LocalStore : IOperationJournal
{
    public string DirectoryPath { get; }
    private string LogPath => System.IO.Path.Combine(DirectoryPath, "operations.jsonl");
    public LocalStore(string directory) { DirectoryPath = directory; Directory.CreateDirectory(directory); }
    public void Append(OperationEntry entry)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry) + "\n");
        using var stream = new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        stream.Write(bytes); stream.Flush(true);
    }
    public IReadOnlyList<OperationEntry> ReadHistory()
    {
        if (!File.Exists(LogPath)) return [];
        var entries = new List<OperationEntry>();
        foreach (var line in File.ReadLines(LogPath))
        {
            try { var entry = JsonSerializer.Deserialize<OperationEntry>(line); if (entry != null) entries.Add(entry); }
            catch (JsonException) { entries.Add(new(DateTimeOffset.Now, "", "", 0, "记录不完整", "上次操作可能中断，请检查回收站与原文件")); }
        }
        return entries.AsEnumerable().Reverse().Take(1000).ToList();
    }
    public IReadOnlyList<string> LoadProtection()
    {
        var path = System.IO.Path.Combine(DirectoryPath, "settings.json");
        return File.Exists(path) ? JsonSerializer.Deserialize<string[]>(File.ReadAllText(path)) ?? [] : [];
    }
    public void SaveProtection(IEnumerable<string> folders)
    {
        var path = System.IO.Path.Combine(DirectoryPath, "settings.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(folders.ToArray())); File.Move(path + ".tmp", path, true);
    }
}
