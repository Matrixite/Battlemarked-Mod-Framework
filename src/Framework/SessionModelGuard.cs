namespace Battlemarked.ModFramework;

// The framework polls Unity scenes after native callbacks may already have run.
internal static class SessionModelGuard
{
    internal static bool RetainCapture(int? capturedScene, int newScene)
        => capturedScene.HasValue && capturedScene.Value == newScene;

    internal static bool IsSameScene(int? previous, string previousName, int next, string nextName)
        => previous.HasValue && previous.Value == next && previousName == nextName;

    internal static bool MatchesTeardown(IntPtr retired, IntPtr tracked)
        => retired != IntPtr.Zero && retired == tracked;

    internal static bool PreferExistingHud(bool preferredModel, int? capturedScene, int nextScene,
        bool fromHud, IntPtr existing, IntPtr candidate)
        => preferredModel && RetainCapture(capturedScene, nextScene) && !fromHud &&
            existing != IntPtr.Zero && candidate != existing;

    // A false UI single-player flag is not sufficient to authorize a native write.
    // The alternative requires an independently checked current HUD local room,
    // plus all the other positive UI and hero checks. Unknown never authorizes.
    internal static bool LocalRoomProof(bool fromHud, bool nativeLocalRoom, bool? connected,
        bool? roomHost, int? roomPlayers, int? localPlayers)
        => fromHud && nativeLocalRoom && connected == true && roomHost == true &&
            roomPlayers == 1 && localPlayers == 1;

    internal static string SessionRejection(bool? solo, bool? local, bool? multi,
        bool? inGame, bool? playing, bool? host, bool roomProof)
    {
        if (multi == true) return "multiplayer";
        if (multi != false) return "multiplayer state unknown";
        if (local != true) return "not verified local game";
        if (inGame != true) return "not verified in game";
        if (playing != true) return "not verified playing level";
        if (host != true) return "not verified local host";
        if (solo != true && !(solo == false && roomProof)) return "not verified single player";
        return null;
    }
}
