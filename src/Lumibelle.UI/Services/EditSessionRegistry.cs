namespace lumibelle;
public sealed class EditSessionRegistry
{
    private readonly Dictionary<Guid, Func<Task<bool>>> sessions = [];
    public IDisposable Register(Func<Task<bool>> save)
    { var id = Guid.NewGuid(); sessions[id] = save; return new Registration(() => sessions.Remove(id)); }
    public async Task<bool> SaveAllAsync()
    { foreach (var save in sessions.Values.ToArray()) if (!await save()) return false; return true; }
    private sealed class Registration(Action remove) : IDisposable { public void Dispose() => remove(); }
}
