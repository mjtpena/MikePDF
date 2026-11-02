
### m-chunk-4KB-pad30MB.csv
# label=chunk-4KB-pad30MB mode=warm cx=256 runs=20 pdf=C:\Users\mjtpe\dev\Orqentis\QuirePDF\.claude\worktrees\agent-a20eab59cba996f96\spikes\S6b\out\pdfs\pad30mb.pdf
n=20 ok=20
| column | p50 | p95 | p99 | max | min |
|---|---|---|---|---|---|
| total_ms | 153.82 | 199.81 | 357.87 | 397.39 | 140.71 |
| copy_ms | 58.58 | 65.20 | 69.81 | 70.96 | 54.27 |
| connect_ms | 0.54 | 8.65 | 123.40 | 152.09 | 0.45 |
| open_rt_ms | 76.37 | 98.99 | 126.04 | 132.80 | 61.51 |
| render_rt_ms | 2.16 | 4.87 | 8.87 | 9.87 | 1.86 |
| close_ms | 10.79 | 13.80 | 27.86 | 31.37 | 9.01 |
| convert_ms | 0.21 | 0.24 | 0.26 | 0.26 | 0.17 |
| srv_spawn_us | 18763.50 | 25311.30 | 51657.46 | 58244.00 | 16813.00 |
| srv_open_us | 3556.50 | 5407.15 | 13389.43 | 15385.00 | 3079.00 |
| srv_render_us | 1745.50 | 4390.70 | 6559.74 | 7102.00 | 1499.00 |
| srv_out_us | 226.00 | 457.05 | 989.81 | 1123.00 | 204.00 |
| srv_stage_us | 52384.00 | 64165.00 | 67782.60 | 68687.00 | 37429.00 |

### m-chunk-64KB-pad30MB.csv
# label=chunk-64KB-pad30MB mode=warm cx=256 runs=20 pdf=C:\Users\mjtpe\dev\Orqentis\QuirePDF\.claude\worktrees\agent-a20eab59cba996f96\spikes\S6b\out\pdfs\pad30mb.pdf
n=20 ok=20
| column | p50 | p95 | p99 | max | min |
|---|---|---|---|---|---|
| total_ms | 139.81 | 221.10 | 331.47 | 359.06 | 114.24 |
| copy_ms | 25.80 | 27.57 | 29.76 | 30.31 | 23.74 |
| connect_ms | 0.58 | 7.63 | 108.01 | 133.11 | 0.48 |
| open_rt_ms | 88.59 | 154.21 | 166.49 | 169.56 | 69.69 |
| render_rt_ms | 2.25 | 3.09 | 7.91 | 9.12 | 2.01 |
| close_ms | 12.47 | 15.61 | 29.19 | 32.58 | 10.93 |
| convert_ms | 0.25 | 0.39 | 0.48 | 0.50 | 0.20 |
| srv_spawn_us | 19813.00 | 25178.75 | 49859.75 | 56030.00 | 18162.00 |
| srv_open_us | 3839.00 | 5130.05 | 12122.81 | 13871.00 | 3339.00 |
| srv_render_us | 1848.00 | 2278.70 | 5648.54 | 6491.00 | 1670.00 |
| srv_out_us | 235.50 | 459.10 | 551.82 | 575.00 | 204.00 |
| srv_stage_us | 61173.50 | 96420.20 | 133860.84 | 143221.00 | 45219.00 |

### m-cold-aot-256.csv
# label=cold-aot-256 mode=cold cx=256 runs=30 pdf=C:\Users\mjtpe\dev\Orqentis\QuirePDF\.claude\worktrees\agent-a20eab59cba996f96\spikes\S6b\out\pdfs\text1.pdf
n=30 ok=30
| column | p50 | p95 | p99 | max | min |
|---|---|---|---|---|---|
| total_ms | 110.07 | 139.23 | 145.71 | 148.31 | 88.69 |
| copy_ms | 0.08 | 0.12 | 0.13 | 0.13 | 0.06 |
| connect_ms | 68.28 | 89.33 | 97.09 | 99.46 | 51.90 |
| open_rt_ms | 29.46 | 38.43 | 49.30 | 53.28 | 23.03 |
| render_rt_ms | 2.12 | 2.57 | 2.72 | 2.75 | 1.73 |
| close_ms | 7.77 | 10.64 | 10.82 | 10.84 | 6.03 |
| convert_ms | 0.23 | 0.27 | 0.28 | 0.28 | 0.18 |
| srv_spawn_us | 24582.00 | 33105.70 | 43761.72 | 47744.00 | 19006.00 |
| srv_open_us | 3449.50 | 4775.40 | 5088.10 | 5207.00 | 2903.00 |
| srv_render_us | 1747.00 | 2149.40 | 2288.40 | 2329.00 | 1444.00 |
| srv_out_us | 228.00 | 303.20 | 305.71 | 306.00 | 186.00 |
| srv_stage_us | 625.50 | 827.25 | 933.88 | 971.00 | 411.00 |

### m-cold-fresh-256.csv
# label=cold-fresh-256 mode=cold cx=256 runs=30 pdf=C:\Users\mjtpe\dev\Orqentis\QuirePDF\.claude\worktrees\agent-a20eab59cba996f96\spikes\S6b\out\pdfs\text1.pdf
n=30 ok=30
| column | p50 | p95 | p99 | max | min |
|---|---|---|---|---|---|
| total_ms | 273.59 | 331.13 | 341.90 | 345.35 | 238.01 |
| copy_ms | 0.08 | 0.11 | 0.13 | 0.14 | 0.07 |
| connect_ms | 135.75 | 176.46 | 189.56 | 192.32 | 114.47 |
| open_rt_ms | 96.73 | 108.29 | 119.46 | 123.91 | 81.82 |
| render_rt_ms | 9.93 | 12.08 | 13.35 | 13.57 | 8.33 |
| close_ms | 30.02 | 34.71 | 37.26 | 38.25 | 25.99 |
| convert_ms | 0.23 | 0.27 | 0.29 | 0.30 | 0.19 |
| srv_spawn_us | 62617.00 | 68966.20 | 76275.68 | 79062.00 | 51305.00 |
| srv_open_us | 15107.50 | 17888.35 | 19093.87 | 19528.00 | 12384.00 |
| srv_render_us | 6952.00 | 8122.05 | 8549.70 | 8715.00 | 5553.00 |
| srv_out_us | 481.50 | 713.45 | 794.19 | 820.00 | 387.00 |
| srv_stage_us | 1685.00 | 2570.20 | 2884.65 | 3005.00 | 1396.00 |

### m-cold-spare-256.csv
# label=cold-spare-256 mode=cold cx=256 runs=30 pdf=C:\Users\mjtpe\dev\Orqentis\QuirePDF\.claude\worktrees\agent-a20eab59cba996f96\spikes\S6b\out\pdfs\text1.pdf
n=30 ok=30
| column | p50 | p95 | p99 | max | min |
|---|---|---|---|---|---|
| total_ms | 252.68 | 363.53 | 411.45 | 430.24 | 213.52 |
| copy_ms | 0.07 | 0.10 | 0.14 | 0.15 | 0.06 |
| connect_ms | 167.16 | 243.56 | 266.77 | 275.35 | 132.60 |
| open_rt_ms | 45.53 | 80.22 | 86.67 | 86.95 | 39.82 |
| render_rt_ms | 9.94 | 16.67 | 17.58 | 17.67 | 8.46 |
| close_ms | 30.26 | 47.53 | 52.18 | 53.75 | 24.98 |
| convert_ms | 0.23 | 0.29 | 0.33 | 0.34 | 0.19 |
| srv_spawn_us | 9727.00 | 23156.95 | 29924.65 | 31930.00 | 7833.00 |
| srv_open_us | 16037.00 | 23513.15 | 23963.25 | 24130.00 | 13120.00 |
| srv_render_us | 7057.00 | 11580.95 | 12103.59 | 12286.00 | 5949.00 |
| srv_out_us | 480.00 | 810.85 | 830.81 | 834.00 | 396.00 |
| srv_stage_us | 1685.50 | 3167.90 | 3432.08 | 3533.00 | 1368.00 |

### m-inline-1024.csv
# label=inline-1024 mode=warm cx=1024 runs=100 pdf=C:\Users\mjtpe\dev\Orqentis\QuirePDF\.claude\worktrees\agent-a20eab59cba996f96\spikes\S6b\out\pdfs\text1.pdf
n=100 ok=100
| column | p50 | p95 | p99 | max | min |
|---|---|---|---|---|---|
| total_ms | 52.37 | 71.42 | 88.43 | 287.66 | 44.42 |
| copy_ms | 0.07 | 0.10 | 0.14 | 0.17 | 0.05 |
| connect_ms | 0.45 | 1.01 | 3.26 | 140.94 | 0.33 |
| open_rt_ms | 27.29 | 38.48 | 59.07 | 96.04 | 22.24 |
| render_rt_ms | 10.46 | 12.94 | 14.43 | 15.46 | 8.37 |
| close_ms | 9.21 | 13.32 | 15.51 | 29.98 | 7.08 |
| convert_ms | 1.90 | 2.37 | 2.56 | 5.43 | 0.92 |
| srv_spawn_us | 20969.50 | 28987.85 | 47143.88 | 62180.00 | 17680.00 |
| srv_open_us | 4099.00 | 5265.30 | 6053.83 | 13462.00 | 3382.00 |
| srv_render_us | 5212.50 | 6566.00 | 6925.42 | 9046.00 | 3999.00 |
| srv_out_us | 3855.00 | 4801.15 | 5705.36 | 5939.00 | 2610.00 |
| srv_stage_us | 645.50 | 1765.85 | 2914.02 | 16182.00 | 453.00 |

### m-inline-256.csv
# label=inline-256 mode=warm cx=256 runs=100 pdf=C:\Users\mjtpe\dev\Orqentis\QuirePDF\.claude\worktrees\agent-a20eab59cba996f96\spikes\S6b\out\pdfs\text1.pdf
n=100 ok=100
| column | p50 | p95 | p99 | max | min |
|---|---|---|---|---|---|
| total_ms | 38.05 | 45.82 | 56.74 | 262.56 | 32.59 |
| copy_ms | 0.07 | 0.09 | 0.47 | 0.91 | 0.05 |
| connect_ms | 0.45 | 0.81 | 3.62 | 128.00 | 0.31 |
| open_rt_ms | 26.40 | 31.88 | 44.27 | 92.32 | 22.67 |
| render_rt_ms | 2.35 | 4.02 | 5.69 | 9.75 | 1.95 |
| close_ms | 7.45 | 9.57 | 10.30 | 30.06 | 6.09 |
| convert_ms | 0.12 | 0.14 | 0.16 | 0.16 | 0.10 |
| srv_spawn_us | 20440.50 | 25050.05 | 38731.46 | 57191.00 | 17860.00 |
| srv_open_us | 3858.00 | 5108.00 | 5865.33 | 16887.00 | 3205.00 |
| srv_render_us | 1850.00 | 2232.75 | 2528.95 | 6682.00 | 1551.00 |
| srv_out_us | 204.50 | 1804.85 | 2091.83 | 3362.00 | 137.00 |
| srv_stage_us | 549.50 | 992.10 | 1622.02 | 1624.00 | 397.00 |

### m-input-encrypted.pdf.csv
# label=input-encrypted.pdf mode=warm cx=256 runs=3 pdf=C:\Users\mjtpe\dev\Orqentis\QuirePDF\.claude\worktrees\agent-a20eab59cba996f96\spikes\S6b\out\pdfs\encrypted.pdf
n=3 ok=0
| column | p50 | p95 | p99 | max | min |
|---|---|---|---|---|---|

### m-input-js.pdf.csv
# label=input-js.pdf mode=warm cx=256 runs=3 pdf=C:\Users\mjtpe\dev\Orqentis\QuirePDF\.claude\worktrees\agent-a20eab59cba996f96\spikes\S6b\out\pdfs\js.pdf
n=3 ok=3
| column | p50 | p95 | p99 | max | min |
|---|---|---|---|---|---|
| total_ms | 33.71 | 252.14 | 271.56 | 276.41 | 32.27 |
| copy_ms | 0.08 | 0.11 | 0.11 | 0.11 | 0.07 |
| connect_ms | 0.80 | 126.04 | 137.18 | 139.96 | 0.44 |
| open_rt_ms | 24.04 | 88.14 | 93.84 | 95.26 | 21.16 |
| render_rt_ms | 0.74 | 7.69 | 8.31 | 8.46 | 0.73 |
| close_ms | 7.57 | 27.84 | 29.64 | 30.09 | 7.17 |
| convert_ms | 0.27 | 0.28 | 0.28 | 0.28 | 0.25 |
| srv_spawn_us | 20870.00 | 60468.20 | 63988.04 | 64868.00 | 19172.00 |
| srv_open_us | 1109.00 | 10435.70 | 11264.74 | 11472.00 | 762.00 |
| srv_render_us | 370.00 | 5233.60 | 5665.92 | 5774.00 | 367.00 |
| srv_out_us | 221.00 | 438.80 | 458.16 | 463.00 | 217.00 |
| srv_stage_us | 641.00 | 1696.70 | 1790.54 | 1814.00 | 472.00 |

### m-input-notpdf.pdf.csv
# label=input-notpdf.pdf mode=warm cx=256 runs=3 pdf=C:\Users\mjtpe\dev\Orqentis\QuirePDF\.claude\worktrees\agent-a20eab59cba996f96\spikes\S6b\out\pdfs\notpdf.pdf
n=3 ok=0
| column | p50 | p95 | p99 | max | min |
|---|---|---|---|---|---|

### m-input-truncated.pdf.csv
# label=input-truncated.pdf mode=warm cx=256 runs=3 pdf=C:\Users\mjtpe\dev\Orqentis\QuirePDF\.claude\worktrees\agent-a20eab59cba996f96\spikes\S6b\out\pdfs\truncated.pdf
n=3 ok=0
| column | p50 | p95 | p99 | max | min |
|---|---|---|---|---|---|

### m-input-zero.pdf.csv
# label=input-zero.pdf mode=warm cx=256 runs=3 pdf=C:\Users\mjtpe\dev\Orqentis\QuirePDF\.claude\worktrees\agent-a20eab59cba996f96\spikes\S6b\out\pdfs\zero.pdf
n=3 ok=0
| column | p50 | p95 | p99 | max | min |
|---|---|---|---|---|---|

### m-mem-aot-256.csv
# label=mem-aot-256 mode=warm cx=256 runs=100 pdf=C:\Users\mjtpe\dev\Orqentis\QuirePDF\.claude\worktrees\agent-a20eab59cba996f96\spikes\S6b\out\pdfs\text20.pdf
n=100 ok=100
| column | p50 | p95 | p99 | max | min |
|---|---|---|---|---|---|
| total_ms | 33.48 | 41.06 | 56.33 | 112.90 | 30.56 |
| copy_ms | 0.10 | 0.13 | 0.18 | 0.19 | 0.08 |
| connect_ms | 0.42 | 0.66 | 1.67 | 66.57 | 0.29 |
| open_rt_ms | 23.30 | 29.22 | 39.72 | 44.51 | 21.13 |
| render_rt_ms | 1.93 | 2.37 | 3.82 | 4.31 | 1.68 |
| close_ms | 6.22 | 7.51 | 9.66 | 10.26 | 5.36 |
| convert_ms | 0.20 | 0.26 | 0.32 | 0.51 | 0.16 |
| srv_spawn_us | 18620.00 | 22839.25 | 35275.60 | 39790.00 | 16849.00 |
| srv_open_us | 3355.50 | 4068.95 | 5298.09 | 6990.00 | 2845.00 |
| srv_render_us | 1622.00 | 2058.10 | 3526.98 | 4020.00 | 1401.00 |
| srv_out_us | 208.00 | 250.00 | 277.32 | 309.00 | 185.00 |
| srv_stage_us | 582.50 | 845.10 | 1071.09 | 1179.00 | 444.00 |

### m-mem-fresh-1024.csv
# label=mem-fresh-1024 mode=warm cx=1024 runs=50 pdf=C:\Users\mjtpe\dev\Orqentis\QuirePDF\.claude\worktrees\agent-a20eab59cba996f96\spikes\S6b\out\pdfs\text20.pdf
n=50 ok=50
| column | p50 | p95 | p99 | max | min |
|---|---|---|---|---|---|
| total_ms | 63.72 | 72.09 | 195.27 | 308.59 | 56.76 |
| copy_ms | 0.10 | 0.14 | 0.32 | 0.47 | 0.09 |
| connect_ms | 0.41 | 0.64 | 71.87 | 138.81 | 0.30 |
| open_rt_ms | 25.66 | 31.35 | 59.83 | 85.38 | 22.86 |
| render_rt_ms | 24.05 | 27.36 | 37.99 | 47.20 | 20.69 |
| close_ms | 8.59 | 10.93 | 21.97 | 30.94 | 7.12 |
| convert_ms | 3.41 | 3.82 | 3.88 | 3.94 | 2.61 |
| srv_spawn_us | 20043.50 | 23955.50 | 40059.66 | 54939.00 | 17754.00 |
| srv_open_us | 3849.00 | 4650.55 | 10940.69 | 13792.00 | 3315.00 |
| srv_render_us | 4903.50 | 7689.95 | 9242.56 | 10446.00 | 4160.00 |
| srv_out_us | 3440.50 | 3946.80 | 5334.75 | 6621.00 | 3064.00 |
| srv_stage_us | 602.50 | 1423.90 | 2250.22 | 2408.00 | 455.00 |

### m-mem-fresh-256.csv
# label=mem-fresh-256 mode=warm cx=256 runs=100 pdf=C:\Users\mjtpe\dev\Orqentis\QuirePDF\.claude\worktrees\agent-a20eab59cba996f96\spikes\S6b\out\pdfs\text20.pdf
n=100 ok=100
| column | p50 | p95 | p99 | max | min |
|---|---|---|---|---|---|
| total_ms | 52.97 | 66.25 | 76.90 | 290.71 | 45.45 |
| copy_ms | 0.10 | 0.13 | 0.15 | 0.15 | 0.08 |
| connect_ms | 0.41 | 0.58 | 2.16 | 127.17 | 0.31 |
| open_rt_ms | 25.44 | 34.99 | 45.73 | 90.95 | 21.99 |
| render_rt_ms | 17.73 | 19.97 | 20.94 | 41.93 | 14.75 |
| close_ms | 7.71 | 9.51 | 26.41 | 28.45 | 6.34 |
| convert_ms | 0.23 | 0.29 | 0.73 | 1.37 | 0.18 |
| srv_spawn_us | 19781.50 | 25114.85 | 40162.34 | 59204.00 | 16854.00 |
| srv_open_us | 3704.00 | 4619.05 | 8156.69 | 14462.00 | 3244.00 |
| srv_render_us | 1786.00 | 2329.05 | 4604.95 | 6976.00 | 1510.00 |
| srv_out_us | 228.00 | 304.55 | 362.23 | 682.00 | 189.00 |
| srv_stage_us | 560.00 | 910.55 | 1192.77 | 1764.00 | 419.00 |

### m-preview-cold.csv
# label=preview-cold preview mode=cold runs=15 pdf=C:\Users\mjtpe\dev\Orqentis\QuirePDF\.claude\worktrees\agent-a20eab59cba996f96\spikes\S6b\out\pdfs\text1.pdf
n=15 ok=15
| column | p50 | p95 | p99 | max | min |
|---|---|---|---|---|---|
| first_pixel_ms | 291.65 | 371.62 | 424.51 | 437.73 | 263.72 |
| do_preview_call_ms | 1.05 | 1.90 | 2.60 | 2.77 | 0.85 |

### m-preview-warm.csv
# label=preview-warm preview mode=warm runs=30 pdf=C:\Users\mjtpe\dev\Orqentis\QuirePDF\.claude\worktrees\agent-a20eab59cba996f96\spikes\S6b\out\pdfs\text1.pdf
n=30 ok=30
| column | p50 | p95 | p99 | max | min |
|---|---|---|---|---|---|
| first_pixel_ms | 51.88 | 72.26 | 290.04 | 378.23 | 40.07 |
| do_preview_call_ms | 0.93 | 2.11 | 2.38 | 2.46 | 0.76 |

### m-size-pad30MB.csv
# label=size-pad30MB mode=warm cx=256 runs=30 pdf=C:\Users\mjtpe\dev\Orqentis\QuirePDF\.claude\worktrees\agent-a20eab59cba996f96\spikes\S6b\out\pdfs\pad30mb.pdf
n=30 ok=30
| column | p50 | p95 | p99 | max | min |
|---|---|---|---|---|---|
| total_ms | 120.39 | 149.78 | 280.90 | 331.04 | 98.60 |
| copy_ms | 22.81 | 25.27 | 25.86 | 26.05 | 20.43 |
| connect_ms | 0.54 | 0.61 | 94.30 | 132.57 | 0.45 |
| open_rt_ms | 75.72 | 108.43 | 119.92 | 121.52 | 58.95 |
| render_rt_ms | 2.17 | 2.72 | 7.21 | 9.04 | 1.90 |
| close_ms | 11.54 | 13.17 | 26.34 | 31.60 | 9.30 |
| convert_ms | 0.22 | 0.25 | 0.30 | 0.32 | 0.18 |
| srv_spawn_us | 19042.50 | 30621.85 | 49929.68 | 55326.00 | 16758.00 |
| srv_open_us | 3767.50 | 4417.25 | 10810.59 | 13342.00 | 3119.00 |
| srv_render_us | 1783.50 | 2299.00 | 5199.14 | 6369.00 | 1517.00 |
| srv_out_us | 221.00 | 271.30 | 392.57 | 441.00 | 199.00 |
| srv_stage_us | 50587.00 | 74463.50 | 87415.71 | 91969.00 | 35557.00 |

### m-size-pad40MB-over-cap.csv
# label=size-pad40MB-over-cap mode=warm cx=256 runs=3 pdf=C:\Users\mjtpe\dev\Orqentis\QuirePDF\.claude\worktrees\agent-a20eab59cba996f96\spikes\S6b\out\pdfs\pad40mb.pdf
n=3 ok=0
| column | p50 | p95 | p99 | max | min |
|---|---|---|---|---|---|

### m-size-pad5MB.csv
# label=size-pad5MB mode=warm cx=256 runs=50 pdf=C:\Users\mjtpe\dev\Orqentis\QuirePDF\.claude\worktrees\agent-a20eab59cba996f96\spikes\S6b\out\pdfs\pad5mb.pdf
n=50 ok=50
| column | p50 | p95 | p99 | max | min |
|---|---|---|---|---|---|
| total_ms | 47.38 | 63.20 | 171.00 | 266.13 | 40.47 |
| copy_ms | 3.96 | 4.82 | 5.25 | 5.56 | 3.28 |
| connect_ms | 0.50 | 0.95 | 62.92 | 122.23 | 0.39 |
| open_rt_ms | 30.41 | 44.30 | 70.94 | 91.40 | 25.30 |
| render_rt_ms | 2.41 | 4.43 | 6.72 | 8.78 | 1.91 |
| close_ms | 8.00 | 11.28 | 20.69 | 29.60 | 6.52 |
| convert_ms | 0.23 | 0.33 | 0.51 | 0.54 | 0.19 |
| srv_spawn_us | 20189.50 | 29407.25 | 46249.94 | 54332.00 | 16636.00 |
| srv_open_us | 3823.50 | 5130.10 | 10685.43 | 15141.00 | 3045.00 |
| srv_render_us | 1906.50 | 3495.75 | 5262.91 | 6361.00 | 1487.00 |
| srv_out_us | 275.50 | 462.90 | 913.14 | 1018.00 | 205.00 |
| srv_stage_us | 4657.00 | 6890.90 | 9518.38 | 10272.00 | 3940.00 |

### m-size-text20-65KB.csv
# label=size-text20-65KB mode=warm cx=256 runs=100 pdf=C:\Users\mjtpe\dev\Orqentis\QuirePDF\.claude\worktrees\agent-a20eab59cba996f96\spikes\S6b\out\pdfs\text20.pdf
n=100 ok=100
| column | p50 | p95 | p99 | max | min |
|---|---|---|---|---|---|
| total_ms | 35.33 | 41.56 | 122.09 | 292.19 | 30.68 |
| copy_ms | 0.10 | 0.12 | 0.19 | 0.21 | 0.08 |
| connect_ms | 0.41 | 0.64 | 2.31 | 141.89 | 0.31 |
| open_rt_ms | 23.98 | 27.74 | 98.67 | 109.48 | 21.38 |
| render_rt_ms | 2.12 | 2.72 | 8.39 | 10.05 | 1.78 |
| close_ms | 7.01 | 9.13 | 23.58 | 29.60 | 5.95 |
| convert_ms | 0.21 | 0.25 | 0.47 | 0.47 | 0.18 |
| srv_spawn_us | 18788.50 | 22169.25 | 64234.33 | 101491.00 | 16585.00 |
| srv_open_us | 3577.00 | 4153.90 | 6034.45 | 15583.00 | 3154.00 |
| srv_render_us | 1725.00 | 2256.70 | 4044.54 | 7068.00 | 1435.00 |
| srv_out_us | 220.50 | 296.25 | 541.44 | 1080.00 | 192.00 |
| srv_stage_us | 556.50 | 856.15 | 1330.82 | 1709.00 | 442.00 |

### m-warm-aot-256.csv
# label=warm-aot-256 mode=warm cx=256 runs=100 pdf=C:\Users\mjtpe\dev\Orqentis\QuirePDF\.claude\worktrees\agent-a20eab59cba996f96\spikes\S6b\out\pdfs\text1.pdf
n=100 ok=100
| column | p50 | p95 | p99 | max | min |
|---|---|---|---|---|---|
| total_ms | 38.85 | 69.48 | 115.04 | 161.38 | 33.57 |
| copy_ms | 0.08 | 0.12 | 0.16 | 0.17 | 0.06 |
| connect_ms | 0.52 | 0.98 | 1.92 | 69.55 | 0.32 |
| open_rt_ms | 26.77 | 48.94 | 76.84 | 129.05 | 23.62 |
| render_rt_ms | 2.21 | 3.00 | 4.41 | 4.76 | 1.84 |
| close_ms | 7.45 | 11.28 | 18.27 | 25.33 | 6.03 |
| convert_ms | 0.23 | 0.32 | 0.58 | 1.08 | 0.19 |
| srv_spawn_us | 21491.00 | 43265.40 | 70676.07 | 122064.00 | 18855.00 |
| srv_open_us | 3696.50 | 4857.40 | 5290.42 | 5332.00 | 3114.00 |
| srv_render_us | 1854.50 | 2503.95 | 4023.00 | 4419.00 | 1548.00 |
| srv_out_us | 238.00 | 306.00 | 330.70 | 400.00 | 197.00 |
| srv_stage_us | 638.50 | 1101.40 | 1663.71 | 2724.00 | 441.00 |

### m-warm-fresh-1024.csv
# label=warm-fresh-1024 mode=warm cx=1024 runs=100 pdf=C:\Users\mjtpe\dev\Orqentis\QuirePDF\.claude\worktrees\agent-a20eab59cba996f96\spikes\S6b\out\pdfs\text1.pdf
n=100 ok=100
| column | p50 | p95 | p99 | max | min |
|---|---|---|---|---|---|
| total_ms | 44.15 | 52.28 | 61.39 | 296.23 | 39.61 |
| copy_ms | 0.07 | 0.08 | 0.10 | 0.22 | 0.05 |
| connect_ms | 0.41 | 0.76 | 2.66 | 150.71 | 0.30 |
| open_rt_ms | 23.18 | 28.39 | 40.15 | 94.30 | 20.24 |
| render_rt_ms | 8.17 | 9.46 | 10.28 | 16.40 | 6.96 |
| close_ms | 7.71 | 9.60 | 12.53 | 28.92 | 6.72 |
| convert_ms | 3.17 | 3.60 | 3.77 | 3.93 | 2.86 |
| srv_spawn_us | 18270.00 | 21852.20 | 35583.90 | 61908.00 | 16073.00 |
| srv_open_us | 3502.50 | 4198.55 | 7568.66 | 14366.00 | 3072.00 |
| srv_render_us | 4584.50 | 5312.60 | 5649.58 | 10360.00 | 3773.00 |
| srv_out_us | 3342.50 | 3958.95 | 4466.42 | 5399.00 | 2812.00 |
| srv_stage_us | 451.00 | 720.90 | 890.28 | 1512.00 | 373.00 |

### m-warm-fresh-256.csv
# label=warm-fresh-256 mode=warm cx=256 runs=200 pdf=C:\Users\mjtpe\dev\Orqentis\QuirePDF\.claude\worktrees\agent-a20eab59cba996f96\spikes\S6b\out\pdfs\text1.pdf
n=200 ok=200
| column | p50 | p95 | p99 | max | min |
|---|---|---|---|---|---|
| total_ms | 37.07 | 48.03 | 55.32 | 392.45 | 31.81 |
| copy_ms | 0.07 | 0.11 | 0.15 | 0.27 | 0.05 |
| connect_ms | 0.44 | 0.86 | 1.52 | 241.15 | 0.31 |
| open_rt_ms | 25.57 | 34.60 | 44.32 | 101.54 | 22.10 |
| render_rt_ms | 2.20 | 2.83 | 3.35 | 10.32 | 1.84 |
| close_ms | 7.25 | 10.12 | 11.99 | 36.34 | 5.98 |
| convert_ms | 0.23 | 0.27 | 0.48 | 0.89 | 0.19 |
| srv_spawn_us | 20079.00 | 25908.90 | 38390.95 | 66984.00 | 16996.00 |
| srv_open_us | 3769.00 | 4876.30 | 7277.06 | 16332.00 | 3304.00 |
| srv_render_us | 1799.00 | 2343.05 | 2829.51 | 7198.00 | 1534.00 |
| srv_out_us | 229.00 | 296.00 | 467.60 | 995.00 | 189.00 |
| srv_stage_us | 543.00 | 1116.15 | 1532.08 | 1757.00 | 396.00 |

### m-warm-fresh-32.csv
# label=warm-fresh-32 mode=warm cx=32 runs=100 pdf=C:\Users\mjtpe\dev\Orqentis\QuirePDF\.claude\worktrees\agent-a20eab59cba996f96\spikes\S6b\out\pdfs\text1.pdf
n=100 ok=100
| column | p50 | p95 | p99 | max | min |
|---|---|---|---|---|---|
| total_ms | 33.60 | 42.67 | 57.12 | 223.18 | 30.15 |
| copy_ms | 0.06 | 0.08 | 0.12 | 0.17 | 0.05 |
| connect_ms | 0.40 | 0.65 | 2.11 | 109.10 | 0.31 |
| open_rt_ms | 23.35 | 30.49 | 44.57 | 79.42 | 21.25 |
| render_rt_ms | 1.77 | 2.67 | 2.99 | 7.67 | 1.35 |
| close_ms | 6.75 | 8.39 | 9.29 | 24.88 | 5.83 |
| convert_ms | 0.04 | 0.05 | 0.07 | 0.11 | 0.03 |
| srv_spawn_us | 18377.00 | 24082.65 | 38401.73 | 52433.00 | 15960.00 |
| srv_open_us | 3559.00 | 4497.60 | 6440.39 | 12518.00 | 2957.00 |
| srv_render_us | 1542.50 | 2200.30 | 2729.72 | 5573.00 | 1190.00 |
| srv_out_us | 35.00 | 59.10 | 86.39 | 224.00 | 27.00 |
| srv_stage_us | 493.00 | 894.65 | 1266.99 | 1365.00 | 382.00 |

### m-warm-fresh-96.csv
# label=warm-fresh-96 mode=warm cx=96 runs=100 pdf=C:\Users\mjtpe\dev\Orqentis\QuirePDF\.claude\worktrees\agent-a20eab59cba996f96\spikes\S6b\out\pdfs\text1.pdf
n=100 ok=100
| column | p50 | p95 | p99 | max | min |
|---|---|---|---|---|---|
| total_ms | 35.47 | 40.54 | 66.20 | 299.22 | 31.66 |
| copy_ms | 0.06 | 0.08 | 0.09 | 0.14 | 0.05 |
| connect_ms | 0.43 | 0.66 | 2.29 | 145.99 | 0.33 |
| open_rt_ms | 24.92 | 28.11 | 52.50 | 107.22 | 22.00 |
| render_rt_ms | 1.71 | 2.16 | 8.57 | 10.42 | 1.49 |
| close_ms | 7.12 | 8.28 | 16.40 | 33.25 | 6.04 |
| convert_ms | 0.07 | 0.08 | 0.08 | 0.16 | 0.06 |
| srv_spawn_us | 19565.00 | 22234.40 | 46257.58 | 69778.00 | 17166.00 |
| srv_open_us | 3752.50 | 4345.55 | 5812.81 | 16387.00 | 3237.00 |
| srv_render_us | 1495.50 | 1795.40 | 2082.32 | 7559.00 | 1315.00 |
| srv_out_us | 60.00 | 85.35 | 151.80 | 330.00 | 49.00 |
| srv_stage_us | 494.50 | 923.65 | 1929.26 | 2351.00 | 411.00 |

### m-warm-reuse-256.csv
# label=warm-reuse-256 mode=warm cx=256 runs=200 pdf=C:\Users\mjtpe\dev\Orqentis\QuirePDF\.claude\worktrees\agent-a20eab59cba996f96\spikes\S6b\out\pdfs\text1.pdf
n=200 ok=200
| column | p50 | p95 | p99 | max | min |
|---|---|---|---|---|---|
| total_ms | 8.29 | 10.59 | 12.09 | 213.09 | 6.61 |
| copy_ms | 0.06 | 0.07 | 0.08 | 0.41 | 0.04 |
| connect_ms | 0.39 | 0.65 | 0.80 | 120.16 | 0.28 |
| open_rt_ms | 4.05 | 5.43 | 7.22 | 79.31 | 3.05 |
| render_rt_ms | 1.81 | 2.34 | 3.52 | 7.34 | 1.50 |
| close_ms | 0.71 | 1.04 | 2.35 | 4.51 | 0.53 |
| convert_ms | 0.22 | 0.25 | 0.30 | 0.50 | 0.18 |
| srv_spawn_us | 6.00 | 10.05 | 22.02 | 50735.00 | 4.00 |
| srv_open_us | 2627.50 | 3327.40 | 5327.06 | 12389.00 | 2129.00 |
| srv_render_us | 1466.00 | 1792.45 | 3168.94 | 5145.00 | 1245.00 |
| srv_out_us | 185.00 | 240.20 | 363.96 | 564.00 | 146.00 |
| srv_stage_us | 467.50 | 750.45 | 1389.85 | 3223.00 | 359.00 |

### m-warm-spare-256.csv
# label=warm-spare-256 mode=warm cx=256 runs=200 pdf=C:\Users\mjtpe\dev\Orqentis\QuirePDF\.claude\worktrees\agent-a20eab59cba996f96\spikes\S6b\out\pdfs\text1.pdf
n=200 ok=200
| column | p50 | p95 | p99 | max | min |
|---|---|---|---|---|---|
| total_ms | 28.29 | 35.83 | 56.90 | 269.68 | 23.88 |
| copy_ms | 0.08 | 0.11 | 0.13 | 0.14 | 0.06 |
| connect_ms | 0.50 | 0.90 | 1.12 | 175.56 | 0.35 |
| open_rt_ms | 14.58 | 19.53 | 39.27 | 89.33 | 12.28 |
| render_rt_ms | 2.60 | 3.30 | 3.88 | 10.26 | 2.13 |
| close_ms | 8.71 | 11.13 | 14.28 | 35.29 | 7.14 |
| convert_ms | 0.26 | 0.30 | 0.34 | 1.89 | 0.20 |
| srv_spawn_us | 8010.00 | 10343.80 | 29134.39 | 83044.00 | 6748.00 |
| srv_open_us | 4305.50 | 5290.00 | 5920.27 | 17888.00 | 3590.00 |
| srv_render_us | 2117.00 | 2715.40 | 2839.50 | 7179.00 | 1721.00 |
| srv_out_us | 269.50 | 345.10 | 495.94 | 1230.00 | 214.00 |
| srv_stage_us | 635.00 | 1229.05 | 1784.17 | 1857.00 | 453.00 |
