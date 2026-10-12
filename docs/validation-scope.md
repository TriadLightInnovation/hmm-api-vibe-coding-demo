# Validation scope and supplied-file provenance

Repository preparation was performed without running Windows software or operating microscope hardware.

## What was checked during packaging

- The source archive's two inner ZIPs were inventoried.
- The prompt requirements, C# project configuration, native interop declarations, and executable runtime configuration were inspected.
- Managed reference `.cs`, `.xaml`, `.csproj`, and `.ps1` files were copied without changing their bytes.
- Machine-specific paths in the DemoApp prompt were made portable.
- Development logs, symbols, build caches, generated reports, and bundled analysis tools were excluded from the public package.
- Retained DemoApp runtime files were copied without changing their bytes.
- The supplied external DemoApp manifest was compared with the included configuration JSON files.

## What still needs Windows validation

- Restore/build of a correctly configured managed working copy.
- Offline tests and normal UI launch/close for that exact source and SDK.
- Supplied DemoApp launch and deployment diagnostics.
- Any hardware acceptance on the user's actual instrument.

The uploaded source snapshot points to sibling SDK and manifest paths that are absent at those locations. These must be resolved in a working copy before claiming a reproducible build.

The external `sdk-baseline.json` in the supplied DemoApp ZIP has two configuration hash differences:

| File | Comparison with supplied external manifest |
| --- | --- |
| `data/config_channel_module.json` | Different hash |
| `data/ctrl_top.json` | Different hash |

The remaining eleven configuration JSON files matched. The supplied external manifest and all shipped runtime/configuration bytes are retained unchanged in the preview download. No baseline hashes or acceptance results were rewritten. The comparison does not establish which manifest is embedded in the compiled DemoApp and does not, by itself, prove whether that executable can initialize.

The reference source contains historical SDK profiles and experimental restart paths. Historical reports are not current acceptance for another SDK or instrument. Establish the standard lifecycle and current SDK compatibility with TLI when commissioning your system.

The archive filename's API version 3.6.1 label, the API header's Beta20260629 string, and historical profile labels in the source are recorded provenance, not a compatibility certification.
