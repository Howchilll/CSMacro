namespace Demo.App;

/// <summary>
/// 普通的业务代码：一个按类型登记字段的存档容器。
/// 谁要存档，由字段上的 [@Save] 决定；登记代码由宏在编译期生成。
/// </summary>
public static class SaveContainer
{
    private sealed record Entry(Type Owner, string Key, Func<object, object?> Get, Action<object, object?> Set);

    private static readonly List<Entry> s_entries = new();

    public static void Register<T>(string key, Func<T, object?> get, Action<T, object?> set) =>
        s_entries.Add(new Entry(typeof(T), key, o => get((T)o), (o, v) => set((T)o, v)));

    public static Dictionary<string, object?> Save(object owner) =>
        s_entries.Where(e => e.Owner == owner.GetType()).ToDictionary(e => e.Key, e => e.Get(owner));

    public static void Load(object owner, Dictionary<string, object?> data)
    {
        foreach (var e in s_entries.Where(e => e.Owner == owner.GetType()))
            if (data.TryGetValue(e.Key, out var value)) e.Set(owner, value);
    }
}
