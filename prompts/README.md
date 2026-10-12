# Development prompts

Run these stages in order on a Windows development computer.

| Stage | File | Purpose |
| --- | --- | --- |
| 1 | [English environment check](environment-check.en.txt) | Discover the supplied source and SDK, resolve actual dependencies, make a separate working copy, build, and verify offline behavior. |
| 1 | [繁體中文環境檢查](environment-check.zh-TW.txt) | The Traditional Chinese alternative to the English environment prompt. |
| 2 | [DemoApp development](demo-app.txt) | Create an independent application and a new interface while preserving the verified example's acquisition and processing behavior. |

Stage 1 must produce the environment report and verified template used by Stage 2. Historical repair or acceptance records are not included here. Re-establish acceptance for your own source and SDK; do not assume the supplied reference snapshot is already the repaired template.

The DemoApp prompt's machine-specific Windows paths have been replaced with `<WORKSPACE>` and a portable PowerShell command. Replace that placeholder with your actual absolute workspace path before use. Functional requirements were retained.

Place the reference project and a compatible, complete HMM SDK in a local development workspace. The environment prompt is intended to discover and adapt the actual project paths, rather than assume a particular machine or folder date.

The prompts specify offline verification and manual hardware operation. The 40-minute example concerns the demonstrated interface-development task, after preparing the development foundation.
