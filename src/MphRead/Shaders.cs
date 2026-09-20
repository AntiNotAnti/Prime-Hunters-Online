namespace MphRead
{
    public static class Shaders
    {
        public static string VertexShader { get; } = @"
#version 120
uniform bool use_light;
uniform bool use_texture;
uniform bool show_colors;
uniform bool fog_enable;
uniform vec3 light1vec;
uniform vec3 light1col;
uniform vec3 light2vec;
uniform vec3 light2col;
uniform vec3 diffuse;
uniform vec3 ambient;
uniform vec3 specular;
uniform vec3 emission;
uniform vec4 fog_color;
uniform float far_plane;
uniform mat4 proj_mtx;
uniform mat4 view_mtx;
uniform mat4 view_inv_mtx;
uniform mat4 tex_mtx;
uniform int texgen_mode;
uniform mat4[32] mtx_stack;

varying vec2 texcoord;
varying vec4 color;

vec3 light_calc(vec3 light_vec, vec3 light_col, vec3 normal_vec, vec3 dif_col, vec3 amb_col, vec3 spe_col)
{
    vec3 sight_vec = vec3(0.0, 0.0, -1.0);
    float dif_factor = max(0.0, -dot(light_vec, normal_vec));
    vec3 half_vec = (light_vec + sight_vec) / 2.0;
    float spe_factor = max(0.0, dot(-half_vec, normal_vec));
    spe_factor = spe_factor * spe_factor;
    vec3 spe_out = spe_col * light_col * spe_factor;
    vec3 dif_out = dif_col * light_col * dif_factor;
    vec3 amb_out = amb_col * light_col;
    return spe_out + dif_out + amb_out;
}

void main()
{
    mat4 stack_mtx = mtx_stack[int(gl_MultiTexCoord0.z)];
    // view_inv_mtx is set for billboard transforms
    mat4 model_mtx = stack_mtx * view_inv_mtx;
    gl_Position = proj_mtx * view_mtx * model_mtx * gl_Vertex;
    vec4 vtx_color = show_colors ? gl_Color : vec4(1.0);
    vec3 normal = normalize(mat3(model_mtx) * gl_Normal);
    if (use_light) {
        vec3 dif_current = diffuse;
        vec3 amb_current = ambient;
        if (gl_Color.a == 0.0) {
            // see comment on DIF_AMB
            dif_current = vtx_color.rgb;
            amb_current = vec3(0.0, 0.0, 0.0);
        }
        vec3 col1 = light_calc(light1vec, light1col, normal, dif_current, amb_current, specular);
        vec3 col2 = light_calc(light2vec, light2col, normal, dif_current, amb_current, specular);
        color = vec4(min((col1 + col2 + emission), vec3(1.0, 1.0, 1.0)), 1.0);
    }
    else {
        // alpha will only be less than 1.0 here if DIF_AMB is used but lighting is disabled
        color = vec4(vtx_color.rgb, 1.0);
    }
    if (use_texture) {
        // texgen mode: 0 - none, 1 - texcoord, 2 - normal, 3 - vertex
        if (texgen_mode == 0 || texgen_mode == 1) {
            texcoord = vec2(tex_mtx * vec4(gl_MultiTexCoord0.xy, 0, 1));
        }
        else if (texgen_mode == 2 || texgen_mode == 3) {
            mat4 tex_mul = tex_mtx;
            if (texgen_mode == 2) {
                // texgen uses the node transform, which doesn't have billboard transform applied
                tex_mul = transpose(tex_mtx * (use_light ? view_mtx : mat4(1.0)) * mat4(mat3(stack_mtx)));
            }
            mat2x4 texgen_mtx = mat2x4(
                vec4(tex_mul[0][0], tex_mul[0][1], tex_mul[0][2], gl_MultiTexCoord0.x),
                vec4(tex_mul[1][0], tex_mul[1][1], tex_mul[1][2], gl_MultiTexCoord0.y)
            );
            if (texgen_mode == 2) {
                texcoord = vec4(gl_Normal, 1.0) * texgen_mtx;
            }
            else {
                texcoord = vec4(gl_Vertex.xyz, 1.0) * texgen_mtx;
            }
        }
    }
    else {
        texcoord = vec2(0.0, 0.0);
    }
}
";

        public static string FragmentShader { get; } = @"
#version 120
uniform bool use_texture;
uniform bool fog_enable;
uniform vec4 fog_color;
uniform float fog_min;
uniform float fog_max;
uniform sampler2D tex;
uniform bool use_override;
uniform vec4 override_color;
uniform bool use_pal_override;
uniform vec4 pal_override_color;
uniform float mat_alpha;
uniform int mat_mode;
uniform vec3[32] toon_table;
// Cel shading: 0 bands is off. Only the scene is drawn through this program;
// the helmet and the HUD go through the RTT one afterwards and are left as
// they are without anything having to turn this off.
uniform int cel_bands;
// The one colour the bound texture averages to, and whether to use it in
// place of the texture's own. Set per render item by the renderer, which
// works the average out once when the texture is uploaded.
uniform bool use_flat;
uniform vec3 flat_color;

varying vec2 texcoord;
varying vec4 color;

vec4 toon_color(vec4 vtx_color)
{
    return vec4(toon_table[int(vtx_color.r * 31)], vtx_color.a);
}

// Brightness to steps, hue left alone: a surface keeps its colour and it is
// the shading across it that goes flat.
//
// The step used to be softened over the middle third of a band, because the
// texture was still there underneath and its texel-to-texel variation sat on
// a band boundary somewhere in every wall, which a hard step turned into
// speckle. There is no texture under this any more -- use_flat has already
// replaced it with one colour -- so the softening is down to the width that
// keeps the boundary from crawling as the camera moves, and a band is a band
// rather than a gradient.
vec3 cel_shade(vec3 c)
{
    float steps = float(cel_bands);
    float lum = max(max(c.r, c.g), c.b);
    if (lum <= 0.0) {
        return c;
    }
    // Levels sit at the middle of each band, so the darkest is not black and
    // the brightest is not blown out.
    float scaled = lum * steps - 0.5;
    float lower = floor(scaled);
    float level = (lower + 0.5 + smoothstep(0.46, 0.54, scaled - lower)) / steps;
    vec3 banded = c * (level / lum);
    // A drawn frame is more saturated than a photograph of the same thing,
    // and flattening the shading takes some of the apparent colour with it.
    float grey = dot(banded, vec3(0.299, 0.587, 0.114));
    return clamp(mix(vec3(grey), banded, 1.35), 0.0, 1.0);
}

void main()
{
    // mat_mode: 0 - modulate, 1 - decal, 2 - toon
    vec4 col;
    if (use_texture) {
        vec4 texcolor = use_pal_override ? vec4(pal_override_color.xyz, texture2D(tex, texcoord).w) : texture2D(tex, texcoord);
        // Cel shading takes the picture off the texture rather than banding
        // it. The texel's alpha is kept, so a grate is still a grate and a
        // decal is still cut to shape, but its colour is replaced by the one
        // colour the whole texture averages to. Banding a photograph of
        // rubble only ever produces banded rubble; what makes a picture read
        // as drawn is that the surface is one colour and the line around it
        // carries the shape.
        if (use_flat && !use_pal_override) {
            texcolor.rgb = flat_color;
        }
        if (mat_mode == 1) {
            col = vec4(
                (texcolor.r * texcolor.a + color.r * (1 - texcolor.a)),
                (texcolor.g * texcolor.a + color.g * (1 - texcolor.a)),
                (texcolor.b * texcolor.a + color.b * (1 - texcolor.a)),
                mat_alpha * color.a
            );
        }
        else if (mat_mode == 2) {
            vec4 toon = toon_color(color);
            col = vec4(texcolor.rgb * color.r + toon.rgb, mat_alpha * texcolor.a * color.a);
        }
        else {
            col = color * vec4(texcolor.rgb, mat_alpha * texcolor.a);
        }
        if (use_override) {
            col.r = override_color.r;
            col.g = override_color.g;
            col.b = override_color.b;
            col.a *= override_color.a;
        }
    }
    else if (use_override) {
        col = override_color;
    }
    else {
        col = mat_mode == 2 ? toon_color(color) : color;
        col.a *= mat_alpha;
    }
    // Cel shading, on the finished surface colour -- the texture, the vertex
    // colours and the lighting together, which is the only place all three
    // are. Banding the lighting term alone left a room untouched: rooms carry
    // nearly all of their shading in vertex colours and light almost nothing
    // dynamically, so the mode was invisible exactly where it should have
    // shown most. Before the fog, which is atmosphere rather than surface and
    // reads wrong in steps.
    if (cel_bands > 0) {
        col.rgb = cel_shade(col.rgb);
    }
    if (fog_enable) {
        float depth = gl_FragCoord.z;
        float density = 0.0;
        if (depth >= fog_max) {
            density = 1.0;
        }
        else if (depth > fog_min) {
            // MPH fog table has min 0 and max 124
            density = (depth - fog_min) / (fog_max - fog_min) * 124.0 / 128.0;
        }
        col = vec4((col * (1.0 - density) + fog_color * density).xyz, col.a);
    }
    gl_FragColor = col;
}
";

        /// <summary>
        /// The front screen's ground: the photograph with the moving field
        /// laid over it in <c>overlay</c>.
        ///
        /// Two coordinate sets, because the two layers are framed differently.
        /// The photograph is cropped to fill the window the way
        /// <c>UniformToFill</c> filled it, so unit 0 carries the trimmed
        /// rectangle; the field is stretched edge to edge like the canvas it
        /// is a port of, so unit 1 carries a plain 0..1.
        /// </summary>
        public static string BackdropVertexShader { get; } = @"
#version 120

varying vec2 photocoord;
varying vec2 noisecoord;

void main()
{
    gl_Position = vec4(gl_Vertex.xy, 0, 1);
    photocoord = gl_MultiTexCoord0.xy;
    noisecoord = gl_MultiTexCoord1.xy;
}
";

        /// <summary>
        /// <c>mix-blend-mode: overlay</c> at <c>opacity: .62</c>, per channel.
        ///
        /// Overlay is multiply where the backdrop is dark and screen where it
        /// is light -- the same curve as hard-light with the two layers
        /// swapped -- so mid grey in the field is the no-op and what reads is
        /// how far each cell swings either side of it. Branchless, because
        /// this runs once per pixel of the window and a step costs less than
        /// a divergent branch would.
        /// </summary>
        public static string BackdropFragmentShader { get; } = @"
#version 120

uniform sampler2D photo;
uniform sampler2D noise;
uniform float strength;
varying vec2 photocoord;
varying vec2 noisecoord;

void main()
{
    vec3 b = texture2D(photo, photocoord).rgb;
    vec3 s = texture2D(noise, noisecoord).rgb;
    vec3 lo = 2.0 * b * s;
    vec3 hi = 1.0 - 2.0 * (1.0 - b) * (1.0 - s);
    vec3 over = mix(lo, hi, step(vec3(0.5), b));
    gl_FragColor = vec4(mix(b, over, strength), 1.0);
}
";

        public static string RttVertexShader { get; } = @"
#version 120

varying vec2 texcoord;

void main()
{
    gl_Position = vec4(gl_Vertex.xy, 0, 1);
    texcoord = gl_MultiTexCoord0.xy;
}
";

        public static string RttFragmentShader { get; } = @"
#version 120

uniform float alpha;
uniform bool use_mask;
uniform float view_width;
uniform float view_height;
uniform vec4 fade_color;
uniform sampler2D tex;
uniform sampler2D mask;
varying vec2 texcoord;
varying vec4 color;

void main()
{
    if (fade_color.a > 0) {
        gl_FragColor = fade_color;
    }
    else {
        gl_FragColor = texture2D(tex, texcoord);
        if (use_mask) {
            float maskY = gl_FragCoord.y + (view_width - view_height) / 2;
            vec2 maskTexcoord = vec2(gl_FragCoord.x / view_width, 1 - maskY / view_width);
            vec4 maskColor = texture2D(mask, maskTexcoord);
            if (maskColor.a > 0) {
                gl_FragColor.a = 0;
            }
        }
        gl_FragColor.a *= alpha;
    }
}
";

        /// <summary>
        /// The ink line, drawn over the scene once it has been rendered.
        ///
        /// A silhouette is not something a fragment can see on its own: it is
        /// a place where this surface and the one behind it are different, and
        /// only a pass that can look at its neighbours knows that. So this
        /// runs over the finished offscreen target, reading the depth the
        /// scene left behind and darkening the pixels where it steps.
        ///
        /// Depth rather than colour. Colour finds every edge in a texture as
        /// well -- and this game's textures are rubble, panelling and grating,
        /// so a Sobel over brightness inks in most of a wall and the picture
        /// comes out scribbled on. What is wanted is the shape of the room,
        /// which is what the depth buffer holds.
        ///
        /// It runs there rather than over the window for two reasons: the
        /// helmet, the HUD and the fade are drawn afterwards and must not be
        /// outlined, and the screenshot path reads the offscreen target rather
        /// than the back buffer, so a line drawn later would be in the game
        /// and missing from every picture of it.
        /// </summary>
        public static string CelFragmentShader { get; } = @"
#version 120

uniform sampler2D tex;
uniform sampler2D depth_tex;
uniform float texel_w;
uniform float texel_h;
uniform float outline;
uniform float near_plane;
uniform float far_plane;
// One step of the depth buffer the driver actually gave us, which is not
// always the one that was asked for. See the note in edge_at.
uniform float depth_quantum;
// What the ink must clear before it is believed, measured on this machine
// rather than assumed. Zero until the first frame has been looked at.
// 1 draws the measurement instead of the picture, for that one frame.
uniform int probe;

varying vec2 texcoord;

// The depth buffer's own value, not a distance in world units.
//
// That is the whole trick. Window-space depth is an affine function of 1/z,
// and 1/z is *linear across the screen* for any plane at any angle -- that is
// what makes perspective-correct interpolation work at all. So the second
// difference of this number is exactly zero on a flat surface however steeply
// it runs away from the camera. Linearising it to world units first, which is
// what this pass used to do, throws that away: z itself is not linear in
// screen space, its second difference over a floor stretching to the far wall
// is large, and every flat surface seen at an angle came out scribbled over.
// Where this pixel is, worked out from gl_FragCoord rather than from the
// interpolated texture coordinate.
//
// gl_FragCoord.xy is the pixel centre exactly, so this lands on texel centres
// exactly and the taps are exactly r texels apart. Coming through a varying
// they are only as good as the interpolator, and ES promises highp no better
// than sixteen bits of mantissa -- a fraction of a row of error, which is
// nothing to a picture and everything to a pass that compares a row with the
// two either side of it. Getting a neighbour off by a row every so often
// draws a line straight across the screen wherever it happens, which is what
// a phone was doing while this machine was not.
vec2 pixel_uv()
{
    return gl_FragCoord.xy * vec2(texel_w, texel_h);
}

float raw_depth(float dx, float dy)
{
    return texture2D(depth_tex, pixel_uv() + vec2(dx * texel_w, dy * texel_h)).x;
}

// How much of an edge there is at a given reach, 0 to 1.
//
// Reach does two things. It widens the line -- a pixel three away from an
// edge still sees it, so the ink comes out three or four pixels wide instead
// of the one pixel a drawn line never is -- and, because the kink is divided
// by it while whatever the depth buffer got wrong is not, it is also
// *quieter*. Reaching two and three rather than one and two is two to three
// times the margin over a noisy depth buffer for a line that looks the same,
// and each reach carries its own floor rather than the two being maxed
// together and taking the noisier one's noise with them.
//
// The neighbours are subtracted from the centre before being added to each
// other. Written as a sum of three samples it is three roundings of numbers
// close to 1, and the signal here is around a millionth of that; written as
// two differences it is exact, since a difference of two floats within a
// factor of two of each other always is. That costs nothing and is most of
// the precision this pass has.
// The kink at one reach, in the units everything below is measured in.
// The kink in the depth buffer's own units -- what the pass actually looks
// at, before anything is made of it. Per unit of reach, so the reaches are
// comparable with each other and with a measurement taken at one of them.
float kink_abs(float d, float r)
{
    vec2 h = vec2(raw_depth(-r, 0.0) - d, raw_depth(r, 0.0) - d);
    vec2 v = vec2(raw_depth(0.0, -r) - d, raw_depth(0.0, r) - d);
    return max(abs(h.x + h.y), abs(v.x + v.y)) / r;
}

float kink_rel(float d, float r, float unit)
{
    return kink_abs(d, r) / unit;
}

float edge_at(float d, float r, float unit)
{
    // What this machine's depth is worth here, in this pixel's units.
    //
    // depth_quantum is an error in the *depth buffer's* units -- one step of
    // it, or worse where the driver interpolates worse than it stores, which
    // Renderer.CalibrateInk measures rather than takes on trust. Dividing it
    // by the same unit the kink is divided by is the whole point: unit shrinks
    // with distance and with grazing angle, so a fixed error is worth more and
    // more in these units the further away and the flatter-on the surface is.
    // A floor that did not do this was the bug -- it was a constant, measured
    // once in the middle of a frame where unit was large, and it protected
    // exactly the surfaces that never needed protecting. A floor stretching
    // away from the camera got no protection at all, and drew the depth
    // buffer's own steps as regular black stripes across itself.
    float quantised = depth_quantum * 4.0 / r / unit;
    float lo = max(1.1, quantised * 1.5);
    float hi = max(3.5, quantised * 4.0);
    return smoothstep(lo, hi, kink_rel(d, r, unit));
}

void main()
{
    vec3 base = texture2D(tex, pixel_uv()).rgb;
    float d = raw_depth(0.0, 0.0);
    float ink = 0.0;
    // Nothing was drawn here: the cleared far plane has no shape to draw
    // around, and the normalisation below divides by nearly zero on it.
    // The silhouette against it is still found, from the geometry's side.
    if (d < 0.9999995) {
        // far/(far-near) - d is (far*near/(far-near))/z, so dividing by it
        // takes the distance out and leaves a pure change of slope; dividing
        // by the texel width takes the resolution out, so the same threshold
        // means the same corner at 640x360 and at 4K. What is left is about
        // 2 for a right-angled crease and hundreds for a silhouette.
        float scale = far_plane / (far_plane - near_plane) - d;
        float unit = max(scale, 1e-9) * texel_w;
        if (probe == 1) {
            // log2 of the kink in depth units, -32..0 into 0..1, and black
            // where nothing was drawn so the reader can leave those pixels
            // out. Depth units, not this pixel's units, because what is being
            // measured is a property of the machine rather than of wherever
            // the camera happened to be pointing: a flat surface's kink in
            // these units is the depth error itself, and it is the same number
            // across the frame. Thirty-two powers of two below one covers a
            // buffer from eight bits to well past the twenty-four this asks
            // for.
            float shown = clamp(log2(max(kink_abs(d, 2.0), 1e-10)) / 32.0 + 1.0, 0.004, 1.0);
            gl_FragColor = vec4(shown, shown, shown, 1.0);
            return;
        }
        ink = max(edge_at(d, 2.0, unit), edge_at(d, 3.0, unit)) * outline;
    }
    else if (probe == 1) {
        gl_FragColor = vec4(0.0, 0.0, 0.0, 1.0);
        return;
    }
    gl_FragColor = vec4(base * (1.0 - ink), 1.0);
}
";

        public static string ShiftFragmentShader { get; } = @"
#version 120

uniform float[64] shift_table;
uniform int shift_idx;
uniform float shift_fac;
uniform float lerp_fac;
uniform float[192] white_table;
uniform float white_fac;
uniform sampler2D tex;

varying vec2 texcoord;
varying vec4 color;

void main()
{
    int band = int((1.0 - texcoord.y) * 192.0);
    int index = int(mod((band + shift_idx + mod(band, 2) * 32), 64));
    float value1 = shift_table[index];
    float value2 = shift_table[int(mod(index + 1, 64))];
    float value = mix(value1, value2, lerp_fac) * shift_fac;
    vec2 shifted = vec2(texcoord.x + value, texcoord.y);
    if (shifted.x < 0.0 || shifted.x > 1.0) {
        gl_FragColor = vec4(0, 0, 0, 1);
    }
    else {
        gl_FragColor = texture2D(tex, shifted);
    }
    if (white_fac != 0) {
        float factor = white_table[band];
        if (white_fac < 0) {
            gl_FragColor = vec4(factor, factor, factor, 1);
        }
        else {
            factor *= white_fac;
            if (factor >= 0) {
                float r = gl_FragColor.r + (1 - gl_FragColor.r) * factor;
                float g = gl_FragColor.g + (1 - gl_FragColor.g) * factor;
                float b = gl_FragColor.b + (1 - gl_FragColor.b) * factor;
                gl_FragColor = vec4(r, g, b, 1);
            }
            else {
                factor = -factor;
                float r = gl_FragColor.r  - gl_FragColor.r * factor;
                float g = gl_FragColor.g  - gl_FragColor.g * factor;
                float b = gl_FragColor.b  - gl_FragColor.b * factor;
                gl_FragColor = vec4(r, g, b, 1);
            }
        }
    }
}
";
    }

    public class ShaderLocations
    {
        public int UseLight { get; set; }
        public int ShowColors { get; set; }
        public int UseTexture { get; set; }
        public int Light1Color { get; set; }
        public int Light1Vector { get; set; }
        public int Light2Color { get; set; }
        public int Light2Vector { get; set; }
        public int Diffuse { get; set; }
        public int Ambient { get; set; }
        public int Specular { get; set; }
        public int Emission { get; set; }
        public int UseFog { get; set; }
        public int CelBands { get; set; }
        public int UseFlat { get; set; }
        public int FlatColor { get; set; }
        public int CelOutline { get; set; }
        public int CelTexelWidth { get; set; }
        public int CelTexelHeight { get; set; }
        public int CelNearPlane { get; set; }
        public int CelFarPlane { get; set; }
        public int CelDepthQuantum { get; set; }
        public int CelProbe { get; set; }
        public int FogColor { get; set; }
        public int FogMinDistance { get; set; }
        public int FogMaxDistance { get; set; }
        public int UseOverride { get; set; }
        public int OverrideColor { get; set; }
        public int UsePaletteOverride { get; set; }
        public int PaletteOverrideColor { get; set; }
        public int MaterialAlpha { get; set; }
        public int MaterialMode { get; set; }
        public int ViewMatrix { get; set; }
        public int ViewInvMatrix { get; set; }
        public int ProjectionMatrix { get; set; }
        public int TextureMatrix { get; set; }
        public int TexgenMode { get; set; }
        public int MatrixStack { get; set; }
        public int ToonTable { get; set; }
        public int FadeColor { get; set; }
        public int LayerAlpha { get; set; }
        public int UseMask { get; set; }
        public int ViewWidth { get; set; }
        public int ViewHeight { get; set; }
        public int ShiftTable { get; set; }
        public int ShiftIndex { get; set; }
        public int ShiftFactor { get; set; }
        public int LerpFactor { get; set; }
        public int WhiteoutTable { get; set; }
        public int WhiteoutFactor { get; set; }
    }
}
