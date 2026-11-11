using Aiursoft.AnduinOS.OfflineBundles.Core.Models;

namespace Aiursoft.AnduinOS.OfflineBundles.Core.Services;

public static class DebianControl
{
    public static List<Dictionary<string, string>> Read(string text)
    {
        var paragraphs = new List<Dictionary<string, string>>();
        var paragraph = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? previous = null;
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.Length == 0)
            {
                if (paragraph.Count != 0)
                {
                    paragraphs.Add(paragraph);
                    paragraph = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }
                previous = null;
            }
            else if (line[0] is ' ' or '\t' && previous != null)
            {
                paragraph[previous] += "\n" + line;
            }
            else
            {
                var colon = line.IndexOf(':');
                if (colon < 1 || !paragraph.TryAdd(line[..colon], line[(colon + 1)..].TrimStart()))
                {
                    throw new InvalidDataException("Malformed or duplicate Debian control field.");
                }
                previous = line[..colon];
            }
        }
        if (paragraph.Count != 0)
        {
            paragraphs.Add(paragraph);
        }
        return paragraphs;
    }

    public static PackageIdentity Identity(IReadOnlyDictionary<string, string> fields)
    {
        if (!fields.TryGetValue("Package", out var name) || !fields.TryGetValue("Version", out var version) ||
            !fields.TryGetValue("Architecture", out var architecture))
        {
            throw new InvalidDataException("Incomplete Debian package identity.");
        }
        return new PackageIdentity(name, version, architecture);
    }

    public static string Write(IEnumerable<Dictionary<string, string>> paragraphs) =>
        string.Join("\n\n", paragraphs.Select(p => string.Join("\n", p.Select(f => $"{f.Key}: {f.Value}")))) + "\n\n";

    public static Dictionary<string, string> ReadOsRelease(string text)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n'))
        {
            var split = line.IndexOf('=');
            if (split > 0 && !line.StartsWith('#'))
            {
                fields.Add(line[..split], line[(split + 1)..].Trim().Trim('"', '\''));
            }
        }
        return fields;
    }
}
