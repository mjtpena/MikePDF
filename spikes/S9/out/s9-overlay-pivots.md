
#### ink, RTX 5080 Laptop (dGPU): frame p50 / p95 ms (whole frame: tiles + overlay, GPU flushed)

| strategy | N | 1x | 4x | 16x |
|---|---|---|---|---|
| A rebuild+cull | 10 | 0.48 / 0.83 | 0.27 / 0.58 | 0.11 / 0.39 |
| A rebuild+cull | 100 | 3.90 / 6.13 | 1.61 / 1.93 | 0.27 / 0.66 |
| A rebuild+cull | 1000 | 30.68 / 36.76 | 8.80 / 12.70 | 2.09 / 2.79 |
| A rebuild+cull | 5000 | 180.59 / 202.24 | 37.98 / 44.53 | 5.62 / 9.83 |
| B cached+cull | 10 | 0.27 / 0.51 | 0.24 / 0.57 | 0.09 / 0.44 |
| B cached+cull | 100 | 2.65 / 3.12 | 1.05 / 1.43 | 0.19 / 0.55 |
| B cached+cull | 1000 | 20.49 / 24.93 | 7.08 / 11.64 | 1.50 / 1.89 |
| B cached+cull | 5000 | 115.24 / 125.30 | 30.16 / 34.86 | 4.33 / 5.46 |
| C cached no cull | 10 | 0.28 / 0.52 | 0.27 / 0.59 | 0.12 / 0.53 |
| C cached no cull | 100 | 2.53 / 3.14 | 1.33 / 1.64 | 0.42 / 0.75 |
| C cached no cull | 1000 | 21.07 / 25.47 | 9.65 / 12.60 | 3.58 / 4.30 |
| C cached no cull | 5000 | 107.73 / 118.73 | 44.46 / 54.06 | 18.95 / 25.77 |
| D command list | 10 | 0.30 / 0.59 | 0.26 / 0.52 | 0.14 / 0.41 |
| D command list | 100 | 2.50 / 3.00 | 1.15 / 1.50 | 0.41 / 0.63 |
| D command list | 1000 | 21.30 / 25.69 | 8.19 / 12.38 | 3.87 / 5.12 |
| D command list | 5000 | 109.82 / 116.87 | 38.41 / 53.43 | 13.05 / 17.95 |
| E grouped per colour | 10 | 0.27 / 0.50 | 0.47 / 0.77 | 0.10 / 0.33 |
| E grouped per colour | 100 | 1.79 / 2.18 | 0.73 / 1.04 | 0.40 / 0.66 |
| E grouped per colour | 1000 | 22.71 / 27.97 | 4.86 / 8.69 | 2.13 / 2.75 |
| E grouped per colour | 5000 | 436.44 / 521.91 | 51.12 / 69.91 | 6.54 / 10.72 |
| F cached realisation+cull | 10 | 0.15 / 0.37 | 0.16 / 0.45 | 0.10 / 0.39 |
| F cached realisation+cull | 100 | 0.59 / 0.98 | 0.24 / 0.57 | 0.16 / 0.48 |
| F cached realisation+cull | 1000 | 5.01 / 6.66 | 1.26 / 2.11 | 0.39 / 0.68 |
| F cached realisation+cull | 5000 | 26.73 / 46.20 | 6.15 / 8.61 | 1.12 / 1.53 |
| G raster cache (blit) | 10 | 0.10 / 0.26 | 0.16 / 0.42 | 0.10 / 0.35 |
| G raster cache (blit) | 100 | 0.11 / 0.29 | 0.12 / 0.28 | 0.12 / 0.46 |
| G raster cache (blit) | 1000 | 0.14 / 0.59 | 0.12 / 0.35 | 0.13 / 0.41 |
| G raster cache (blit) | 5000 | 0.15 / 0.53 | 0.13 / 0.40 | 0.13 / 0.47 |

#### ink, Intel Graphics (iGPU): frame p50 / p95 ms (whole frame: tiles + overlay, GPU flushed)

| strategy | N | 1x | 4x | 16x |
|---|---|---|---|---|
| A rebuild+cull | 10 | 1.51 / 2.30 | 1.07 / 1.45 | 0.94 / 1.22 |
| A rebuild+cull | 100 | 5.76 / 8.54 | 3.09 / 4.51 | 1.09 / 1.46 |
| A rebuild+cull | 1000 | 46.54 / 56.57 | 12.76 / 17.80 | 2.55 / 3.64 |
| A rebuild+cull | 5000 | 192.08 / 216.53 | 43.65 / 51.89 | 6.10 / 10.18 |
| B cached+cull | 10 | 1.47 / 1.99 | 1.17 / 1.57 | 0.87 / 1.12 |
| B cached+cull | 100 | 4.38 / 5.65 | 2.26 / 3.06 | 1.10 / 1.57 |
| B cached+cull | 1000 | 26.93 / 36.35 | 9.40 / 12.46 | 2.38 / 3.21 |
| B cached+cull | 5000 | 131.34 / 157.28 | 32.52 / 36.99 | 4.78 / 5.95 |
| C cached no cull | 10 | 1.51 / 2.46 | 1.11 / 1.59 | 0.81 / 1.10 |
| C cached no cull | 100 | 3.84 / 5.73 | 2.71 / 3.67 | 1.04 / 1.31 |
| C cached no cull | 1000 | 27.02 / 35.65 | 10.87 / 14.03 | 4.25 / 5.55 |
| C cached no cull | 5000 | 140.19 / 193.96 | 42.78 / 48.89 | 13.82 / 16.46 |
| D command list | 10 | 1.44 / 2.05 | 1.35 / 1.99 | 0.87 / 1.17 |
| D command list | 100 | 3.75 / 4.70 | 2.56 / 3.31 | 0.97 / 1.24 |
| D command list | 1000 | 25.86 / 33.58 | 9.43 / 10.43 | 3.89 / 4.74 |
| D command list | 5000 | 141.39 / 174.63 | 43.28 / 50.00 | 11.67 / 14.89 |
| E grouped per colour | 10 | 1.54 / 2.69 | 1.72 / 2.64 | 0.80 / 1.07 |
| E grouped per colour | 100 | 3.76 / 6.74 | 3.06 / 4.05 | 1.12 / 1.57 |
| E grouped per colour | 1000 | 38.67 / 49.02 | 6.95 / 8.36 | 2.93 / 4.32 |
| E grouped per colour | 5000 | 593.74 / 653.86 | 53.77 / 63.49 | 8.32 / 9.65 |
| F cached realisation+cull | 10 | 0.94 / 1.43 | 1.04 / 1.65 | 0.81 / 1.02 |
| F cached realisation+cull | 100 | 1.27 / 1.67 | 1.36 / 1.83 | 0.79 / 1.01 |
| F cached realisation+cull | 1000 | 7.29 / 9.81 | 2.83 / 3.44 | 1.16 / 1.63 |
| F cached realisation+cull | 5000 | 28.51 / 33.81 | 8.00 / 9.45 | 2.25 / 3.09 |
| G raster cache (blit) | 10 | 1.09 / 1.77 | 1.24 / 2.03 | 0.93 / 1.25 |
| G raster cache (blit) | 100 | 0.96 / 1.08 | 1.35 / 1.95 | 0.88 / 1.21 |
| G raster cache (blit) | 1000 | 1.17 / 1.47 | 1.01 / 1.47 | 0.94 / 1.29 |
| G raster cache (blit) | 5000 | 1.24 / 1.71 | 1.15 / 1.83 | 1.25 / 1.64 |

#### ink, WARP (software): frame p50 / p95 ms (whole frame: tiles + overlay, GPU flushed)

| strategy | N | 1x | 4x | 16x |
|---|---|---|---|---|
| A rebuild+cull | 10 | 0.65 / 0.86 | 0.69 / 1.12 | 0.49 / 0.65 |
| A rebuild+cull | 100 | 2.58 / 5.60 | 1.53 / 3.11 | 0.66 / 0.83 |
| A rebuild+cull | 1000 | 21.59 / 31.19 | 6.09 / 10.58 | 2.57 / 4.22 |
| A rebuild+cull | 5000 | 118.38 / 139.00 | 35.31 / 39.99 | 4.50 / 9.43 |
| B cached+cull | 10 | 0.54 / 0.70 | 0.81 / 1.22 | 0.45 / 0.62 |
| B cached+cull | 100 | 1.24 / 2.22 | 1.03 / 1.29 | 0.60 / 0.79 |
| B cached+cull | 1000 | 7.65 / 10.03 | 4.28 / 6.44 | 1.92 / 2.67 |
| B cached+cull | 5000 | 33.10 / 40.08 | 16.06 / 22.00 | 3.27 / 4.13 |
| C cached no cull | 10 | 0.53 / 0.72 | 0.83 / 1.48 | 0.50 / 0.59 |
| C cached no cull | 100 | 1.38 / 2.27 | 1.29 / 1.73 | 0.88 / 1.07 |
| C cached no cull | 1000 | 7.26 / 9.69 | 7.84 / 11.65 | 4.59 / 5.46 |
| C cached no cull | 5000 | 34.59 / 41.14 | 31.03 / 37.26 | 19.86 / 23.15 |
| D command list | 10 | 0.58 / 0.73 | 0.75 / 1.05 | 0.54 / 0.71 |
| D command list | 100 | 1.21 / 1.68 | 1.31 / 1.80 | 0.96 / 1.45 |
| D command list | 1000 | 6.88 / 10.00 | 6.71 / 8.55 | 4.05 / 4.92 |
| D command list | 5000 | 28.62 / 34.72 | 27.70 / 33.61 | 16.59 / 24.18 |
| E grouped per colour | 10 | 0.53 / 0.72 | 0.87 / 1.31 | 0.61 / 0.76 |
| E grouped per colour | 100 | 1.35 / 1.79 | 1.48 / 1.90 | 1.12 / 1.55 |
| E grouped per colour | 1000 | 8.44 / 10.41 | 7.18 / 9.09 | 5.71 / 7.12 |
| E grouped per colour | 5000 | 33.39 / 39.31 | 37.71 / 53.63 | 25.38 / 29.86 |
| F cached realisation+cull | 10 | 1.01 / 1.28 | 0.67 / 0.94 | 0.49 / 0.62 |
| F cached realisation+cull | 100 | 4.49 / 5.24 | 1.66 / 2.04 | 0.70 / 0.86 |
| F cached realisation+cull | 1000 | 36.26 / 39.82 | 8.56 / 9.86 | 1.71 / 2.21 |
| F cached realisation+cull | 5000 | 185.24 / 201.81 | 39.47 / 43.20 | 4.25 / 5.01 |
| G raster cache (blit) | 10 | 0.64 / 0.86 | 0.71 / 0.97 | 0.58 / 0.75 |
| G raster cache (blit) | 100 | 0.58 / 0.73 | 0.71 / 1.04 | 0.62 / 0.84 |
| G raster cache (blit) | 1000 | 0.82 / 1.19 | 0.92 / 1.25 | 0.65 / 0.83 |
| G raster cache (blit) | 5000 | 0.78 / 1.39 | 1.21 / 2.27 | 0.82 / 0.97 |

#### highlight, RTX 5080 Laptop (dGPU): frame p50 / p95 ms (whole frame: tiles + overlay, GPU flushed)

| strategy | N | 1x | 4x | 16x |
|---|---|---|---|---|
| H0 SourceOver rects | 10 | 0.11 / 0.34 | 0.18 / 0.52 | 0.11 / 0.42 |
| H0 SourceOver rects | 100 | 0.22 / 0.49 | 0.24 / 0.60 | 0.49 / 0.78 |
| H0 SourceOver rects | 1000 | 1.21 / 1.57 | 1.24 / 1.68 | 2.15 / 2.84 |
| H0 SourceOver rects | 5000 | 4.12 / 5.95 | 5.83 / 8.54 | 10.54 / 14.03 |
| H1 Multiply (2 RTs) | 10 | 0.22 / 0.62 | 0.42 / 0.92 | 0.25 / 0.72 |
| H1 Multiply (2 RTs) | 100 | 0.41 / 0.72 | 0.40 / 0.88 | 0.68 / 1.14 |
| H1 Multiply (2 RTs) | 1000 | 1.37 / 1.76 | 1.41 / 1.94 | 2.42 / 3.52 |
| H1 Multiply (2 RTs) | 5000 | 4.95 / 7.05 | 6.03 / 9.51 | 12.20 / 15.54 |
| H3 Multiply (retained) | 10 | 0.36 / 0.72 | 0.55 / 0.95 | 0.38 / 0.83 |
| H3 Multiply (retained) | 100 | 0.33 / 0.79 | 0.47 / 0.96 | 0.74 / 1.17 |
| H3 Multiply (retained) | 1000 | 0.90 / 1.35 | 1.29 / 1.84 | 1.71 / 2.43 |
| H3 Multiply (retained) | 5000 | 3.66 / 4.42 | 5.22 / 7.92 | 7.93 / 11.47 |

#### highlight, Intel Graphics (iGPU): frame p50 / p95 ms (whole frame: tiles + overlay, GPU flushed)

| strategy | N | 1x | 4x | 16x |
|---|---|---|---|---|
| H0 SourceOver rects | 10 | 0.83 / 1.08 | 1.30 / 1.81 | 0.92 / 1.27 |
| H0 SourceOver rects | 100 | 0.96 / 1.14 | 1.52 / 2.19 | 1.09 / 1.43 |
| H0 SourceOver rects | 1000 | 2.22 / 3.24 | 3.07 / 3.98 | 3.23 / 4.14 |
| H0 SourceOver rects | 5000 | 7.78 / 10.05 | 8.19 / 9.82 | 13.95 / 15.42 |
| H1 Multiply (2 RTs) | 10 | 1.48 / 1.86 | 1.76 / 2.48 | 1.42 / 2.00 |
| H1 Multiply (2 RTs) | 100 | 1.54 / 1.75 | 2.45 / 3.26 | 1.47 / 2.01 |
| H1 Multiply (2 RTs) | 1000 | 2.90 / 3.80 | 3.03 / 3.96 | 3.26 / 4.40 |
| H1 Multiply (2 RTs) | 5000 | 8.61 / 10.63 | 7.55 / 8.77 | 14.35 / 17.60 |
| H3 Multiply (retained) | 10 | 1.84 / 2.37 | 2.02 / 2.70 | 1.68 / 2.47 |
| H3 Multiply (retained) | 100 | 1.69 / 1.87 | 2.21 / 3.07 | 1.59 / 2.20 |
| H3 Multiply (retained) | 1000 | 2.80 / 3.58 | 2.46 / 3.03 | 2.67 / 3.42 |
| H3 Multiply (retained) | 5000 | 6.15 / 8.21 | 6.32 / 7.57 | 7.63 / 9.17 |

#### highlight, WARP (software): frame p50 / p95 ms (whole frame: tiles + overlay, GPU flushed)

| strategy | N | 1x | 4x | 16x |
|---|---|---|---|---|
| H0 SourceOver rects | 10 | 0.52 / 0.66 | 0.49 / 0.73 | 0.53 / 0.65 |
| H0 SourceOver rects | 100 | 0.73 / 0.94 | 0.84 / 1.07 | 0.69 / 0.91 |
| H0 SourceOver rects | 1000 | 2.50 / 2.93 | 2.23 / 2.90 | 1.35 / 1.69 |
| H0 SourceOver rects | 5000 | 10.47 / 12.25 | 9.48 / 48.73 | 6.52 / 8.04 |
| H1 Multiply (2 RTs) | 10 | 3.69 / 5.15 | 3.52 / 4.81 | 3.16 / 3.99 |
| H1 Multiply (2 RTs) | 100 | 4.33 / 5.85 | 3.76 / 5.66 | 4.05 / 5.52 |
| H1 Multiply (2 RTs) | 1000 | 6.04 / 7.70 | 5.25 / 6.97 | 3.50 / 4.61 |
| H1 Multiply (2 RTs) | 5000 | 12.81 / 15.94 | 7.64 / 9.40 | 5.71 / 6.63 |
| H3 Multiply (retained) | 10 | 4.18 / 5.98 | 5.51 / 7.74 | 5.52 / 6.83 |
| H3 Multiply (retained) | 100 | 4.39 / 6.08 | 5.42 / 7.41 | 6.12 / 7.78 |
| H3 Multiply (retained) | 1000 | 6.10 / 8.31 | 7.79 / 13.54 | 5.23 / 6.54 |
| H3 Multiply (retained) | 5000 | 12.49 / 15.15 | 8.10 / 9.31 | 6.22 / 7.65 |

#### mixed, RTX 5080 Laptop (dGPU): frame p50 / p95 ms (whole frame: tiles + overlay, GPU flushed)

| strategy | N | 1x | 4x | 16x |
|---|---|---|---|---|
| H3 + B | 100 | 2.75 / 3.31 | 1.40 / 1.82 | 1.04 / 1.48 |
| H3 + B | 1000 | 25.02 / 33.78 | 7.75 / 11.46 | 3.56 / 4.75 |

#### mixed, Intel Graphics (iGPU): frame p50 / p95 ms (whole frame: tiles + overlay, GPU flushed)

| strategy | N | 1x | 4x | 16x |
|---|---|---|---|---|
| H3 + B | 100 | 4.45 / 6.48 | 3.45 / 4.45 | 1.78 / 2.37 |
| H3 + B | 1000 | 28.63 / 37.79 | 10.23 / 12.59 | 4.17 / 4.81 |

#### mixed, WARP (software): frame p50 / p95 ms (whole frame: tiles + overlay, GPU flushed)

| strategy | N | 1x | 4x | 16x |
|---|---|---|---|---|
| H3 + B | 100 | 5.74 / 8.10 | 6.08 / 8.46 | 6.63 / 8.53 |
| H3 + B | 1000 | 11.58 / 14.70 | 11.00 / 17.69 | 5.69 / 7.04 |

#### floors (tiles only, p50 ms) and empty frame + flush

- dgpu 1x: tiles only 0.07 ms; empty frame + flush 0.050 ms
- dgpu 4x: tiles only 0.17 ms; empty frame + flush 0.127 ms
- dgpu 16x: tiles only 0.10 ms; empty frame + flush 0.064 ms
- igpu 1x: tiles only 0.90 ms; empty frame + flush 0.559 ms
- igpu 4x: tiles only 1.10 ms; empty frame + flush 0.554 ms
- igpu 16x: tiles only 0.83 ms; empty frame + flush 0.422 ms
- warp 1x: tiles only 0.45 ms; empty frame + flush 0.314 ms
- warp 4x: tiles only 0.57 ms; empty frame + flush 0.334 ms
- warp 16x: tiles only 0.49 ms; empty frame + flush 0.298 ms
