# v0.5.4 alpha validation

v0.5.3 compiled and loaded, but computed health failed propagation: native facade 16, patched object getter 20, target 20. Registration removed and health stopped. AP 49 refunds, 23 resets, zero errors.

v0.5.4 removes both previous health patch paths and tests a single explicitly armed native max setter, exact verified signatures 2002021185C00C, Health=2. Only full, unmodified integral baseline; no writes by default. One attempt per launch, config arm consumed before gameplay; verifies raw/permanent/effective maximum, native current and modifier count, attempts same-object rollback on failure. No automatic restoration on teardown; persistent save modification possible. Identity/authoritativeness, native health setter behavior, actual healing and persistence still unverified. AP budget unchanged. Standalone guard tests added; no C# compilation or runtime test here. Fresh disposable solo only.
