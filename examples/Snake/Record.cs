using Snake.Macros;
using static Snake.Macros.Persistence;

namespace Snake;

/// <summary>跨局保存的记录。要存什么，就在字段上标 [@Persist]；读写代码和格式指纹都是生成的。</summary>
public sealed class Record
{
    [@Persist] public int Best;
    [@Persist] public int GamesPlayed;
    [@Persist] public int TotalFood;

    public void Save(BinaryWriter writer) { @WRITE_PERSISTED(writer); }
    public void Load(BinaryReader reader) { @READ_PERSISTED(reader); }

    public static Record LoadFrom(string path, Action<string> report)
    {
        var record = new Record();
        if (!File.Exists(path)) return record;
        try
        {
            using var reader = new BinaryReader(File.OpenRead(path));
            record.Load(reader);
        }
        catch (InvalidDataException e)
        {
            report($"ignoring {Path.GetFileName(path)}: {e.Message}");
            record = new Record();
        }
        return record;
    }

    public void SaveTo(string path)
    {
        using var writer = new BinaryWriter(File.Create(path));
        Save(writer);
    }
}
