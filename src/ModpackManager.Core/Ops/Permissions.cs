using System.Text.Json.Serialization;

namespace ModpackManager.Core.Ops;

[JsonConverter(typeof(JsonStringEnumConverter<Permission>))]
public enum Permission
{
    [JsonStringEnumMemberName("mods.add")] ModsAdd,
    [JsonStringEnumMemberName("mods.remove")] ModsRemove,
    [JsonStringEnumMemberName("mods.update")] ModsUpdate,
    [JsonStringEnumMemberName("configs.edit")] ConfigsEdit,
    [JsonStringEnumMemberName("files.manage")] FilesManage,
    [JsonStringEnumMemberName("pack.settings")] PackSettings,
}

/// <summary>Who runs an operation. The owner (Creator) acts in-process on the host; everyone else holds a token.</summary>
public sealed record Principal(string UserId, string Name, bool IsOwner, IReadOnlySet<Permission> Permissions)
{
    public static readonly Principal Owner = new("owner", "owner", true, Enum.GetValues<Permission>().ToHashSet());

    public bool Has(Permission permission) => IsOwner || Permissions.Contains(permission);
}

public static class Permissions
{
    /// <summary>
    /// The permission an operation needs, from the category of its target path rather than the operation type,
    /// so e.g. deleting mods/x.jar always needs mods.remove, whatever operation is used.
    /// </summary>
    public static Permission Required(Operation op, bool targetExists) => op switch
    {
        AddMod => Permission.ModsAdd,
        UpdateMod => Permission.ModsUpdate,
        SetSettings => Permission.PackSettings,
        PutFile(var path, _) => CategoryOf(path) switch
        {
            PathCategory.Mods => targetExists ? Permission.ModsUpdate : Permission.ModsAdd,
            PathCategory.Configs => Permission.ConfigsEdit,
            _ => Permission.FilesManage,
        },
        DeleteFile(var path) => CategoryOf(path) switch
        {
            PathCategory.Mods => Permission.ModsRemove,
            PathCategory.Configs => Permission.ConfigsEdit,
            _ => Permission.FilesManage,
        },
        _ => throw new ArgumentOutOfRangeException(nameof(op), op, null),
    };

    enum PathCategory { Mods, Configs, Files }

    static PathCategory CategoryOf(string path) => path.StartsWith("mods/", StringComparison.Ordinal) ? PathCategory.Mods
        : path.StartsWith("config/", StringComparison.Ordinal) || path.StartsWith("defaultconfigs/", StringComparison.Ordinal) ? PathCategory.Configs
        : PathCategory.Files;
}
