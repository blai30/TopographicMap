#[compute]
#version 450

// Seed pass for the analytic vector contour lines. For each grid cell (this texel plus its
// right/down/diagonal neighbors) it runs marching squares for the single contour level
// crossing the cell and stores that contour SEGMENT (both endpoints, in UV) into the
// persistent segment texture. The canvas shader samples that texture directly and computes
// exact point-to-segment distance per display pixel, so there is no distance field to build
// or facet. It is a discrete band comparison, so it is robust on flat ground (no gradient
// division).

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(rgba16f, set = 0, binding = 0) uniform image2D color_image; // R=norm height, G=mask
layout(rgba32f, set = 0, binding = 1) uniform image2D seg_out;     // x0,y0,x1,y1 (UV); x0<0 invalid

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

	// Reserve the very last texel for elevation-model metadata. The consumer's line search
	// clamps cell indices to size-2 (a cell spans px..px+1), so this texel is never read as a
	// contour segment. The consumer reads height_min/height_max/interval from here, so producer
	// and consumer cannot drift. The w channel is a 1.0 "produced" flag (0.0 in the zero placeholder).
	ivec2 last_texel = ivec2(p.size) - 1;
	if (px == last_texel) {
		imageStore(seg_out, px, vec4(p.height_min, p.height_max, p.interval, 1.0));
		return;
	}

	vec4 invalid = vec4(-1.0);
	if (px.x + 1 >= int(p.size.x) || px.y + 1 >= int(p.size.y)) { imageStore(seg_out, px, invalid); return; }

	vec4 m00 = imageLoad(color_image, px);
	vec4 m10 = imageLoad(color_image, px + ivec2(1, 0));
	vec4 m01 = imageLoad(color_image, px + ivec2(0, 1));
	vec4 m11 = imageLoad(color_image, px + ivec2(1, 1));
	if (m00.g < 0.5 || m10.g < 0.5 || m01.g < 0.5 || m11.g < 0.5) { imageStore(seg_out, px, invalid); return; }

	float h00 = mix(p.height_min, p.height_max, m00.r);
	float h10 = mix(p.height_min, p.height_max, m10.r);
	float h01 = mix(p.height_min, p.height_max, m01.r);
	float h11 = mix(p.height_min, p.height_max, m11.r);

	float cmin = min(min(h00, h10), min(h01, h11));
	float cmax = max(max(h00, h10), max(h01, h11));
	float lv = (floor(cmin / p.interval) + 1.0) * p.interval; // first level multiple above cmin
	if (lv > cmax) { imageStore(seg_out, px, invalid); return; }

	// Marching-squares edge crossings, in cell-local coords (corner A=(0,0)=h00,
	// B=(1,0)=h10, C=(0,1)=h01, D=(1,1)=h11).
	vec2 pts[4];
	int n = 0;
	if ((h00 < lv) != (h10 < lv)) { pts[n++] = vec2((lv - h00) / (h10 - h00), 0.0); } // top A-B
	if ((h01 < lv) != (h11 < lv)) { pts[n++] = vec2((lv - h01) / (h11 - h01), 1.0); } // bottom C-D
	if ((h00 < lv) != (h01 < lv)) { pts[n++] = vec2(0.0, (lv - h00) / (h01 - h00)); } // left A-C
	if ((h10 < lv) != (h11 < lv)) { pts[n++] = vec2(1.0, (lv - h10) / (h11 - h10)); } // right B-D
	if (n < 2) { imageStore(seg_out, px, invalid); return; }

	// Convert to UV using texel-center positions (corner A is the center of texel px).
	vec2 base = vec2(px);
	vec2 uv0 = (base + pts[0] + 0.5) / p.size;
	vec2 uv1 = (base + pts[1] + 0.5) / p.size;
	imageStore(seg_out, px, vec4(uv0, uv1));
}
