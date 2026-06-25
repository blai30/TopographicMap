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

void fragment() {
	vec2 buffer_uv = clamp(window_center + (UV - vec2(0.5)) * window_span, vec2(0.0), vec2(1.0));
	float h = mix(height_min, height_max, texture(height_buffer, buffer_uv).r);
	float span = max(0.0001, height_max - height_min);
	float band_center = (floor(h / contour_interval) + 0.5) * contour_interval;
	float tint_t = clamp((band_center - height_min) / span, 0.0, 1.0);
	COLOR = vec4(texture(color_ramp, vec2(tint_t, 0.5)).rgb, 1.0);
}
