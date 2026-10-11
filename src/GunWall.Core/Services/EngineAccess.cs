namespace GunWall.Services;

/// <summary>What an <see cref="IEngine"/> member does to protection (stage 4a).
/// The background service decides from this whether a call from the window may
/// run as it is, needs an unlock (Protect settings, stage 4c), or may not cross
/// the pipe at all.</summary>
public enum Access
{
    /// <summary>Reads state. Always allowed.</summary>
    Read,
    /// <summary>Changes something that is not protection: appearance, notes,
    /// logs, update checks, backups. Always allowed.</summary>
    Preference,
    /// <summary>Makes protection stricter (block, lockdown, protection back on).
    /// Always allowed: stricter cannot be abused.</summary>
    Tighten,
    /// <summary>Stricter when its last bool argument is true (protection ON,
    /// lockdown ON, a scope blocked), looser when false.</summary>
    TightenIfTrue,
    /// <summary>Looser when its last bool argument is true (bypass blocklists,
    /// mute an app, allow by default), stricter when false.</summary>
    TightenIfFalse,
    /// <summary>Makes protection looser (allow, remove, import, pause, reset).
    /// Needs an unlock while Protect settings is on.</summary>
    Loosen,
    /// <summary>Never sent to the service: an object, an event or start-up
    /// work the window does for itself.</summary>
    Local,
}

[AttributeUsage(AttributeTargets.Method | AttributeTargets.Property | AttributeTargets.Event)]
public sealed class EngineAccessAttribute : Attribute
{
    public EngineAccessAttribute(Access access) => Access = access;
    public Access Access { get; }

    /// <summary>The effective access of one call: TightenIfTrue / TightenIfFalse
    /// resolved against the last bool argument. Unknown is treated as Loosen.</summary>
    public static Access Resolve(Access declared, object?[]? args)
    {
        if (declared is not (Access.TightenIfTrue or Access.TightenIfFalse)) return declared;
        bool? flag = null;
        if (args != null)
            for (int i = args.Length - 1; i >= 0; i--)
                if (args[i] is bool b) { flag = b; break; }
        if (flag is null) return Access.Loosen;
        return (declared == Access.TightenIfTrue) == flag.Value ? Access.Tighten : Access.Loosen;
    }
}
