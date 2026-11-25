# v0.5.2 alpha validation

v0.5.1 test: 24 verified health writes, 19 losses and 19 successful refreshes, no reported errors, all five tracked objects at their last observed target; AP 16 refunds and six resets, no errors. This is not evidence of continuous health retention.

Verified supplied generated Jade.GameModel.dll metadata: StatsFacade.UpdateModifiers signature 2002011512210112831802. New postfix is gated by build, main thread, live facade match and local solo active hero scope, and is optional; AP observation remains fallback. Session facade wrappers are cleared on scope teardown. Native identity and aggregate modifier checks still do not establish authoritative object or exact modifier identity. No direct max or HP setter. Rolling eight per five seconds per health object replaces 32 lifetime attempts; 128 object bounds remain. Full-to-full AP refill calls do not reset budgets; verified partial-to-full resets enabled by default and can be disabled.

Equivalent pure guard simulations checked for partial refill, full-to-full rejection, throttle and 100 spaced attempts. C# test scenarios included but not compiled here; native hook, UI persistence, save behavior, and AP partial reset are unverified in game. Use disposable local solo only.
