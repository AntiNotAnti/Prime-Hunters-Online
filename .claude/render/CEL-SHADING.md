# Cel shading — the flat colour and the ink line

Two halves, in two different places. `Shaders.FragmentShader` paints every
surface in one flat colour and bands what is left of the shading;
`Shaders.CelFragmentShader` runs over the finished offscreen target and draws
the ink line. `Mods/RenderOptions.cs` holds the three knobs (`CelShading`,
`CelBands`, `CelEdge`) and the launcher's Settings page shows them.

The ES copies of both live in `Mods/Render/EsShaders.cs` and are checked
against the SHA-256 of the desktop source they were written from, so a change
here that is not carried across throws by name at the first compile rather
than rendering differently on a phone. Recompute after editing `Shaders.cs`:
normalise CRLF to LF, hash each `@"..."` body with `""` unescaped.

## Half one: the texture is replaced, not banded

Banding the brightness of a photograph of rubble gives banded rubble. The
mode only started to read as *drawn* when the texture stopped being there at
all:

- `Renderer._flatColors` maps a texture's binding ID to the one colour it
  averages to. It is filled as the texels go to the card -- `BindTexture` and
  `BindGetTexture` are the only places this code ever holds the pixels.
- The average is **weighted by alpha**. A cut-out texture (a grate, a decal, a
  sprite) is mostly transparent and its transparent texels usually carry
  black; averaging those in drags every such surface towards the one colour
  the outline pass needs to keep for itself.
- `Renderer.SetFlatColor` sets `use_flat` and `flat_color` per render item.
  The shader replaces `texcolor.rgb` and **keeps `texcolor.a`**, so alpha
  cut-outs are still cut out and a decal is still shaped.
- `use_pal_override` wins over it: that path is already painting a chosen
  colour, and a flat average on top would lose it.
- HUD models drawn through the scene's program (`SetHudLayerUniforms` -- the
  damage flash, the locator icons, the intro filter) get `use_flat` and
  `cel_bands` set to zero. They are not part of the world. `UpdateUniforms`
  puts the bands back on the next frame.

What is left after that -- the vertex colours, which is where these rooms keep
nearly all of their shading, and the lighting -- is what `cel_shade` bands.
With no texture noise under it the step no longer has to be soft: it is
`smoothstep(0.46, 0.54, ...)` inside a band, wide enough not to crawl as the
camera moves and narrow enough that a band is a band.

## Half two: the ink line, and why it used to be everywhere

**Take the second difference of the raw depth buffer, not of a linearised
distance.** That is the whole fix, and it is one line.

Window-space depth is an affine function of `1/z`, and `1/z` is linear across
the screen for any plane at any angle -- that is what makes perspective-correct
interpolation work. So the second difference of the raw value is *exactly*
zero on a flat surface however steeply it runs away from the camera.

The pass used to linearise to world units first and difference that. `z`
itself is **not** linear in screen space: its second difference across a floor
running to the far wall is large, and it grows with the angle. So every flat
surface seen at a slant -- which is most of a room -- was scribbled over, and
the threshold had to be set so high to hide it that real silhouettes came out
faint. That was the "far too many black lines on flat walls".

The rest is normalisation, so one threshold means one thing everywhere:

| Divide by | Takes out |
|---|---|
| `far/(far-near) - d`, which is `(far*near/(far-near))/z` | how far away the surface is |
| `texel_w` | the resolution: the same corner reads the same at 640x360 and at 4K |

What comes out is about **2 for a right-angled crease** and **hundreds for a
silhouette**. `smoothstep(1.1, 3.5, rel)` is where it sits: sharp creases and
every silhouette, and not the shallow facets of a low-poly cliff, which read
around 0.5 and used to fill Alinos Landfall with a triangle mesh.

Two details that matter:

- **The line's width comes from reach, not from a blur.** `kink_at` is
  evaluated at one texel and at two, each divided by its reach (the second
  difference of a plane is zero at any reach, so both are still clean), and
  the max is taken. A pixel two away from an edge still sees it, so the ink is
  three or four pixels wide instead of the one pixel a drawn line never is.
- **The cleared far plane is skipped** (`d < 0.9999995`). Nothing was drawn
  there, so there is no shape to draw around, and the normalisation would
  divide by nearly zero. The silhouette against it is still found -- from the
  geometry's side, where `d` is finite.

`CelEdge` defaults to **1**, a line of solid black. It was 0.75 back when the
pass inked most of every wall and full strength would have been unreadable.
The default lives in **two** places and both have to say it: `RenderOptions`
holds the engine's, and `MenuSettings` in `Menu.cs` holds the settings file's,
which wins on every path that loads settings -- which is every path a player
takes. Changing only the first changes nothing anybody sees.

## What the pass needs from the depth buffer, and how to check it

The kink it is looking for is around a millionth of the stored value, so this
pass is only as good as the depth it reads. Two things protect that, and one
number says whether they were enough.

- **The neighbours are subtracted from the centre before being added to each
  other.** As a sum of three samples near 1.0 it is three roundings against a
  signal a millionth their size; as two differences it is exact, because a
  difference of two floats within a factor of two of each other always is.
  This matters more on a phone than here: `highp` in an ES fragment shader is
  only promised sixteen bits of mantissa.
- **The reaches are two and three, not one and two.** The kink is divided by
  the reach and the buffer's error is not, so reaching further is quieter as
  well as wider. Moving up one step is two to three times the margin for a
  line that measures 2-3% different on the desktop and looks identical.
- **The taps are placed from `gl_FragCoord`, not from the interpolated
  texture coordinate.** `gl_FragCoord.xy` is the pixel centre exactly, so the
  samples land on texel centres exactly and are exactly `r` texels apart.
  Through a varying they are only as good as the interpolator, and ES promises
  `highp` no better than sixteen bits of mantissa: a fraction of a row of
  error, which is nothing to a picture and everything to a pass that compares
  a row against the two either side of it. Getting a neighbour off by a row
  every so often draws a line **straight across the screen** wherever it
  happens -- regularly spaced, perfectly horizontal, crossing every surface at
  the same y, because it is a property of the screen and not of the geometry.
  That is what a phone was doing while this machine was not, and on this
  machine the change is pixel-for-pixel invisible.
- **`depth_quantum`** is asked of the driver per attachment
  (`Renderer.MeasureDepthQuantum`) rather than assumed, and each reach lifts
  its threshold to clear it. Ask the *depth* half of the attachment: both GL
  and ES answer `INVALID_OPERATION` for a component size of
  `DEPTH_STENCIL_ATTACHMENT` itself, and SwiftShader will not answer at all.

### The one that mattered: measure it, do not ask

**A driver reports the bits it stores. It does not report what its
rasteriser's interpolation was worth on the way in** -- and if that is coarse
then every flat wall arrives with a kink on it, the pass inks the lot, and no
arithmetic in the shader recovers what never arrived. That is a phone drawing
regular lines across every surface while this machine draws none, with both
reporting a 24-bit depth buffer.

So `Renderer.CalibrateInk` measures it, once a scene:

1. `DrawCelQuad(target, probe: true)` paints `log2` of the kink instead of the
   picture, ±12 mapped into 0..1, black where nothing was drawn.
2. A centred 384x384 patch is read back -- **colour, because that is the one a
   GL ES driver is required to hand back; `glReadPixels` on depth is desktop
   only**.
3. The **median** over the drawn pixels stands for the flat surfaces. Most of
   any frame is flat surface, and an edge measures hundreds, so edges sit in a
   tail that cannot move a median.
4. Six times that becomes `ink_floor`, and `edge_at` takes whichever is higher
   of it, the fixed 1.1, and what `depth_quantum` implies.

It is free where the depth is good: this machine measures **0.0159**, the
floor lands at 0.095, the fixed threshold still governs, and the picture is
pixel-for-pixel what it was. Where the depth is bad the outline retreats to
the silhouettes instead of scribbling: SwiftShader measures **1998**, the
floor lands at 11991, and the room is visible again instead of solid black.

The probe runs in the same frame as the real pass and before it, so nobody
ever sees it -- the scene has already been copied to `_celTexture` by then and
the real pass paints every pixel back out of it.

Three lines of log say where a machine stands:

```
[render] cel shading on, 4 bands, outline 1.00, fog off
[render] cel outline: the depth buffer is 24 bits
[render] cel outline: a flat surface measures 0.0159 here against a threshold
         of 1.1, which is nothing. Ink floor 0.0953.
```

The third is the one to ask for. Anything near or above 1 in it means the ink
would have been drawing the machine's own error, and now is not.

## On OpenGL ES

`Renderer.DrawCelOutline` uses a separate color-only framebuffer pointing at
the scene color texture. It samples the copied color and the scene depth;
neither is attached to the framebuffer being drawn into. Keeping that depth
attachment out of the draw target avoids texture feedback without detaching
and reattaching it on every frame. The color-only target is validated when
attached and after resizing. Explicit pass state and respawn checks are
documented in `RENDER-STABILITY.md`.

## Testing it

`-cel on|off`, `-celbands N`, `-celedge N` and `-fog on|off` are read for every
invocation (`ModEntry.ApplyRenderOverrides`), which is what lets a screenshot
command ask for the mode -- the settings file is the launcher's, and
`-thumbnail`, `-maptest` and `-connect` never open one.

```bash
FruityPrime -thumbnail "MP3 PROVING GROUND" -cel on      # one room, into thumbnails/
FruityPrime -thumbnail "UNIT1 ALINOS LANDFALL" -cel on -celbands 5
FruityPrime -netcheck HOST -port N -shots DIR -cel on    # a real match, with hunters
```

Judge it on the desktop. SwiftShader on the emulator draws this scene with
dithered noise through every surface, cel shading on and off alike, so an
Android picture answers "it ran" and nothing about the picture.
