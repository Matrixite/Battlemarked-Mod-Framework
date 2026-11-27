namespace Battlemarked.ModFramework;

// Ordinary managed values only; never persist a modified native inventory.
internal static class HandSizePolicy
{
    internal const int Minimum = 1, Maximum = 30, Default = 12;
    internal static int Clamp(int size) => Math.Clamp(size, Minimum, Maximum);
    internal static bool IsFull(int count, int size) => count >= Clamp(size);
    internal static bool MatchesSession(string liveSession, ulong liveHost, string candidateSession, ulong candidateHost) =>
        !string.IsNullOrWhiteSpace(liveSession) && liveSession == candidateSession && liveHost == candidateHost;
}
