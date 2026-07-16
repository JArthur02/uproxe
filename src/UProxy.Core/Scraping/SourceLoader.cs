namespace UProxy.Core.Scraping;

public sealed class SourceLoader
{
    public IReadOnlyList<string> LoadUrls(string path)
    {
        if (!File.Exists(path))
            return [];

        var urls = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lines = File.ReadAllLines(path);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0)
                continue;
            if (line.StartsWith('#') || line.StartsWith(';') || line.StartsWith("//"))
                continue;

            // Legacy range expansion: url[1-3] → url1, url2, url3 (bounded)
            if (TryExpandRange(line, out var expanded))
            {
                foreach (var expandedUrl in expanded)
                {
                    if (seen.Add(expandedUrl))
                        urls.Add(expandedUrl);
                }
                continue;
            }

            if (Uri.TryCreate(line, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                var normalized = uri.ToString();
                if (seen.Add(normalized))
                    urls.Add(normalized);
            }
        }

        return urls;
    }

    private static bool TryExpandRange(string line, out List<string> expanded)
    {
        expanded = [];
        var start = line.IndexOf('[');
        var end = line.IndexOf(']');
        if (start < 0 || end <= start)
            return false;

        var inside = line[(start + 1)..end];
        var dash = inside.IndexOf('-');
        if (dash < 0)
            return false;

        if (!int.TryParse(inside[..dash], out var from) ||
            !int.TryParse(inside[(dash + 1)..], out var to))
            return false;

        if (to < from)
            (from, to) = (to, from);

        if (to - from > 50)
            return false; // hard cap

        var prefix = line[..start];
        var suffix = line[(end + 1)..];
        for (var n = from; n <= to; n++)
        {
            var candidate = prefix + n + suffix;
            if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                expanded.Add(uri.ToString());
            }
        }

        return expanded.Count > 0;
    }
}
