#[compute]
#version 450

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(set = 0, binding = 0, rgba16f) uniform image2D color_image;
layout(set = 0, binding = 1) uniform sampler2D depth_texture;

layout(set = 0, binding = 2, std140) uniform Params {
	mat4 proj;      // forward camera projection (view -> clip)
	mat4 inv_view;  // camera transform (view -> world)
	vec4 misc0; // raster_size.xy, min_elevation, max_elevation
	vec4 misc1; // levels, fill_low, fill_high, major_every
	vec4 misc2; // minor_width_px, major_width_px, minor_opacity, major_opacity
	vec4 misc3; // contours_enabled, smooth_ramp, minor_fade, major_contours_enabled
	vec4 misc4; // invert_ramp, unused, unused, unused
	vec4 ink_color;
	vec4 paper_color;
	vec4 background_color;
} p;

// Reconstruct world-space elevation (world Y) at a pixel center: inv-projection,
// perspective divide, inv-view. inv_proj is computed once in main() (GLSL
// inverse() is reliable here). Returns the raw depth for background detection.
float elevation_at(vec2 uv, mat4 inv_proj, out float out_depth) {
	// Compute shaders have no implicit LOD, so sample with textureLod, never
	// texture() (plain texture() reads as zero here).
	float depth = textureLod(depth_texture, uv, 0.0).r;
	out_depth = depth;
	vec4 view = inv_proj * vec4(uv * 2.0 - 1.0, depth, 1.0);
	view.xyz /= view.w;
	vec4 world = p.inv_view * vec4(view.xyz, 1.0);
	return world.y;
}

// Anti-aliased iso-line mask. deriv is the screen-space step gradient
// (reconstructed by neighbor differencing, since fwidth is unavailable in
// compute shaders). Flat ground -> zero deriv -> no line.
float iso(float q, float deriv, float width_px) {
	if (deriv <= 0.0) {
		return 0.0;
	}
	float dist = abs(fract(q - 0.5) - 0.5);
	return 1.0 - clamp(dist / (deriv * width_px), 0.0, 1.0);
}

void main() {
	ivec2 px = ivec2(gl_GlobalInvocationID.xy);
	ivec2 size = ivec2(p.misc0.xy);
	if (px.x >= size.x || px.y >= size.y) {
		return;
	}

	vec2 inv_size = 1.0 / p.misc0.xy;
	vec2 uv = (vec2(px) + 0.5) * inv_size;

	mat4 inv_proj = inverse(p.proj);

	float depth;
	float elevation = elevation_at(uv, inv_proj, depth);

	// Empty space = near/far plane. Covers reverse-Z and standard depth.
	bool is_background = (depth <= 0.000001 || depth >= 0.999999);

	float min_e = p.misc0.z;
	float max_e = p.misc0.w;
	float levels_f = max(p.misc1.x, 1.0);
	float fill_low = p.misc1.y;
	float fill_high = p.misc1.z;
	float major_every = max(p.misc1.w, 1.0);
	float minor_width_px = p.misc2.x;
	float major_width_px = p.misc2.y;
	float minor_opacity = p.misc2.z;
	float major_opacity = p.misc2.w;
	bool contours_enabled = p.misc3.x > 0.5;
	bool smooth_ramp = p.misc3.y > 0.5;
	float minor_fade = p.misc3.z;
	bool major_contours_enabled = p.misc3.w > 0.5;
	bool invert_ramp = p.misc4.x > 0.5;

	float range = max(max_e - min_e, 0.0001);
	float te = clamp((elevation - min_e) / range, 0.0, 1.0);

	float band = clamp(floor(te * levels_f), 0.0, levels_f - 1.0);
	float ramp = smooth_ramp ? te : band / max(levels_f - 1.0, 1.0);

	// Default: high elevation -> light (paper), low -> dark (ink).
	// invert_ramp flips the color-to-elevation mapping (high -> dark).
	if (invert_ramp) {
		ramp = 1.0 - ramp;
	}
	float fill = mix(fill_low, fill_high, ramp);
	vec3 col = mix(p.ink_color.rgb, p.paper_color.rgb, fill);

	if (contours_enabled) {
		// Step-space gradient from neighbor texels (replaces fwidth(q)).
		float depth_dx;
		float depth_dy;
		float elev_dx = elevation_at(uv + vec2(inv_size.x, 0.0), inv_proj, depth_dx);
		float elev_dy = elevation_at(uv + vec2(0.0, inv_size.y), inv_proj, depth_dy);

		float q = te * levels_f;
		float te_dx = clamp((elev_dx - min_e) / range, 0.0, 1.0);
		float te_dy = clamp((elev_dy - min_e) / range, 0.0, 1.0);
		float deriv_q = abs(te_dx * levels_f - q) + abs(te_dy * levels_f - q);

		float minor = iso(q, deriv_q, minor_width_px);
		float major = iso(q / major_every, deriv_q / major_every, major_width_px);

		float fade = clamp(1.0 - deriv_q * minor_fade, 0.0, 1.0);
		minor *= fade;

		col = mix(col, p.ink_color.rgb, minor * minor_opacity);
		if (major_contours_enabled) {
			col = mix(col, p.ink_color.rgb, major * major_opacity);
		}
	}

	col = mix(col, p.background_color.rgb, float(is_background));

	imageStore(color_image, px, vec4(col, 1.0));
}
