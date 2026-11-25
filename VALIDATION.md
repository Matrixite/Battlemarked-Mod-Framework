# v0.4.7 alpha validation

Optional experimental refund mode (off by default): no max setter is called on the refund branch; a successful one-AP deduction from a verified 2/2 state may be refunded to 2/2, bounded by the selected bonus per native AP object. Budget resets only on a verified 0-to-2 refill. Elevated caps are skipped, and old-cap adjustment is ignored in refund mode. Existing exact Harmony targets, solo/build gates and legacy method are retained.

Standalone C# scenarios cover +0 through +3, exhaustion, invalid/free/two-point deductions, duplicate or partial refills, and unverified states. C# compilation and native runtime testing have not been performed in this environment. The refund can still affect current saved AP; native state copies and partial/early turn endings are unresolved. Fresh disposable solo adventure only. The v0.4.6 +3 cap-based change is still unverified in-game.
