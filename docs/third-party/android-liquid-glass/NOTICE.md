# AndroidLiquidGlass attribution

Lens profile, rounded-rectangle gradient, saturation coefficients and material values
are adapted from [Kyant0/AndroidLiquidGlass](https://github.com/Kyant0/AndroidLiquidGlass),
commit `65ab177e90e5c1d8c62e70cf7755841982da65f6`, Copyright 2025 Kyant.
Licensed under Apache License 2.0; the complete license is in `LICENSE` beside this file.

Original sources: `backdrop/src/commonMain/kotlin/com/kyant/backdrop/internal/Shaders.kt`,
`effects/Lens.kt`, `effects/ColorFilter.kt`, `highlight/HighlightStyle.kt`, and
`app/src/commonMain/kotlin/com/kyant/backdrop/catalog/components/LiquidBottomTabs.kt`.

Modifications: rewritten for C# / WPF; existing Squircle/continuous SDF outline;
cached CPU mapping; three-box Gaussian approximation instead of RenderEffect;
existing capture exclusion and separate foreground composition. Optional upstream
chromatic aberration is excluded. Existing external window shadows are retained.
The container-transition path uses the same saturation/blur/tint body with an unmasked
desktop texture and a WPF pixel shader for the live lens gradient, displacement,
highlight and shade. The moving corner distance uses an analytic approximation;
the shared WPF Squircle clip owns the exact visible outline. The resting CPU material
retains its original distance calculation.
