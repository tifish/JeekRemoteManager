using System.Text.RegularExpressions;

namespace JeekRemoteManager.Services;

/// <summary>
/// Local safety net for the AI panel's auto-run loop: flags commands whose typical shapes
/// can mass-delete or irreversibly overwrite data, so the UI asks the user to confirm first.
/// This is a heuristic blacklist, not a sandbox — the model is additionally instructed to
/// use the terminal.run-danger tool, and either signal triggers confirmation.
/// </summary>
public static class DangerousCommandDetector
{
    private static readonly Regex[] Patterns =
    [
        // rm with a recursive flag (also catches --recursive / --force long forms)
        new(@"\brm\b[^\n;|&]*\s-{1,2}\w*[rR]", RegexOptions.Compiled),
        // rm expanding a wildcard
        new(@"\brm\b[^\n;|&]*\*", RegexOptions.Compiled),
        new(@"\bfind\b[^\n]*\s-delete\b", RegexOptions.Compiled),
        new(@"\brsync\b[^\n]*\s--delete", RegexOptions.Compiled),
        // writing raw bytes over a block device
        new(@"\bdd\b[^\n]*\bof=/dev/", RegexOptions.Compiled),
        new(@">\s*/dev/(sd|nvme|vd|hd|mmcblk)", RegexOptions.Compiled),
        new(@"\b(mkfs|mkswap)(\.\w+)?\b", RegexOptions.Compiled),
        new(@"\b(wipefs|blkdiscard|shred)\b", RegexOptions.Compiled),
        new(@"\b(sgdisk|sfdisk)\b", RegexOptions.Compiled),
        new(@"\bparted\b[^\n]*\b(rm|mklabel|mkpart)\b", RegexOptions.Compiled),
        new(@"\btruncate\b[^\n]*\s-s\b", RegexOptions.Compiled),
        new(@"\bmv\b[^\n]*\s/dev/null", RegexOptions.Compiled),
        // SQL mass deletion
        new(@"\bDROP\s+(DATABASE|TABLE|SCHEMA)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"\bTRUNCATE\s+TABLE\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"\bDELETE\s+FROM\b(?![^;\n]*\bWHERE\b)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        // git history / worktree destruction
        new(@"\bgit\s+reset\s+--hard\b", RegexOptions.Compiled),
        new(@"\bgit\s+clean\b[^\n;|&]*\s-\w*[fdxX]", RegexOptions.Compiled),
        new(@"\bgit\s+push\b[^\n;|&]*(\s--force\b|\s-\w*f)", RegexOptions.Compiled),
        new(@"\bgit\s+branch\b[^\n;|&]*\s-\w*D", RegexOptions.Compiled),
        // volume / container / account cleanup that takes data with it
        new(@"\b(lvremove|vgremove|pvremove)\b", RegexOptions.Compiled),
        new(@"\bdocker\s+(system|volume|image|container|network)\s+prune\b", RegexOptions.Compiled),
        new(@"\bdocker\s+volume\s+rm\b", RegexOptions.Compiled),
        new(@"\bcrontab\s+-\w*r", RegexOptions.Compiled),
        new(@"\buserdel\b[^\n;|&]*\s-\w*r", RegexOptions.Compiled),
    ];

    public static bool IsDangerous(string command)
    {
        foreach (var pattern in Patterns)
        {
            if (pattern.IsMatch(command))
                return true;
        }

        return false;
    }
}
