shader_type canvas_item;

// Per-pixel hypsometric tint sampled from the shared height buffer over a UV
// window. Contour lines are drawn separately as vector geometry (ContourLayer),
// so this shader only produces the stepped elevation tint. Elevation is the only
// concept: low ground (including underwater) takes the gradient's low colors.

uniform sampler2D height_buffer : filter_linear, repeat_disable;
uniform sampler2D color_ramp : filter_linear, repeat_disable;
uniform float height_min = -40.0;
uniform float height_max = 110.0;
uniform float contour_interval = 10.0;
uniform vec2 window_center = vec2(0.5);
uniform vec2 window_span = vec2(1.0);

// Contour line styling. px_per_uv converts the SDF (distance stored in buffer-UV
// units) to screen pixels using the current zoom, so the line stays a constant screen
// width with no per-pixel gradient (which is what makes flat ground robust).
uniform float px_per_uv = 1.0;
uniform float major_every = 5.0;
uniform float minor_width_px = 1.1;
uniform float major_width_px = 1.9;
uniform float contour_darken = 0.6;

void fragment() {
	vec2 buffer_uv = clamp(window_center + (UV - vec2(0.5)) * window_span, vec2(0.0), vec2(1.0));
	vec4 buf = texture(height_buffer, buffer_uv);
	float h = mix(height_min, height_max, buf.r);
	float span = max(0.0001, height_max - height_min);

	// Stepped hypsometric tint.
	float band_center = (floor(h / contour_interval) + 0.5) * contour_interval;
	float tint_t = clamp((band_center - height_min) / span, 0.0, 1.0);
	vec3 tint = texture(color_ramp, vec2(tint_t, 0.5)).rgb;

	// Contour line from the signed-distance field (B = distance in UV units to the
	// nearest line). The line's level is the nearest contour multiple to the local
	// height: near a line, h equals that line's level, so we need not store it.
	float screen_dist = buf.b * px_per_uv;
	float level_index = floor(h / contour_interval + 0.5);
	float is_major = step(0.5, 0.5 - abs(mod(level_index, major_every)));
	float width = mix(minor_width_px, major_width_px, is_major);
	float line_a = 1.0 - smoothstep(width - 1.0, width + 1.0, screen_dist);
	float level_norm = clamp((level_index * contour_interval - height_min) / span, 0.0, 1.0);
	vec3 line_col = texture(color_ramp, vec2(level_norm, 0.5)).rgb * contour_darken;

	COLOR = vec4(mix(tint, line_col, line_a), 1.0);
}
