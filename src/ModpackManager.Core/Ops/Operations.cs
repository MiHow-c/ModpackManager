using System.Text.Json.Serialization;

namespace ModpackManager.Core.Ops;

/// <summary>A single change to a pack. Self-contained (ids, not resolved files) so a moderator can send it to the host as-is.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(AddMod), "addMod")]
[JsonDerivedType(typeof(UpdateMod), "updateMod")]
[JsonDerivedType(typeof(PutFile), "putFile")]
[JsonDerivedType(typeof(DeleteFile), "delete")]
[JsonDerivedType(typeof(SetSettings), "setSettings")]
public abstract record Operation;

/// <summary>Adds a Modrinth mod version to mods/.</summary>
public sealed record AddMod(string ProjectId, string VersionId) : Operation;

/// <summary>Switches the Modrinth mod at Path to another version of the same project.</summary>
public sealed record UpdateMod(string Path, string VersionId) : Operation;

/// <summary>Creates or overwrites a file (configs, kubejs, resource packs...).</summary>
public sealed record PutFile(string Path, byte[] Content) : Operation;

/// <summary>Removes a file; for packwiz mods this removes the .pw.toml.</summary>
public sealed record DeleteFile(string Path) : Operation;

public sealed record SetSettings(string McVersion, Loader Loader, string? LoaderVersion) : Operation;

public enum OpError
{
    InvalidPath, InvalidValue, NotFound, SyntaxError, AlreadyInstalled, WrongProject, NotAMod, NoFile, HashMismatch,
    Unauthorized, Forbidden, StaleRevision,
}

public sealed class OperationException(OpError error, string? detail = null) : Exception(detail is null ? error.ToString() : $"{error}: {detail}")
{
    public OpError Error => error;

    public string? Detail => detail;
}
