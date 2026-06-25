shader_type canvas_item;

// Flat directional arrow marker drawn inside a UI rect via signed-distance math,
// no texture asset. The owning Control is rotated to the player's heading; this
// shader just draws a centered arrow that points up in local space. Anti-aliased
// with screen-space derivatives so it stays crisp at any marker size.

uniform vec4 marker_color : source_color = vec4(0.95, 0.15, 0.15, 1.0);
uniform vec4 outline_color : source_color = vec4(1.0, 1.0, 1.0, 1.0);

// Signed distance from point p to the triangle (a, b, c). Negative inside.
float sd_triangle(vec2 p, vec2 a, vec2 b, vec2 c) {
	vec2 e0 = b - a; vec2 e1 = c - b; vec2 e2 = a - c;
	vec2 v0 = p - a; vec2 v1 = p - b; vec2 v2 = p - c;
	vec2 pq0 = v0 - e0 * clamp(dot(v0, e0) / dot(e0, e0), 0.0, 1.0);
	vec2 pq1 = v1 - e1 * clamp(dot(v1, e1) / dot(e1, e1), 0.0, 1.0);
	vec2 pq2 = v2 - e2 * clamp(dot(v2, e2) / dot(e2, e2), 0.0, 1.0);
	float s = sign(e0.x * e2.y - e0.y * e2.x);
	vec2 d = min(min(
		vec2(dot(pq0, pq0), s * (v0.x * e0.y - v0.y * e0.x)),
		vec2(dot(pq1, pq1), s * (v1.x * e1.y - v1.y * e1.x))),
		vec2(dot(pq2, pq2), s * (v2.x * e2.y - v2.y * e2.x)));
	return -sqrt(d.x) * sign(d.y);
}

void fragment() {
	vec2 uv = UV - vec2(0.5);
	float dist = sd_triangle(uv, vec2(0.0, -0.42), vec2(0.30, 0.32), vec2(-0.30, 0.32));
	float edge = fwidth(dist) * 1.5;
	float fill = 1.0 - smoothstep(0.0, edge, dist);
	float outline = (1.0 - smoothstep(0.05, 0.05 + edge, dist)) - fill;
	vec3 col = mix(outline_color.rgb, marker_color.rgb, fill);
	float alpha = clamp(fill + outline, 0.0, 1.0);
	COLOR = vec4(col, alpha);
}
