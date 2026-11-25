# v0.4.8 alpha validation

Based on the v0.4.7 report: configured +3, existing cap 3, zero refunds, 32 elevated-cap spend skips. Refund mode now honors explicit existing-cap opt-in and subtracts existing AP above the normal allowance from the refund budget. At cap 3 and +3, two verified one-point refunds target five actions; at cap 2 and +3, three refunds target five. No max setter is invoked by this path. Existing exact hook signatures and solo/build gates remain unchanged. Same-cap verification and a cap lock guard against mid-cycle cap changes. A cap above target is not lowered, and a zero bonus is read-only.

32 cap/bonus/opt-in cases checked using an equivalent JavaScript simulation; standalone C# tests updated but not compiled or executed here. Native stat copies, partial/early turn endings, loaded save behavior, and actual five-action usability remain unverified. Use disposable solo adventures only.
