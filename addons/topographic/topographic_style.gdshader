shader_type canvas_item;

// Per-pixel hypsometric tint plus analytic vector contour lines, sampled from the shared
// height buffer over a UV window. The contour lines are resolution independent: instead of
// reading a precomputed distance field, this shader samples the compositor's per-cell
// contour SEGMENT texture and computes the EXACT point-to-segment distance for the display
// pixel, so lines stay crisp and constant-width at any zoom with no texel facets. Elevation
// is the only concept: low ground (including underwater) takes the gradient's low colors.

uniform sampler2D height_buffer : filter_linear, repeat_disable;
uniform sampler2D segments : filter_nearest, repeat_disable; // per-cell x0,y0,x1,y1 (UV); x0<0 invalid
uniform sampler2D color_ramp : filter_linear, repeat_disable;
uniform float height_min = -40.0;
uniform float height_max = 110.0;
uniform float contour_interval = 10.0;
uniform vec2 window_center = vec2(0.5);
uniform vec2 window_span = vec2(1.0);

// Contour line styling. px_per_uv converts a UV-space distance to screen pixels using the
// current zoom, so the line stays a constant screen width with no per-pixel gradient
// (which is what makes flat ground robust).
uniform float px_per_uv = 1.0;
uniform float major_every = 5.0;
uniform float minor_width_px = 1.1;
uniform float major_width_px = 1.9;
uniform float contour_darken = 0.6;

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
float vector_line_dist(vec2 buffer_uv) {
	vec2 tex = vec2(textureSize(segments, 0));
	float max_width_px = max(minor_width_px, major_width_px);
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
	float h = mix(height_min, height_max, texture(height_buffer, buffer_uv).r);
	float span = max(0.0001, height_max - height_min);

	// Exact screen-pixel distance to the nearest contour line for this display pixel.
	float dist_px = vector_line_dist(buffer_uv) * px_per_uv;
	float level_index = floor(h / contour_interval + 0.5);

	// Anti-aliased hypsometric tint. A band boundary IS a contour line, so the color step
	// is placed and anti-aliased at the line (~1px), not at the faceted per-pixel height
	// threshold, so band edges stay as smooth as the lines and coincide with them. The side
	// (below/above the nearest line) comes from the local height; the level/blend jumps at
	// mid-band cancel, so the fill color stays continuous there.
	float blend = smoothstep(-0.5, 0.5, dist_px * sign(h - level_index * contour_interval));
	float t_below = clamp(((level_index - 0.5) * contour_interval - height_min) / span, 0.0, 1.0);
	float t_above = clamp(((level_index + 0.5) * contour_interval - height_min) / span, 0.0, 1.0);
	vec3 tint = mix(texture(color_ramp, vec2(t_below, 0.5)).rgb,
		texture(color_ramp, vec2(t_above, 0.5)).rgb, blend);

	// Crisp line: solid core with a 1px anti-aliased edge.
	float is_major = step(0.5, 0.5 - abs(mod(level_index, major_every)));
	float width = mix(minor_width_px, major_width_px, is_major);
	float line_a = clamp(width - dist_px + 0.5, 0.0, 1.0);
	float level_norm = clamp((level_index * contour_interval - height_min) / span, 0.0, 1.0);
	vec3 line_col = texture(color_ramp, vec2(level_norm, 0.5)).rgb * contour_darken;

	COLOR = vec4(mix(tint, line_col, line_a), 1.0);
}
