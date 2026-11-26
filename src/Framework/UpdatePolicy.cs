using System.Text.Json;
using System.Text.RegularExpressions;

namespace Battlemarked.ModFramework;

// Fixed public repository; only deliberately versioned, newer source snapshots.
internal static class UpdatePolicy
{
    internal const string Repository = "Matrixite/Battlemarked-Mod-Framework";
    internal const string ManifestName = "bmf-update.json";
    internal const string RefUrl = "https://api.github.com/repos/" + Repository + "/git/ref/heads/main";
    // Keep provider-specific technical details in diagnostics, not the player UI.
    internal static string ForDisplay(string value, string fallback) =>
        string.IsNullOrEmpty(value) ||
        value.IndexOf("github", StringComparison.OrdinalIgnoreCase) >= 0 ||
        value.IndexOf(Repository, StringComparison.OrdinalIgnoreCase) >= 0
            ? fallback : value;

    internal static bool ValidCommit(string sha) => sha != null && Regex.IsMatch(sha, @"\A[0-9a-f]{40}\z");
    internal static bool ValidVersion(string version) => version != null &&
        Regex.IsMatch(version, @"\A(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\z") &&
        Version.TryParse(version, out _);
    internal static bool IsNewer(string remote, string current) =>
        ValidVersion(remote) && ValidVersion(current) && Version.Parse(remote) > Version.Parse(current);
    internal static Uri ManifestUrl(string sha) => ValidCommit(sha)
        ? new Uri($"https://raw.githubusercontent.com/{Repository}/{sha}/{ManifestName}")
        : throw new ArgumentException("Invalid update commit.");
    internal static Uri ArchiveUrl(string sha) => ValidCommit(sha)
        ? new Uri($"https://codeload.github.com/{Repository}/zip/{sha}")
        : throw new ArgumentException("Invalid update commit.");
    internal static string ReadVersion(string json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schema", out var schema) ||
                schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out int number) || number != 1 ||
                !root.TryGetProperty("version", out var v) || v.ValueKind != JsonValueKind.String) return null;
            string version = v.GetString();
            return ValidVersion(version) ? version : null;
        }
        catch (JsonException) { return null; }
    }

    // Reject paths which could escape the extraction root or be interpreted
    // differently by Windows (device names, alternate data streams, etc.).
    internal static bool SafeEntry(string path, string root)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 240 || path.Contains('\\') ||
            !path.StartsWith(root + "/", StringComparison.Ordinal)) return false;
        if (path == root + "/") return true;
        string[] parts = path.TrimEnd('/').Split('/');
        if (parts.Length < 2) return false;
        foreach (string part in parts)
        {
            if (part.Length == 0 || part.EndsWith('.') || part.EndsWith(' ') ||
                part.Any(c => c < 32 || c is ':' or '*' or '?' or '"' or '<' or '>' or '|')) return false;
            string device = part.Split('.')[0].ToUpperInvariant();
            if (device is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
                Regex.IsMatch(device, @"\A(COM|LPT)[1-9¹²³]\z")) return false;
        }
        return true;
    }
}
