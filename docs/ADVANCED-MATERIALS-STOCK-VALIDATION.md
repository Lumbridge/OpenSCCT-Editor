# Supplemental stock rendering acceptance

2026-11-09, after the local packages were built. Baseline: published game 0.8.119 (`b37932af`); materials: `4d3d976c`. These measurements were made on local feature prereleases before release preparation.

## Captures and representative measurements

Four warehouse cameras were captured at 1280x720 on an RTX 4070 SUPER using Godot 4.7.2, Vulkan Forward+. Normal, night and thermal captures completed on both builds. Map geometry, material/shader counts and per-camera draw calls agree. The normal dark-room image is pixel-identical; remaining differences are small and localized, including animated surfaces. These observations do not establish every stock material or hardware target.

| Mode | Camera | Mean RGBA8 channel error | Changed pixels |
|---|---|---:|---:|
| Normal | kitchen | 0.00588 / 255 | 0.069% |
| Normal | dark-room | 0.00000 / 255 | 0.000% |
| Normal | courtyard | 0.00608 / 255 | 0.035% |
| Normal | crane | 0.09070 / 255 | 0.120% |
| Night | kitchen | 0.01259 / 255 | 0.101% |
| Night | dark-room | 0.00023 / 255 | 0.001% |
| Night | courtyard | 0.01326 / 255 | 0.056% |
| Night | crane | 0.14720 / 255 | 0.136% |
| Thermal | kitchen | 0.01273 / 255 | 0.613% |
| Thermal | dark-room | 0.00037 / 255 | 0.042% |
| Thermal | courtyard | 0.00210 / 255 | 0.318% |
| Thermal | crane | 0.02571 / 255 | 0.354% |

Completed captures report map-loader times of 2.76�3.04 s for baseline and 2.90�3.94 s for materials; maximum draw calls are 332 in normal/night and 326 in thermal, identical between builds. These are single-run, cached-content observations, not launch-to-controllable or minimum-hardware benchmarks. Pipeline caching was disabled in the isolated profiles for later captures.

## Open acceptance failures

- Six completed capture processes stalled at shutdown and were terminated after their capture verdict; both builds were affected.
- Both EMF stock-map capture processes crashed before completing their images. Candidate log reports Vulkan fence error -4.
- Separate candidate fog-test runs faulted in nvoglv64.dll; warmed retries completed 36 samples. Baseline source also reproduced the first-phase pixel miss.
- The aggregate SteeE render smoke fails emitters=65/particles=0 on both builds.

No clean end-to-end pass is claimed for these additional stock checks. The authored-material GPU check separately passed all 23 variants, including EMF, with source and exported game binaries; editor, import, collision and native-export acceptance is described in ADVANCED-MATERIALS-VALIDATION.md. First-use/driver stability, clean large-map shutdown, broad stock-map comparisons, extended multiplayer sessions and low-end performance remain open. The raw machine-readable metrics are in the accompanying package's STOCK-VALIDATION.json.
