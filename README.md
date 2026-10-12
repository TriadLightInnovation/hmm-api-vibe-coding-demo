# HMM API + Vibe Coding: Laser-Scanning Microscopy Demo

**Explore how AI-assisted development can help you build a microscopy interface around the HMM API.**

This repository accompanies Triad Light Innovation's demonstration of a new laser-scanning microscopy interface built in about 40 minutes using OpenAI Codex and the HMM API, with an existing managed example as the development foundation.

The timing describes this recorded interface-development example. Environment preparation, the existing acquisition implementation, and microscope commissioning are separate steps.

**[Explore HMM](https://www.triadlightinnovation.com/hmm?utm_source=github&utm_medium=referral&utm_campaign=hmm_api_demo_202610) · [Discuss your system by email](mailto:info@triadlightinnovation.com?subject=HMM%20API%20demo%20inquiry) · [Connect on LinkedIn](https://www.linkedin.com/company/triad-light-innovation)**

## What you can explore

| Resource | What it contains |
| --- | --- |
| [Prompts](prompts/README.md) | Environment preparation in English and Traditional Chinese, followed by the DemoApp development prompt. |
| [Managed example](reference/managed-example/README.md) | The supplied C# / WPF reference source, acquisition logic, display components, and offline test code. |
| [API reference](reference/api/README.md) | Supplied C++ API headers and console example for understanding the interface. |
| [Getting started](docs/getting-started.md) | How to inspect the materials, obtain the Windows DemoApp, and prepare your own development environment. |

The source in this repository is the supplied managed example. The final `HMM_DemoApp` is provided as a Windows binary preview through the repository's Releases tab; its generated source was not included in the supplied materials.

## Why this matters for microscope developers

HMM brings scan control, synchronization, signal acquisition, and processing into a shared hardware and API foundation. Developers can build their own interface and imaging workflow around that foundation.

If you are building a confocal or multiphoton microscope, integrating a scanning system, or upgrading an existing instrument, this example offers a concrete starting point for discussing your software workflow.

## Start here

1. Read [Getting started](docs/getting-started.md).
2. Read the [environment-preparation prompt](prompts/environment-check.en.txt), or its [Traditional Chinese version](prompts/environment-check.zh-TW.txt).
3. Inspect the managed example and API reference.
4. Use the [DemoApp prompt](prompts/demo-app.txt) after establishing your own verified development template.

Actual image acquisition requires compatible HMM hardware, an appropriate driver and SDK, and your configured microscope system. Building or opening a UI does not establish hardware acceptance. See [Validation scope](docs/validation-scope.md).

## Tell us what you want to build

Email **info@triadlightinnovation.com**, or [contact TLI on LinkedIn](https://www.linkedin.com/company/triad-light-innovation), with your microscope type, scanner configuration, and the workflow you want to implement.

We welcome discussions about HMM API integration and online demonstrations.

**[Learn more about HMM](https://www.triadlightinnovation.com/hmm?utm_source=github&utm_medium=referral&utm_campaign=hmm_api_demo_202610)**

## Terms

No repository-wide open-source license has been selected for these supplied materials. Contact TLI about reuse, redistribution, or commercial integration terms. Existing SDK and third-party component terms continue to apply.
