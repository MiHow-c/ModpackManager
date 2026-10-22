using System.Text.Json;
using ModpackManager.Core.Ops;
using ModpackManager.Core.Remote;

namespace ModpackManager.Tests;

public class PermissionTests
{
    public static TheoryData<Operation, bool, Permission> Cases => new()
    {
        { new AddMod("P", "V"), false, Permission.ModsAdd },
        { new UpdateMod("mods/a.jar", "V"), true, Permission.ModsUpdate },
        { new DeleteFile("mods/a.jar"), true, Permission.ModsRemove },
        { new PutFile("mods/new.jar", []), false, Permission.ModsAdd },
        { new PutFile("mods/a.jar", []), true, Permission.ModsUpdate },
        { new PutFile("config/a.toml", []), true, Permission.ConfigsEdit },
        { new DeleteFile("defaultconfigs/a.toml"), true, Permission.ConfigsEdit },
        { new PutFile("kubejs/server_scripts/a.js", []), false, Permission.FilesManage },
        { new DeleteFile("resourcepacks/a.zip"), true, Permission.FilesManage },
        { new SetSettings("1.21.1", Core.Loader.NeoForge, null), false, Permission.PackSettings },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Required_permission_follows_the_target_path(Operation op, bool exists, Permission expected) =>
        Assert.Equal(expected, Permissions.Required(op, exists));

    [Fact]
    public void Owner_has_every_permission_and_others_only_granted_ones()
    {
        var moderator = new Principal("u", "m", false, new HashSet<Permission> { Permission.FilesManage });
        Assert.All(Enum.GetValues<Permission>(), p => Assert.True(Principal.Owner.Has(p)));
        Assert.True(moderator.Has(Permission.FilesManage));
        Assert.False(moderator.Has(Permission.ModsRemove));
    }

    [Fact]
    public void Permissions_and_operations_use_the_wire_names()
    {
        Assert.Equal("[\"mods.add\",\"pack.settings\"]", JsonSerializer.Serialize(new[] { Permission.ModsAdd, Permission.PackSettings }, ApiJson.Options));
        var json = JsonSerializer.Serialize<Operation>(new DeleteFile("mods/a.jar"), ApiJson.Options);
        Assert.Equal("{\"type\":\"delete\",\"path\":\"mods/a.jar\"}", json);
        Assert.Equal(new DeleteFile("mods/a.jar"), JsonSerializer.Deserialize<Operation>(json, ApiJson.Options));
    }

    [Fact]
    public void Invite_code_round_trips_and_rejects_garbage()
    {
        var invite = new InviteCode("eu-west.tunnel.example:41234", new string('a', 64), "tok_EN");
        Assert.Equal(invite, InviteCode.Parse(invite.ToString()));
        Assert.Null(InviteCode.Parse("mpm1.x.y"));
        Assert.Null(InviteCode.Parse("mpm2" + invite.ToString()[4..]));
        Assert.Null(InviteCode.Parse(invite.ToString().Replace(".tok_EN", "")));
        Assert.Null(InviteCode.Parse(new InviteCode("no-port", new string('a', 64), "t").ToString()));
    }
}
