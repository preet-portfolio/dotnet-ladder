## Fitted costs (ns)

| Feature | Utf8JsonReader | Ours | Difference |
|---|---:|---:|---:|
| int | 18.475 | 10.169 | +8.306 |
| double | 53.432 | 30.136 | +23.296 |
| literal | 18.137 | 11.746 | +6.391 |
| container | 7.781 | 6.857 | +0.924 |
| ws_byte | 1.377 | 0.927 | +0.450 |
| string | 23.327 | 10.391 | +12.935 |
| string_byte | 0.022 | 0.041 | -0.018 |
| property | -3.370 | 3.431 | -6.801 |

## Predicted vs measured (µs)

| File | STJ predicted | STJ measured | Ours predicted | Ours measured | Gap predicted | Gap measured | Gap error |
|---|---:|---:|---:|---:|---:|---:|---:|
| twitter.json | 462 | 729 | 372 | 497 | 90 | 232 | -61% |
| citm_catalog.json | 2,246 | 3,637 | 1,697 | 1,518 | 549 | 2,118 | -74% |
| canada.json | 6,808 | 5,375 | 4,117 | 2,285 | 2,692 | 3,090 | -13% |

## Predicted gap by feature (µs)

| File | numbers | strings | properties | literals | containers | whitespace |
|---|---:|---:|---:|---:|---:|---:|
| twitter.json | 18 | 55 | -91 | 30 | 4 | 74 |
| citm_catalog.json | 120 | 6 | -176 | 8 | 40 | 553 |
| canada.json | 2,588 | 0 | -0 | 0 | 104 | 0 |
