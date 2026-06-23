shader_type spatial;
render_mode cull_disabled;

// Blends ground types by world height and slope so the island reads as sand at
// the shore, grass on the hills, rock on steep faces and upper slopes, and snow
// on the peaks. A noise texture jitters the height bands so the transitions are
// natural rather than striped. Rock uses triplanar projection so cliffs do not
// smear. World-space and triplanar sampling mean the mesh needs no UVs.

uniform sampler2D noise_tex : hint_default_white, repeat_enable;
uniform vec3 sand_color : source_color = vec3(0.76, 0.70, 0.50);
uniform vec3 grass_color : source_color = vec3(0.33, 0.45, 0.20);
uniform vec3 rock_color : source_color = vec3(0.42, 0.40, 0.38);
uniform vec3 snow_color : source_color = vec3(0.92, 0.93, 0.96);
uniform float sand_height = 3.0;
uniform float snow_height = 58.0;
uniform float band_blend = 7.0;
uniform float rock_slope = 0.78;
uniform float noise_scale = 0.02;
uniform float noise_strength = 7.0;
uniform float triplanar_scale = 0.08;

varying vec3 world_pos;
varying vec3 world_normal;

void vertex() {
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	world_normal = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);
}

vec3 triplanar(sampler2D tex, vec3 pos, vec3 nrm, float scale) {
	vec3 weights = abs(nrm);
	weights /= (weights.x + weights.y + weights.z);
	vec3 cx = texture(tex, pos.zy * scale).rgb;
	vec3 cy = texture(tex, pos.xz * scale).rgb;
	vec3 cz = texture(tex, pos.xy * scale).rgb;
	return cx * weights.x + cy * weights.y + cz * weights.z;
}

void fragment() {
	float jitter = (texture(noise_tex, world_pos.xz * noise_scale).r - 0.5) * 2.0;
	float height = world_pos.y + jitter * noise_strength;

	float sand_w = 1.0 - smoothstep(sand_height, sand_height + band_blend, height);
	float snow_w = smoothstep(snow_height - band_blend, snow_height, height);
	float grass_w = (1.0 - sand_w) * (1.0 - snow_w);
	vec3 base_col = sand_color * sand_w + grass_color * grass_w + snow_color * snow_w;

	// Rock by slope. Use the absolute up-component so it is correct whether the
	// mesh normals point up or down (this terrain's generated normals point
	// down, which would otherwise read as fully vertical everywhere).
	float up = abs(world_normal.y);
	float slope = clamp((rock_slope - up) / rock_slope, 0.0, 1.0);
	float rock_w = smoothstep(0.0, 0.5, slope);
	float rock_tint = 0.7 + 0.6 * triplanar(noise_tex, world_pos, world_normal, triplanar_scale).r;
	vec3 rock = rock_color * rock_tint;

	ALBEDO = mix(base_col, rock, rock_w);
	ROUGHNESS = 1.0;
}
