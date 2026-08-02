namespace JeekRemoteManager.Models;

/// <summary>
/// Four reusable login-command fragments shared by SSH connections that authenticate
/// through the same bastion. Fragment ids are permanently fixed to 1 through 4.
/// The template contains no endpoint credentials; its opaque id is derived from the
/// same endpoint/credential identity used by the in-process bastion session pool.
/// </summary>
public sealed class BastionLoginProfile
{
    public const int SegmentCount = 4;

    public string Id { get; set; } = "";

    /// <summary>Safe display label such as user@host:22.</summary>
    public string EndpointLabel { get; set; } = "";

    /// <summary>
    /// Commands expanded by #template 1 through #template 4. The array index is
    /// zero-based only in JSON/C#; users always see stable one-based ids.
    /// </summary>
    public string[] Segments { get; set; } = new string[SegmentCount];

    public string GetSegment(int oneBasedId) =>
        Segments is not null
        && oneBasedId is >= 1 and <= SegmentCount
        && Segments.Length >= oneBasedId
            ? Segments[oneBasedId - 1] ?? ""
            : "";

    public void NormalizeSegments()
    {
        Segments ??= [];
        if (Segments.Length == SegmentCount)
            return;

        var normalized = new string[SegmentCount];
        Array.Copy(Segments, normalized, Math.Min(Segments.Length, normalized.Length));
        Segments = normalized;
    }
}

/// <summary>
/// A one-click starting point matching the established interactive bastion flow:
/// enter 2FA, type 0 for all assets, select the current connection and account,
/// elevate with sudo, and return to the menu when switching a reused transport.
/// </summary>
public static class BastionLoginTemplatePreset
{
    public const string ConnectionLoginCommands =
        "#template 1\n"
        + "#select {{name}}\n"
        + "#template 2";

    public static string GetSegment(int oneBasedId) =>
        oneBasedId switch
        {
            1 => "#input\n#reuse-enter\n#pagekey Ctrl-F\n0",
            2 => "2\n#duplicate\nsudo -i\n#reuse-leave\nexit\n#key Enter",
            3 => "",
            4 => "",
            _ => throw new ArgumentOutOfRangeException(nameof(oneBasedId)),
        };

    public static string UseConnectionCommandsWhenEmpty(string existingCommands) =>
        string.IsNullOrWhiteSpace(existingCommands)
            ? ConnectionLoginCommands.ReplaceLineEndings(Environment.NewLine)
            : existingCommands;
}
