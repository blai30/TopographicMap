#[compute]
#version 450

// Optional smoothing pass for the height buffer. A separable box blur of the
// normalized height (R), run as two passes (horizontal then vertical) between
// the height pass and the seed pass. The coverage mask (G) is carried through
// unchanged. Because both the contour lines (seed pass) and the tint bands
// (consumer shader) read this same buffer, blurring it once here smooths both
// together and keeps them aligned, with no change to the seed or consumer
// shaders. They simply read a smoother buffer.

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(rgba16f, set = 0, binding = 0) uniform image2D src; // R=norm height, G=mask
layout(rgba16f, set = 0, binding = 1) uniform image2D dst;

layout(push_constant, std430) uniform Params {
	vec2 size;
	ivec2 dir;  // (1,0) horizontal pass, (0,1) vertical pass
	int radius; // blur radius in texels
	int pad0;
	int pad1;
	int pad2;
} p;

void main() {
	ivec2 px = ivec2(gl_GlobalInvocationID.xy);
	if (px.x >= int(p.size.x) || px.y >= int(p.size.y)) { return; }

	ivec2 hi = ivec2(p.size) - 1;
	float sum = 0.0;
	float weight = 0.0;
	for (int i = -p.radius; i <= p.radius; i++) {
		ivec2 s = clamp(px + p.dir * i, ivec2(0), hi);
		sum += imageLoad(src, s).r;
		weight += 1.0;
	}

	vec4 c = imageLoad(src, px);
	imageStore(dst, px, vec4(sum / weight, c.g, c.b, c.a)); // blurred R, original mask
}
