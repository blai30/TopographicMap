#[compute]
#version 450

// Seed pass for the analytic vector contour lines. For each grid cell (this texel plus its
// right/down/diagonal neighbors) it runs marching squares for the single contour level
// crossing the cell and stores that contour SEGMENT (both endpoints, in UV) into the
// persistent segment texture. The canvas shader samples that texture directly and computes
// exact point-to-segment distance per display pixel, so there is no distance field to build
// or facet. It is a discrete band comparison, so it is robust on flat ground (no gradient
// division).
//
// The segment texture holds TWO RGBA32F texels per logical cell (the texture is twice as wide
// as the cell grid): a primary and a secondary segment. A cell needs two only at a saddle, the
// marching-squares ambiguous case where one level crosses all four edges and the contour has
// two branches in the cell; otherwise the secondary texel is invalid. Cell (cx,cy) maps to
// texels (2*cx, cy) and (2*cx+1, cy).

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(rgba16f, set = 0, binding = 0) uniform image2D color_image; // R=norm height, G=mask
layout(rgba32f, set = 0, binding = 1) uniform image2D seg_out;     // 2 texels/cell; x0<0 invalid

layout(push_constant, std430) uniform Params {
	vec2 size; // logical cell grid (= height image size); the texture is 2*size.x wide
	float height_min;
	float height_max;
	float interval; // world units
	float pad0;
	float pad1;
	float pad2;
} p;

void main() {
	ivec2 cell = ivec2(gl_GlobalInvocationID.xy);
	if (cell.x >= int(p.size.x) || cell.y >= int(p.size.y)) { return; }

	// The two texels this cell owns in the widened texture.
	ivec2 prim = ivec2(cell.x * 2, cell.y);
	ivec2 sec = ivec2(cell.x * 2 + 1, cell.y);
	vec4 invalid = vec4(-1.0);

	// Reserve the very last texel of the texture for elevation-model metadata: it is the
	// secondary slot of the last logical cell. The consumer's line search clamps cell indices
	// to size-2, so neither texel of the last cell is ever read as a contour segment. The
	// consumer reads height_min/height_max/interval from this texel, so producer and consumer
	// cannot drift. The w channel is a 1.0 "produced" flag (0.0 in the zero placeholder).
	if (cell == ivec2(p.size) - 1) {
		imageStore(seg_out, prim, invalid);
		imageStore(seg_out, sec, vec4(p.height_min, p.height_max, p.interval, 1.0));
		return;
	}

	if (cell.x + 1 >= int(p.size.x) || cell.y + 1 >= int(p.size.y)) {
		imageStore(seg_out, prim, invalid);
		imageStore(seg_out, sec, invalid);
		return;
	}

	vec4 m00 = imageLoad(color_image, cell);
	vec4 m10 = imageLoad(color_image, cell + ivec2(1, 0));
	vec4 m01 = imageLoad(color_image, cell + ivec2(0, 1));
	vec4 m11 = imageLoad(color_image, cell + ivec2(1, 1));
	if (m00.g < 0.5 || m10.g < 0.5 || m01.g < 0.5 || m11.g < 0.5) {
		imageStore(seg_out, prim, invalid);
		imageStore(seg_out, sec, invalid);
		return;
	}

	float h00 = mix(p.height_min, p.height_max, m00.r);
	float h10 = mix(p.height_min, p.height_max, m10.r);
	float h01 = mix(p.height_min, p.height_max, m01.r);
	float h11 = mix(p.height_min, p.height_max, m11.r);

	float cmin = min(min(h00, h10), min(h01, h11));
	float cmax = max(max(h00, h10), max(h01, h11));
	float lv = (floor(cmin / p.interval) + 1.0) * p.interval; // first level multiple above cmin
	if (lv > cmax) {
		imageStore(seg_out, prim, invalid);
		imageStore(seg_out, sec, invalid);
		return;
	}

	// Marching-squares edge crossings, in cell-local coords (corner A=(0,0)=h00,
	// B=(1,0)=h10, C=(0,1)=h01, D=(1,1)=h11). The fixed add order is top, bottom, left, right,
	// so when all four edges cross (n == 4, a saddle) pts = [top, bottom, left, right].
	vec2 pts[4];
	int n = 0;
	if ((h00 < lv) != (h10 < lv)) { pts[n++] = vec2((lv - h00) / (h10 - h00), 0.0); } // top A-B
	if ((h01 < lv) != (h11 < lv)) { pts[n++] = vec2((lv - h01) / (h11 - h01), 1.0); } // bottom C-D
	if ((h00 < lv) != (h01 < lv)) { pts[n++] = vec2(0.0, (lv - h00) / (h01 - h00)); } // left A-C
	if ((h10 < lv) != (h11 < lv)) { pts[n++] = vec2(1.0, (lv - h10) / (h11 - h10)); } // right B-D
	if (n < 2) {
		imageStore(seg_out, prim, invalid);
		imageStore(seg_out, sec, invalid);
		return;
	}

	vec2 base = vec2(cell);

	if (n == 4) {
		// Saddle: the level crosses all four edges, so the contour has two branches. Two
		// non-crossing pairings are possible, {top-left, bottom-right} or {top-right, bottom-left}.
		// The asymptotic decider picks the one matching the bilinear surface by comparing the
		// cell-center value to the level: with the A-D diagonal above the level, a center above
		// the level connects A and D (isolating B and C as the top-right and bottom-left pockets);
		// otherwise A and D are isolated as the top-left and bottom-right pockets. The opposite
		// diagonal arrangement is the mirror, handled by the same parity test.
		float center = (h00 + h10 + h01 + h11) * 0.25;
		bool ad_above = h00 >= lv; // in a saddle h00 and h11 share a side
		bool center_above = center >= lv;
		vec4 seg_prim;
		vec4 seg_sec;
		if (ad_above != center_above) {
			// top-left, bottom-right
			seg_prim = vec4((base + pts[0] + 0.5) / p.size, (base + pts[2] + 0.5) / p.size);
			seg_sec = vec4((base + pts[1] + 0.5) / p.size, (base + pts[3] + 0.5) / p.size);
		} else {
			// top-right, bottom-left
			seg_prim = vec4((base + pts[0] + 0.5) / p.size, (base + pts[3] + 0.5) / p.size);
			seg_sec = vec4((base + pts[1] + 0.5) / p.size, (base + pts[2] + 0.5) / p.size);
		}
		imageStore(seg_out, prim, seg_prim);
		imageStore(seg_out, sec, seg_sec);
		return;
	}

	// Single crossing pair: one segment in the primary texel, the secondary stays invalid.
	vec2 uv0 = (base + pts[0] + 0.5) / p.size;
	vec2 uv1 = (base + pts[1] + 0.5) / p.size;
	imageStore(seg_out, prim, vec4(uv0, uv1));
	imageStore(seg_out, sec, invalid);
}
