# Getting started

## Explore the workflow

You can read the prompts, managed example, and API headers on any computer. The application itself is a Windows x64 WPF program targeting .NET 9.

Start with [the prompt guide](../prompts/README.md). The development workflow uses an existing HMM API and managed example as its acquisition foundation, then creates an independent UI application.

## Open the supplied DemoApp preview

1. Open this repository's **Releases** tab and download `HMM_DemoApp_Windows_x64_Preview.zip`.
2. Extract the complete ZIP to a local Windows folder. Keep `HMM_DemoApp.exe`, the managed DLL, native DLLs, `data/`, and configuration files together.
3. Check that the matching .NET 9 Windows Desktop runtime for x64 is installed. This application is framework-dependent.
4. Read `DEMO_README.txt` inside the download. The preview is the supplied executable, with development logs and symbols removed; it has not been rerun during repository preparation.
5. Open `HMM_DemoApp.exe` to inspect the interface. Actual acquisition requires your compatible HMM system and a separately confirmed hardware setup.

If initialization is unavailable, retain the application's deployment/error details and contact TLI. Do not replace DLLs or edit configuration just to bypass checks.

## Develop your own interface

The public source is the supplied managed example. The final generated DemoApp source was not included in the uploaded materials.

Use the environment prompt to establish a verified working copy with the matched HMM SDK. After that stage succeeds, use the DemoApp prompt to build an independent application around your own workflow. Current source build-path limitations and supplied manifest differences are recorded in [Validation scope](validation-scope.md).

## Discuss your microscope

Contact [info@triadlightinnovation.com](mailto:info@triadlightinnovation.com?subject=HMM%20API%20demo%20inquiry) or [TLI on LinkedIn](https://www.linkedin.com/company/triad-light-innovation). Share your microscope type, scanner setup, and what you want the software to do.

[Explore HMM](https://www.triadlightinnovation.com/hmm?utm_source=github&utm_medium=referral&utm_campaign=hmm_api_demo_202610).
