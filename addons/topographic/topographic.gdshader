shader_type canvas_item;

// Per-pixel hypsometric tint plus analytic vector contour lines, sampled from the shared
// height buffer over a UV window. The contour lines are resolution independent: instead of
// reading a precomputed distance field, this shader samples the compositor's per-cell
// contour SEGMENT texture and computes the EXACT point-to-segment distance for the display
// pixel, so lines stay crisp and constant-width at any zoom with no texel facets. Elevation
// is the only concept: low ground (including underwater) takes the gradient's low colors.

// Elevation tint: the gradient. The height range it spans (height_min/height_max) is baked
// into the segment texture's metadata texel by the compositor and read back in-shader.
group_uniforms Elevation;
/**
 * Gradient mapping elevation (height_min..height_max) to color. Colors both the
 * hypsometric bands and the contour lines. Presets live in addons/topographic/gradients/.
 */
uniform sampler2D elevation_gradient : filter_linear, repeat_disable;

// Contour line styling. Widths are in screen pixels, kept constant at any zoom via the
// in-shader px_per_uv derivation. The contour interval is baked into the segment texture's
// metadata texel by the compositor and read back in-shader.
group_uniforms Contours;
/** Every Nth contour line is drawn as a major (thicker) line. */
uniform float lines_per_major : hint_range(1.0, 20.0, 1.0) = 5.0;
/** Width of the minor contour lines, in screen pixels. */
uniform float minor_line_width_px : hint_range(0.0, 8.0, 0.1) = 0.6;
/** Width of the major contour lines, in screen pixels. */
uniform float major_line_width_px : hint_range(0.0, 8.0, 0.1) = 0.8;
// Contour line color. Lines can use a fixed custom color, a color derived from the
// gradient, or a blend of the two.
group_uniforms Contours.Color;
/** Custom contour line color, used when [code]line_color_from_gradient[/code] is below 1. */
uniform vec4 line_color : source_color = vec4(1.0);
/**
 * Blends the line color between the custom [code]line_color[/code] and a color derived
 * from the gradient. 0 = fully custom, 1 = fully gradient-derived.
 */
uniform float line_color_from_gradient : hint_range(0.0, 1.0, 0.01) = 1.0;
/**
 * For the gradient-derived color: darkens (negative, toward black) or lightens (positive,
 * toward white) the sampled gradient color. Lets dynamic lines stay visible on both light
 * and dark palettes.
 */
uniform float line_gradient_lightness : hint_range(-1.0, 1.0, 0.01) = -0.05;
/**
 * For the gradient-derived color: shifts where the line samples the gradient, so it can
 * take a neighboring elevation's hue instead of its own band's color.
 */
uniform float line_gradient_shift : hint_range(-0.5, 0.5, 0.01) = -0.3;

// Data inputs. Wire height_buffer to a ViewportTexture of the map SubViewport, and segments to
// the compositor's shared Texture2DRD. The elevation model (height_min/height_max/contour_interval)
// is not a uniform: the compositor bakes it into the segment texture's last texel and this shader
// reads it back, so producer and consumer cannot drift. window_center/window_span select the
// visible region; a pan/zoom map drives them from its own script.
group_uniforms Runtime;
/** Shared height buffer (R = normalized height, G = mask), from a ViewportTexture. */
uniform sampler2D height_buffer : filter_linear, repeat_disable;
/**
 * Compositor's per-cell contour segment texture (x0,y0,x1,y1 in UV; x0 < 0 means no contour).
 * Wire to the same shared Texture2DRD the compositor writes.
 */
uniform sampler2D segments : filter_nearest, repeat_disable;
/** Center of the sampled buffer-UV window (pan). */
uniform vec2 window_center = vec2(0.5);
/** Size of the sampled buffer-UV window (zoom). */
uniform vec2 window_span = vec2(1.0);

group_uniforms;

// Exact distance from point p to segment a-b, all in buffer-UV space.
float seg_dist(vec2 p, vec4 seg) {
    vec2 a = seg.xy;
    vec2 ab = seg.zw - a;
    float t = clamp(dot(p - a, ab) / max(dot(ab, ab), 1e-12), 0.0, 1.0);
    return distance(p, a + t * ab);
}

// Nearest contour-segment distance (UV) to a buffer point, searching the cells around it.
// The search radius scales with zoom: at high zoom the line spans one cell, at min zoom a
// constant-width screen line can cover several cells, so widen the neighborhood to match.
float vector_line_dist(vec2 buffer_uv, float px_per_uv) {
    vec2 tex = vec2(textureSize(segments, 0));
    float max_width_px = max(minor_line_width_px, major_line_width_px);
    int radius = clamp(int(ceil((max_width_px + 1.5) / px_per_uv * tex.x)), 1, 8);

    ivec2 base = ivec2(floor(buffer_uv * tex - 0.5));
    ivec2 hi = ivec2(tex) - 2; // last cell index (a cell spans px..px+1)

    float best = 1e3;
    for (int dy = -radius; dy <= radius; dy++) {
        for (int dx = -radius; dx <= radius; dx++) {
            ivec2 cell = clamp(base + ivec2(dx, dy), ivec2(0), hi);
            vec4 seg = texelFetch(segments, cell, 0);
            // x0 <= 0 means no contour: the seed pass writes -1 for empty cells, and a valid
            // endpoint UV is always > 0. This also rejects the 1x1 placeholder texture (zero
            // init) bound before the first compositor render and any out-of-range fetch.
            if (seg.x <= 0.0) { continue; }
            best = min(best, seg_dist(buffer_uv, seg));
        }
    }
    return best;
}

void fragment() {
    vec2 buffer_uv = clamp(window_center + (UV - vec2(0.5)) * window_span, vec2(0.0), vec2(1.0));

    // Elevation model from the segment texture's last texel (written by the seed pass). The
    // line search never reads this texel, so it is a safe metadata slot. Before the producer's
    // first render the placeholder reads zero, so guard the interval against divide-by-zero.
    vec4 meta = texelFetch(segments, ivec2(textureSize(segments, 0)) - 1, 0);
    float height_min = meta.r;
    float height_max = meta.g;
    float contour_interval = max(meta.b, 0.0001);

    // Screen pixels per buffer-UV unit, derived from the rect's pixel size (1.0 / fwidth(UV.x)
    // is the rect width in pixels) and the window span. Keeps the line width constant in screen
    // pixels with no driver pushing the value.
    float px_per_uv = (1.0 / max(fwidth(UV.x), 1e-9)) / max(window_span.x, 0.0001);

    float h = mix(height_min, height_max, texture(height_buffer, buffer_uv).r);
    float span = max(0.0001, height_max - height_min);

    // Exact screen-pixel distance to the nearest contour line for this display pixel.
    float dist_px = vector_line_dist(buffer_uv, px_per_uv) * px_per_uv;
    float level_index = floor(h / contour_interval + 0.5);

    // Anti-aliased hypsometric tint. A band boundary IS a contour line, so the color step
    // is placed and anti-aliased at the line (~1px), not at the faceted per-pixel height
    // threshold, so band edges stay as smooth as the lines and coincide with them. The side
    // (below/above the nearest line) comes from the local height; the level/blend jumps at
    // mid-band cancel, so the fill color stays continuous there.
    float blend = smoothstep(-0.5, 0.5, dist_px * sign(h - level_index * contour_interval));
    float t_below = clamp(((level_index - 0.5) * contour_interval - height_min) / span, 0.0, 1.0);
    float t_above = clamp(((level_index + 0.5) * contour_interval - height_min) / span, 0.0, 1.0);
    vec3 tint = mix(texture(elevation_gradient, vec2(t_below, 0.5)).rgb,
        texture(elevation_gradient, vec2(t_above, 0.5)).rgb, blend);

    // Crisp line: solid core with a 1px anti-aliased edge.
    float is_major = step(0.5, 0.5 - abs(mod(level_index, lines_per_major)));
    float width = mix(minor_line_width_px, major_line_width_px, is_major);
    float line_a = clamp(width - dist_px + 0.5, 0.0, 1.0);
    float level_norm = clamp((level_index * contour_interval - height_min) / span, 0.0, 1.0);

    // Gradient-derived line color: sample the gradient at the (optionally shifted) line
    // elevation, then darken (negative) or lighten (positive) toward black/white. Blend that
    // against the custom line_color so lines can be fixed, dynamic, or anything between.
    float grad_pos = clamp(level_norm + line_gradient_shift, 0.0, 1.0);
    vec3 grad_line = texture(elevation_gradient, vec2(grad_pos, 0.5)).rgb;
    grad_line = line_gradient_lightness >= 0.0
        ? mix(grad_line, vec3(1.0), line_gradient_lightness)
        : mix(grad_line, vec3(0.0), -line_gradient_lightness);
    vec3 line_col = mix(line_color.rgb, grad_line, line_color_from_gradient);

    COLOR = vec4(mix(tint, line_col, line_a), 1.0);
}
