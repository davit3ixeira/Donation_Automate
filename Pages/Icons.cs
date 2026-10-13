using Microsoft.AspNetCore.Html;

namespace DonationAutomate.Pages;

/// <summary>
/// Small line icons shared by the pages (imported with <c>@using static</c> in _ViewImports, so a
/// view just writes <c>@Icon("gift")</c>). They are decorative: the text next to them carries the meaning.
/// </summary>
public static class Icons
{
    private static readonly Dictionary<string, string> Paths = new()
    {
        ["gift"] = "<rect x='3' y='8' width='18' height='4' rx='1'/><path d='M12 8v13'/><path d='M19 12v7a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2v-7'/><path d='M7.5 8a2.5 2.5 0 0 1 0-5C10 3 12 8 12 8s2-5 4.5-5a2.5 2.5 0 0 1 0 5'/>",
        ["search"] = "<circle cx='11' cy='11' r='7'/><path d='m20 20-3.5-3.5'/>",
        ["check"] = "<path d='M20 6 9 17l-5-5'/>",
        ["heart"] = "<path d='M19 14c1.5-1.5 3-3.2 3-5.5A5.5 5.5 0 0 0 16.5 3c-1.8 0-3 .5-4.5 2-1.5-1.5-2.7-2-4.5-2A5.5 5.5 0 0 0 2 8.5c0 2.3 1.5 4 3 5.5l7 7Z'/>",
        ["mail"] = "<rect x='2' y='4' width='20' height='16' rx='2'/><path d='m22 7-10 6L2 7'/>",
        ["list"] = "<rect x='5' y='3' width='14' height='18' rx='2'/><path d='M9 8h6M9 12h6M9 16h4'/>",
        ["calendar"] = "<rect x='3' y='5' width='18' height='16' rx='2'/><path d='M3 10h18M8 3v4M16 3v4M8 14h.01M12 14h.01M16 14h.01M8 18h.01M12 18h.01'/>",
        ["star"] = "<path d='M12 2.5l2.9 6.1 6.6.7-4.9 4.5 1.4 6.6L12 17.1l-6 3.3 1.4-6.6-4.9-4.5 6.6-.7z'/>",
        ["shoe"] = "<path d='M4 16V7h5l1 3 6 2c2.5.8 4 1.8 4 4v1H4z'/><path d='M4 20h16'/>",
        ["shirt"] = "<path d='M20.4 6.6 16 4a4 4 0 0 1-8 0L3.6 6.6a1 1 0 0 0-.5 1.2l1 3a1 1 0 0 0 1.2.6L7 11v9a1 1 0 0 0 1 1h8a1 1 0 0 0 1-1v-9l1.7.4a1 1 0 0 0 1.2-.6l1-3a1 1 0 0 0-.5-1.2z'/>",
        ["plus"] = "<path d='M12 5v14M5 12h14'/>",
        ["santa"] = "<path d='M4 17c.5-6.5 4-11 9-11 3.2 0 5.6 2.2 6.4 5.4'/><circle cx='19.6' cy='13.6' r='2'/><rect x='3' y='17' width='17' height='4' rx='2'/>",
        ["arrow"] = "<path d='M5 12h14M13 6l6 6-6 6'/>",
        ["chevron-left"] = "<path d='m15 18-6-6 6-6'/>",
        ["chevron-right"] = "<path d='m9 18 6-6-6-6'/>",
        ["pause"] = "<rect x='6' y='5' width='4' height='14' rx='1'/><rect x='14' y='5' width='4' height='14' rx='1'/>",
        ["play"] = "<path d='M8 5.5v13a1 1 0 0 0 1.5.86l10.5-6.5a1 1 0 0 0 0-1.72L9.5 4.64A1 1 0 0 0 8 5.5z'/>",
        ["expand"] = "<path d='M15 3h6v6M9 21H3v-6M21 3l-7 7M3 21l7-7'/>",
        ["close"] = "<path d='M18 6 6 18M6 6l12 12'/>",
        ["sheet"] = "<rect x='3' y='3' width='18' height='18' rx='2'/><path d='M3 9h18M3 15h18M9 3v18'/>",
        ["sliders"] = "<path d='M4 21v-7M4 10V3M12 21v-9M12 8V3M20 21v-5M20 12V3M1 14h6M9 8h6M17 16h6'/>",
        ["download"] = "<path d='M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4'/><path d='m7 10 5 5 5-5M12 15V3'/>",
        ["upload"] = "<path d='M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4'/><path d='m17 8-5-5-5 5M12 3v12'/>",
        ["trash"] = "<path d='M3 6h18M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2M19 6l-1 14a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2L5 6M10 11v6M14 11v6'/>",
        ["send"] = "<path d='m22 2-7 20-4-9-9-4z'/><path d='M22 2 11 13'/>",
        ["lock"] = "<rect x='4' y='11' width='16' height='10' rx='2'/><path d='M8 11V7a4 4 0 0 1 8 0v4'/>",
        ["logout"] = "<path d='M9 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h4M16 17l5-5-5-5M21 12H9'/>",
        ["link"] = "<path d='M10 13a5 5 0 0 0 7.5.5l3-3a5 5 0 0 0-7-7l-1.7 1.7'/><path d='M14 11a5 5 0 0 0-7.5-.5l-3 3a5 5 0 0 0 7 7l1.7-1.7'/>",
        ["table"] = "<rect x='3' y='4' width='18' height='16' rx='2'/><path d='M3 10h18M9 10v10'/>",
        ["chart"] = "<path d='M3 3v18h18'/><path d='M7 16v-4M12 16V8M17 16v-7'/>",
        ["users"] = "<path d='M17 21v-2a4 4 0 0 0-4-4H5a4 4 0 0 0-4 4v2'/><circle cx='9' cy='7' r='4'/><path d='M23 21v-2a4 4 0 0 0-3-3.9M16 3.1a4 4 0 0 1 0 7.8'/>",
        ["edit"] = "<path d='M12 20h9'/><path d='M16.5 3.5a2.1 2.1 0 0 1 3 3L7 19l-4 1 1-4z'/>",
        ["info"] = "<circle cx='12' cy='12' r='10'/><path d='M12 16v-4M12 8h.01'/>",
        ["alert"] = "<path d='M10.3 3.9 1.8 18a2 2 0 0 0 1.7 3h17a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0z'/><path d='M12 9v4M12 17h.01'/>",
        ["key"] = "<circle cx='7.5' cy='15.5' r='5.5'/><path d='m21 2-9.6 9.6M15.5 7.5l3 3L22 7l-3-3'/>",
        ["pin"] = "<path d='M20 10c0 6-8 12-8 12s-8-6-8-12a8 8 0 0 1 16 0z'/><circle cx='12' cy='10' r='3'/>",
        ["sparkle"] = "<path d='M12 3v4M12 17v4M3 12h4M17 12h4M5.6 5.6l2.8 2.8M15.6 15.6l2.8 2.8M5.6 18.4l2.8-2.8M15.6 8.4l2.8-2.8'/>",
    };

    /// <summary>A small line icon, sized in pixels.</summary>
    public static IHtmlContent Icon(string name, int size = 20) => new HtmlString(
        $"<svg class='icon' width='{size}' height='{size}' viewBox='0 0 24 24' fill='none' stroke='currentColor' stroke-width='2' " +
        $"stroke-linecap='round' stroke-linejoin='round' aria-hidden='true'>{Paths[name]}</svg>");
}
