namespace KeepmarkMobile.Services;

// Local-only pinned bookmark IDs (NodeIds). Not synced — personal shelf.

public sealed class PinnedStore
{
    private const string Key = "pinned_ids";

    public HashSet<int> Load()
    {
        var raw = Preferences.Get(Key, string.Empty);
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => int.TryParse(s, out int n) ? n : -1)
            .Where(n => n >= 0).ToHashSet();
    }

    public bool Toggle(int nodeId)
    {
        var set = Load();
        bool added = set.Add(nodeId);
        if (!added)
        {
            set.Remove(nodeId);
        }

        Preferences.Set(Key, string.Join(",", set));
        return added;
    }
}
