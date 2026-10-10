using Keepmark.Core.I18n;

namespace Keepmark.Core.Models;

/// <summary>
/// Display-only names for the top-level xBrowserSync containers. The synced
/// titles ("[xbs] Menu" …) are the wire format every client looks containers
/// up by: they are never rewritten, only shown with a localized name.
/// </summary>
public static class ContainerNames
{
    public const string Menu = "[xbs] Menu";
    public const string Other = "[xbs] Other";
    public const string Toolbar = "[xbs] Toolbar";

    /// <summary>Localized name for a top-level container (no parent);
    /// any other title, including a nested folder named "[xbs] Menu", is returned unchanged.</summary>
    public static string? Display(string? title, bool isTopLevel)
    {
        if (!isTopLevel)
        {
            return title;
        }

        return title switch
        {
            Menu => Loc.Get(Str.ContainerMenu),
            Other => Loc.Get(Str.ContainerOther),
            Toolbar => Loc.Get(Str.ContainerToolbar),
            _ => title,
        };
    }
}
