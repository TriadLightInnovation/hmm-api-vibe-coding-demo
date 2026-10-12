# Supplied managed reference example

This directory contains the supplied C# / .NET 9 / WPF / x64 development snapshot. It is the reference application from which a separately generated DemoApp was developed. The generated `HMM_DemoApp` source is not part of this directory.

Acquisition, display, interop, and offline test source files are preserved as supplied. Historical development reports and build outputs were omitted.

## Preparing to build

Use the [environment-preparation prompt](../../prompts/environment-check.en.txt), with this project and a compatible SDK, to make a separate working copy and validate its actual build inputs.

The supplied project and `build.ps1` contain historical sibling-folder references:

- `../HMM_SYS_CoreAPI-20260422` and its `HMM_SYS_CoreEXE-20260422` runtime folder.
- `../HMM_FPGA_UI_Example/sdk-baseline.json`.
- A local `offline-packages` restore source.

Those sibling folders are not reconstructed in this public repository. The uploaded SDK folder used a different date, and the original sibling manifest was not included at the referenced location. These are setup inputs to resolve in a working copy, not a promise that the unmodified snapshot builds immediately after cloning.

The DemoApp download has an external manifest, but two supplied configuration JSON files differ from its recorded hashes. Do not simply treat that manifest as verified evidence or suppress failing checks. See [Validation scope](../../docs/validation-scope.md).

Keep the source's native interop layout and acquisition lifecycle compatible with the SDK supplied for your HMM system. Request matching development materials from TLI when needed.
