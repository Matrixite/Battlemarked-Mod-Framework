using System.Globalization;

namespace Battlemarked.ModFramework;

// Explicit, version-specific request in the module data directory, independent of
// BepInEx's in-memory/config serialization. Claim before permitting any native write.
internal sealed class NativeHealthArmRequest
{
    internal const string PendingFileName = "native-health-trial-055.pending";
    internal const string ClaimedFileName = "native-health-trial-055.claimed";
    private readonly string pendingPath, claimedPath;

    internal NativeHealthArmRequest(string directory)
    {
        pendingPath = Path.Combine(directory, PendingFileName);
        claimedPath = Path.Combine(directory, ClaimedFileName);
    }

    internal bool Pending => File.Exists(pendingPath);

    private static bool Valid(int percent) => percent > 100 && percent <= 200 && percent % 25 == 0;
    private static string Payload(int percent) => "BMF-0.5.5 native-health " + percent.ToString(CultureInfo.InvariantCulture);

    internal void Queue(int percent)
    {
        if (!Valid(percent)) throw new ArgumentOutOfRangeException(nameof(percent));
        // Publish only a complete, verified request; do not overwrite a pending one.
        string temp = pendingPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            string payload = Payload(percent);
            File.WriteAllText(temp, payload);
            if (File.ReadAllText(temp) != payload) throw new IOException("Arm request readback failed");
            File.Move(temp, pendingPath);
            if (File.ReadAllText(pendingPath) != payload) throw new IOException("Published arm request readback failed");
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    internal void Cancel()
    {
        File.Delete(pendingPath);
        if (Pending) throw new IOException("Arm request cancellation could not be verified");
    }

    internal bool Consume(int percent, out string detail)
    {
        if (!Pending) { detail = "No pending request found at startup"; return false; }
        // A failed claim throws, so the caller remains unarmed. A claimed marker
        // is never considered pending again, even if its content is invalid.
        File.Move(pendingPath, claimedPath, overwrite: true);
        if (Pending) throw new IOException("Pending request still exists after claim");
        if (!Valid(percent) || File.ReadAllText(claimedPath) != Payload(percent))
        {
            detail = "Request claimed but rejected: invalid or changed target";
            return false;
        }
        detail = "Request claimed and verified at startup";
        return true;
    }
}
