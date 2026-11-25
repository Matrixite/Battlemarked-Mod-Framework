# v0.5.0 alpha validation

AP: v0.4.8 +3 refund mode was confirmed in gameplay and diagnostics (17 refunds, 8 resets, zero errors; multiple five-spend sequences). AP budget implementation is unchanged. Early turn endings and copied state remain limitations.

Health: checked supplied generated Jade.GameModel.dll metadata for Health=2, StatModifier constructor 2004011185C00C1185AC1185B0, AdditionAndSubtraction=0, Temporary=0, Stat.AddModifier 2001011282F8, raw max/modifiers getters and Piece usesHealth/IsDowned. This verifies signatures, not native behavior. Health defaults to 100%, samples read-only, and requires verified full, unmodified health. Temporary modifier only; no direct current/max setter. Attempts are bounded by native identity; no retry after loss or failure. Native modifier persistence and actual HP effect remain unverified. Use fresh disposable solo adventures.

Pure integer health targets and guards have standalone C# tests; equivalent arithmetic was checked outside the game. No C# compilation or in-game testing of v0.5.0 has been performed here.
