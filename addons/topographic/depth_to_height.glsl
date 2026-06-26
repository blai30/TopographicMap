#[compute]
#version 450

// Height-buffer producer. Reads the orthographic top-down camera depth buffer,
// reconstructs world height (linear for an orthographic projection), and writes
// normalized height plus a terrain/background coverage mask into the color
// image. Topographic styling (tint and contours) is applied downstream by
// consumers at their own resolution, so this stage outputs data, not a styled
// image.

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(rgba16f, set = 0, binding = 0) uniform image2D color_image;
layout(set = 0, binding = 1) uniform sampler2D depth_tex;

layout(push_constant, std430) uniform Params {
	vec2 size;
	float cam_y;
	float near_plane;
	float far_plane;
	float height_min;
	float height_max;
	float depth_reversed;
} p;

float world_y_at(float d) {
	float view_z = (p.depth_reversed > 0.5)
		? mix(p.far_plane, p.near_plane, d)
		: mix(p.near_plane, p.far_plane, d);
	return p.cam_y - view_z;
}

void main() {
	ivec2 px = ivec2(gl_GlobalInvocationID.xy);
	if (px.x >= int(p.size.x) || px.y >= int(p.size.y)) {
		return;
	}
	vec2 uv = (vec2(px) + 0.5) / p.size;

	float d = texture(depth_tex, uv).r;
	bool is_background = (p.depth_reversed > 0.5) ? (d <= 0.00001) : (d >= 0.99999);

	float wy = world_y_at(d);
	float t = clamp((wy - p.height_min) / max(0.0001, p.height_max - p.height_min), 0.0, 1.0);
	float mask = is_background ? 0.0 : 1.0;

	imageStore(color_image, px, vec4(t, mask, 0.0, 1.0));
}
