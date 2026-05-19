# Torch Color — Research Findings

Investigated via AssetRipper (http://127.0.0.1:2501) on the live extracted Valheim assets, 2026-05-19.

---

## 1. Torch Prefab Structure

**`piece_groundtorch_wood`** (root GameObject)
- Components: `Piece`, `Fireplace`, `ZNetView`, `LODGroup`
- `Fireplace.m_enabledObject` → child GameObject that holds the live flame (no High/Low split on this torch)
- `Fireplace.m_enabledObjectLow / High` → null for the wooden ground torch

The flame child contains one or more **`fx_Torch_Basic`** GameObjects, each being a single ParticleSystem.

---

## 2. fx_Torch_Basic — Particle System

**GameObject components:** `Transform` + `ParticleSystem` + `ParticleSystemRenderer`

### Material
- Name: `flameball_flipbook_gradient`
- Path: `Assets/Effects/materials/flameball_flipbook_gradient.mat`
- Shader: **`ParticleGradientMapped_Unlit`** (custom Valheim shader)
- Material `_Color`: `(1, 1, 1, 1)` — pure white, no color baked here
- `_FlipbookMode: 1` — animated spritesheet flipbook
- `_GradientAsAlpha: 1` — the shader uses the texture's grayscale as a gradient lookup index
- `_GradientChannel: 0` — reads gradient from CustomData channel 0 (Custom1)

### Texture
- Name: `flameball_flipbook`
- Path: `Assets/Effects/textures/flameball_flipbook.png`
- Size: **256×256**, Format: DXT1
- Layout: **8×8 spritesheet** of flame shapes
- **Colour: entirely greyscale (white-on-black)** — zero orange baked in

### ColorModule (Color over Lifetime)
- Fully white RGB (`1,1,1`) — only controls **alpha fade** (fade-in at birth, fade-out at death)

### startColor (InitialModule)
- `minColor`: grey `(0.625, 0.625, 0.625, 1)` — `maxColor`: white `(1, 1, 1, 1)`
- This is a **brightness jitter only** (white noise), no hue

---

## 3. 🔑 Where the Orange Comes From — CustomDataModule

The orange flame colour is **entirely** driven by the **`CustomDataModule`** of the ParticleSystem.
The custom shader `ParticleGradientMapped_Unlit` reads two HDR colour gradients passed as per-particle vertex data and uses them as a gradient-map over the greyscale texture values.

From the ParticleSystem YAML:

```
CustomDataModule:
  mode0: 2          ← ParticleSystemCustomDataMode.Color
  color0 (Custom1):    ← "cool" body gradient
    key0 @ t=0:   {r: 2,    g: 1.22, b: 0}   ← bright yellow-orange core  (HDR)
    key1 @ t=1:   {r: 0.25, g: 0.16, b: 0}   ← dim dark amber edge

  mode1: 2          ← ParticleSystemCustomDataMode.Color
  color1 (Custom2):    ← "hot" core gradient
    key0 @ t=0:   {r: 2,    g: 0,    b: 0}   ← bright red core  (HDR)
    key1 @ t=1:   {r: 1,    g: 0.30, b: 0}   ← orange-red edge
```

- Values **> 1.0** are intentional HDR — they drive Valheim's post-process bloom.
- `color0` feeds into the shader's "cool" range (outer flame body).
- `color1` feeds into the shader's "hot" range (inner core where flame is brightest).

---

## 4. Light Component

The Unity `Light` on the torch is driven by `LightFlicker` (a Valheim MonoBehaviour).
- Readable/settable via reflection on `m_baseIntensity` and `m_light`.
- Setting `Light.color` to any `Color` already works correctly in the existing code.

---

## 5. Spark / Glow Particles (Other Particle Systems)

Other particle systems under the flame enabledObject (sparks, glow billboard):
- These likely use **simpler materials** (single round dot texture) without `ParticleGradientMapped_Unlit`.
- For these, the existing `ParticleSystem.main.startColor` tinting approach is the correct method.
- The safest detection: check `customData.enabled && GetMode(Custom1) == Color && GetMode(Custom2) == Color`; if true → CustomData approach, otherwise → startColor fallback.

---

## 6. fx_Torch_Green (Comparison)

Multiple `fx_Torch_Green` GameObjects exist in the Mistlands/CastleKit bundles — these are the green-fire castle torches. They use the same `ParticleGradientMapped_Unlit` shader and the same pattern, but with `Custom1`/`Custom2` set to green HDR gradients. This confirms that Valheim's own team uses exactly the CustomData approach to produce different-coloured flames — **we follow the same technique**.

---

## 7. Implications for the Mod

### What DOES NOT work (old approach)
Setting `ParticleSystem.main.startColor` on an `fx_Torch_Basic` particle system has **no visible effect on flame colour**, because the shader ignores the particle `startColor` — it maps all colour from CustomData gradients only. This explains why the original mod could not change the flame colour.

### What WORKS — Correct Approach

For any `fx_Torch_Basic`-type particle system (CustomData gradient-mapped):
1. **Enable** `customData` module.
2. **Set mode** for Custom1 and Custom2 to `ParticleSystemCustomDataMode.Color`.
3. **Build two HDR Gradient objects** from the user's chosen colour:
   - `Custom1` ("cool body"): `color * mult * 2f` → `color * mult * 0.25f`
   - `Custom2` ("hot core"):  `color * mult * 2f` → `color * mult * 1.0f`
4. Assign via `customData.SetColor(ParticleSystemCustomData.Custom1/2, mmg)`.

For sparks / glow particles (no CustomData): continue using `main.startColor`.

Light tinting via `Light.color` + `LightFlicker.m_baseIntensity` is unchanged and already works.

### No texture replacement needed
The greyscale `flameball_flipbook` texture is already a neutral white shape — perfect for any colour. No PNG generation, no file I/O, no texture creation at runtime.

---

## 8. Asset Paths / References

| Asset | Original Path | Bundle |
|---|---|---|
| `flameball_flipbook` texture | `Assets/Effects/textures/flameball_flipbook.png` | `c4210710` |
| `flameball_flipbook_gradient` material | `Assets/Effects/materials/flameball_flipbook_gradient.mat` | `c4210710` |
| `ParticleGradientMapped_Unlit` shader | — | `c4210710` |
| `piece_groundtorch_wood` prefab | — | `cab-09e5cc5ea5674206efec80841cec59cf` |
| `fx_Torch_Basic` (particle GO) | — | `cab-09e5cc5ea5674206efec80841cec59cf` |
| `fx_Torch_Green` (green variant) | — | `cab-ea1f07a21a86f77ee7f6e2ed810a6671`, `cab-9c47d05dcf...` |
