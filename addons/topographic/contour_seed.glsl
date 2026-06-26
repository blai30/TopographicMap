#[compute]
#version 450

// Seed pass for the contour signed-distance field. A texel is "on a contour" when a
// contour level multiple falls between its world height and a right/down neighbor's.
// For such a texel, store the sub-texel crossing position (in UV) and the level index
// as a jump-flood seed. Robust on flat ground: it is a discrete band comparison, no
// gradient division.

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(rgba16f, set = 0, binding = 0) uniform image2D color_image; // R=norm height, G=mask
layout(rgba32f, set = 0, binding = 1) uniform image2D seed_out;    // xy=seedUV, z=level, w=valid

layout(push_constant, std430) uniform Params {
	vec2 size;
	float height_min;
	float height_max;
	float interval; // world units
	float pad0;
	float pad1;
	float pad2;
} p;

void main() {
	ivec2 px = ivec2(gl_GlobalInvocationID.xy);
	if (px.x >= int(p.size.x) || px.y >= int(p.size.y)) { return; }

	vec4 c = imageLoad(color_image, px);
	if (c.g < 0.5) { imageStore(seed_out, px, vec4(0.0)); return; }

	float h = mix(p.height_min, p.height_max, c.r);

	float best_t = 1e9;
	vec2 best_uv = vec2(0.0);
	float best_level = 0.0;
	bool found = false;

	for (int dir = 0; dir < 2; dir++) {
		ivec2 npx = px + (dir == 0 ? ivec2(1, 0) : ivec2(0, 1));
		if (npx.x >= int(p.size.x) || npx.y >= int(p.size.y)) { continue; }
		vec4 nc = imageLoad(color_image, npx);
		if (nc.g < 0.5) { continue; }
		float hn = mix(p.height_min, p.height_max, nc.r);
		if (hn == h) { continue; }
		float lo = min(h, hn), hi = max(h, hn);
		float lvl = ceil(lo / p.interval);  // first level multiple strictly above lo
		float lv = lvl * p.interval;
		if (lv <= lo || lv > hi) { continue; }
		float t = (lv - h) / (hn - h); // 0..1 along the edge from this texel
		if (t < best_t) {
			best_t = t;
			vec2 cross_px = vec2(px) + (dir == 0 ? vec2(t, 0.0) : vec2(0.0, t));
			best_uv = (cross_px + 0.5) / p.size;
			best_level = lvl;
			found = true;
		}
	}

	imageStore(seed_out, px, found ? vec4(best_uv, best_level, 1.0) : vec4(0.0));
}
