#[compute]
#version 450

// Composite pass. Reads the final jump-flood result and writes the distance to the
// nearest contour line into the height buffer B channel and the line's level index
// into A, preserving R (height) and G (mask). One texture now carries everything the
// unified canvas shader needs.

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(rgba16f, set = 0, binding = 0) uniform image2D color_image; // R,G kept; B,A written
layout(rgba32f, set = 0, binding = 1) uniform image2D seed_final;

layout(push_constant, std430) uniform Params {
	vec2 size;
	float pad0;
	float pad1;
} p;

void main() {
	ivec2 px = ivec2(gl_GlobalInvocationID.xy);
	if (px.x >= int(p.size.x) || px.y >= int(p.size.y)) { return; }

	vec4 c = imageLoad(color_image, px);
	vec4 seed = imageLoad(seed_final, px);
	float dist = 1e3;
	float level = 0.0;
	if (seed.w > 0.5) {
		vec2 self_uv = (vec2(px) + 0.5) / p.size;
		dist = distance(self_uv, seed.xy);
		level = seed.z;
	}
	imageStore(color_image, px, vec4(c.r, c.g, dist, level));
}
