# Independent Windows fixture review

3 October 2026. Independent GPT-6 Astra, high reasoning; read-only review of the fixture proposal/preflight against the accepted NEXT-002 security contract. No accounts, groups, task, fixture cluster or live probes were created by the review.

The initial assessment accepted the resource scope but required three bounded corrections:

- Authenticate temporary bootstrap; do not create a network `trust` superuser window.
- Protect LocalSystem code, dependencies, configuration and their ancestors against standard-user replacement; refuse unexpected reparse points and prevent teardown following user-planted links.
- Persist protected credential-free resource ownership and support repeatable cleanup after runner/machine interruption; account for surviving task children and refuse unresolved runs.

These requirements are now explicit in the fixture README. The reviewer reread the correction and returned: **approved approach for the authorisation-preparation gate; no unresolved material design finding within this scope.**

The proposal is suitable for requesting human authorisation and implementing the runner. This is not user authorisation or runtime acceptance. The named resource-creation gate and S01–S08 remain open. Normal installation, service/PostgreSQL changes and rollout are excluded.
