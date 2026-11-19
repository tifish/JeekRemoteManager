using CommunityToolkit.Mvvm.ComponentModel;

namespace JeekRemoteManager.Models;

/// <summary>The name and stable ID shown in the SSH connection editor.</summary>
public sealed partial class SshMfaProfileChoice(Guid? id, string name) : ObservableObject
{
    public Guid? Id { get; } = id;

    [ObservableProperty]
    private string _name = name;
}
