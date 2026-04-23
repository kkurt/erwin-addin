using EliteSoft.Erwin.AlterDdl.Core.Models;

namespace EliteSoft.Erwin.AlterDdl.Core.Emitting;

/// <summary>
/// Picks a <see cref="ISqlEmitter"/> based on the model's
/// <see cref="ModelMetadata.TargetServer"/>. Phase 3.C ships MSSQL;
/// 3.E adds Oracle and Db2. External projects can register their own.
/// </summary>
public sealed class SqlEmitterRegistry
{
    private readonly Dictionary<string, ISqlEmitter> _byKey =
        new(StringComparer.OrdinalIgnoreCase);

    public SqlEmitterRegistry Register(ISqlEmitter emitter, params string[] aliases)
    {
        ArgumentNullException.ThrowIfNull(emitter);
        _byKey[emitter.Dialect] = emitter;
        foreach (var alias in aliases) _byKey[alias] = emitter;
        return this;
    }

    /// <summary>
    /// Choose an emitter for the given target server. Matching is
    /// case-insensitive and aliases are supported ("SQL Server" -> MSSQL).
    /// </summary>
    public ISqlEmitter Resolve(string targetServer)
    {
        if (_byKey.TryGetValue(targetServer, out var direct)) return direct;
        // normalize common variants
        var normalized = targetServer.Replace(" ", "").Replace("_", "");
        foreach (var kv in _byKey)
            if (string.Equals(kv.Key.Replace(" ", "").Replace("_", ""), normalized, StringComparison.OrdinalIgnoreCase))
                return kv.Value;
        throw new NotSupportedException($"no SQL emitter registered for target server '{targetServer}'");
    }

    public bool TryResolve(string targetServer, out ISqlEmitter emitter)
    {
        try { emitter = Resolve(targetServer); return true; }
        catch (NotSupportedException) { emitter = null!; return false; }
    }
}
