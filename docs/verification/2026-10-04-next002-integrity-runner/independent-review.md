# Independent fixture preflight review

An independent Astra reviewer inspected the current G01 adaptation against base `be3b409`, its new admission/recovery tests and the retained ordinary red/green results. This is technical preflight, not execution authorisation or G01 completion.

The review required three corrections, all resolved in the final inspected diff:

1. Replace `dotnet vstest` SDK discovery with `dotnet exec` of the validated absolute installed .NET 10 VSTest DLL, preventing writable ancestor `global.json` files from selecting privileged code.
2. Derive the scheduled-task deadline from Integrity fixture mode at its first registration, so the reused task retains the full suite deadline.
3. During teardown, skip absent evidence sections after partial initialisation while validating every existing entry before removing the exact owned root.

The reviewer approved the bounded preflight after inspecting those corrections. Generated database identity checks, durable create intents, SSPI repository connections, the disabled-bootstrap-owned CONNECT-only trust control and inherited job containment fit the reviewed design.

The explicit generated-database/CREATEDB/trust-control authorisation extension, native execution results and full G01 acceptance remain outstanding. Normal installation rollout remains separately gated.
