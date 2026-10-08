using System.Globalization;
using System.Text;

namespace DonationAutomate.Pages;

/// <summary>
/// Drawing of Morro Dona Marta for the home page: houses climbing the hill, the plano inclinado,
/// and Cristo Redentor on the Corcovado behind. The houses come from a seeded random generator,
/// so the drawing is the same every time; it is built once and reused.
/// </summary>
public static class SantaMartaIllustration
{
    private static readonly Lazy<string> Drawing = new(Build);
    public static string Svg => Drawing.Value;

    private const double Width = 560, Height = 300;

    // The hill's outline: rises from the bottom left to its crest, then runs gently down to the right.
    private const double CrestX = 380, FootY = 280, Rise = 176;

    private static double HillTop(double x) => x <= CrestX
        ? FootY - Rise * Math.Sin(Math.Clamp(x / CrestX, 0, 1) * Math.PI / 2)
        : FootY - Rise + (x - CrestX) * 0.25;

    /// <summary>Where the hill's outline reaches height <paramref name="y"/> on its left side.</summary>
    private static double HillLeftAt(double y) => y >= FootY
        ? -12
        : Math.Asin(Math.Min(1, (FootY - y) / Rise)) * 2 / Math.PI * CrestX;

    /// <summary>Roughly where the houses stop; to the right the hill is forest.</summary>
    private static double HousesRightAt(double y) => 326 + (y - 110) * 0.6;

    // The plano inclinado runs straight up through the houses.
    private const double TrackX0 = 140, TrackY0 = 304, TrackX1 = 350, TrackY1 = 112;
    private const double CrecheStation = 0.52;  // "3ª parada do plano inclinado"

    private static readonly string[] HouseColors = ["#b8473c", "#d6a64e", "#3f8a62", "#e3d6bb", "#c56f45"];

    private static string F(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    private static string Build()
    {
        var rng = new Random(1958);
        var svg = new StringBuilder();

        svg.Append($"""<svg class="santa-marta" viewBox="0 0 {F(Width)} {F(Height)}" role="img" aria-label="Ilustração do Morro Dona Marta, em Botafogo: casas subindo o morro, o plano inclinado e o Cristo Redentor no alto do Corcovado">""");
        svg.Append("""<defs><radialGradient id="sm-cristo-glow"><stop offset="0" stop-color="#fff3d6" stop-opacity=".45"/><stop offset="1" stop-color="#f3cd7a" stop-opacity="0"/></radialGradient></defs>""");

        // Corcovado: a broad slope on the left, the rounded "hump" on top and a sheer face on the right
        svg.Append("""<circle cx="480" cy="20" r="40" fill="url(#sm-cristo-glow)"/>""");
        svg.Append("""<path d="M300 300C340 230 372 170 396 132c20-32 38-62 56-86 8-10 18-16 28-16s18 6 22 18c6 18 8 36 16 50 10 18 28 26 44 30v172z" fill="#173525"/>""");
        svg.Append("""<g fill="#fbf4e4"><rect x="477" y="24" width="6" height="7"/><path d="M477.8 24 478.8 10.5h2.4l1 13.5z"/><path d="M470 11.1q10-1.2 20 0v1.8q-5-.2-8.6 1.2h-2.8q-3.6-1.4-8.6-1.2z"/><circle cx="480" cy="8.1" r="2.1"/></g>""");

        // the hill
        var d = new StringBuilder($"M-4 {F(HillTop(-4))}");
        for (double x = 2; x <= Width + 4; x += 6) d.Append($"L{F(x)} {F(HillTop(x))}");
        d.Append($"L{F(Width + 4)} {F(Height + 4)}L-4 {F(Height + 4)}Z");
        svg.Append($"""<path d="{d}" fill="#1f4632"/>""");

        Houses(svg, rng);
        Track(svg);

        svg.Append("</svg>");
        return svg.ToString();
    }

    /// <summary>
    /// Rows of houses from the top of the hill down, each row drawn over the one above it so the
    /// houses look stacked up the slope; lower rows are bigger, as they are closer.
    /// </summary>
    private static void Houses(StringBuilder svg, Random rng)
    {
        for (double y = 116; y < Height + 20;)
        {
            var scale = 0.75 + (y - 116) / (Height - 116) * 0.5;
            var right = HousesRightAt(y) + (rng.NextDouble() - 0.5) * 24;
            var x = HillLeftAt(y) - 8 + rng.NextDouble() * 8;
            while (x < right)
            {
                var w = (20 + rng.NextDouble() * 10) * scale;
                var h = (15 + rng.NextDouble() * 7) * scale;
                if (y - h >= HillTop(x + w / 2) - 4)
                {
                    var top = y - h;
                    svg.Append($"""<rect x="{F(x)}" y="{F(top)}" width="{F(w)}" height="{F(h)}" fill="{HouseColors[rng.Next(HouseColors.Length)]}"/>""");
                    if (rng.NextDouble() < 0.45)
                        svg.Append($"""<rect x="{F(x + w * 0.35)}" y="{F(top + h * 0.3)}" width="{F(4 * scale)}" height="{F(5 * scale)}" fill="#ffd98e"/>""");
                }
                x += w + 1.2 * scale;
            }
            y += 13 * scale;
        }
    }

    private static void Track(StringBuilder svg)
    {
        svg.Append($"""<line x1="{F(TrackX0)}" y1="{F(TrackY0)}" x2="{F(TrackX1)}" y2="{F(TrackY1)}" stroke="#14110d" stroke-width="5" stroke-linecap="round"/>""");
        svg.Append($"""<line x1="{F(TrackX0)}" y1="{F(TrackY0)}" x2="{F(TrackX1)}" y2="{F(TrackY1)}" stroke="#d9cfb6" stroke-width="1.2" stroke-dasharray="6 3"/>""");

        var x = TrackX0 + (TrackX1 - TrackX0) * CrecheStation;
        var y = TrackY0 + (TrackY1 - TrackY0) * CrecheStation;
        svg.Append($"""<g transform="translate({F(x + 10)} {F(y - 12)})"><title>Creche Mundo Infantil: 3ª parada do plano inclinado</title><circle r="9" fill="#14110d" fill-opacity=".55"/><path d="M0-1.6C-1.4-4.4-5.4-4.2-5.4-.8c0 3 3.6 5 5.4 7.2C1.8 4.2 5.4 2.2 5.4-.8c0-3.4-4-3.6-5.4-.8z" fill="#ff7466"/></g>""");
    }
}
