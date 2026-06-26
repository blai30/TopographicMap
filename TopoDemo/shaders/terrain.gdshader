shader_type spatial;

// Static continent terrain. A flat PlaneMesh is displaced in the vertex stage
// by sampling a committed heightmap. UVs are derived from VERTEX.xz (not the
// mesh's built-in UVs) so the exact same world-position-to-heightmap mapping
// can be reproduced by the collision baker, keeping collider and mesh aligned.
// Heights span min_height..max_height so water areas can sit below Y=0.

uniform sampler2D heightmap : filter_linear, repeat_disable;
uniform float terrain_size = 1536.0;
uniform float heightmap_resolution = 512.0;
uniform float min_height = -40.0;
uniform float max_height = 110.0;
uniform vec3 sand_color : source_color = vec3(0.78, 0.72, 0.50);
uniform vec3 grass_color : source_color = vec3(0.36, 0.52, 0.27);
uniform vec3 rock_color : source_color = vec3(0.45, 0.40, 0.34);
uniform vec3 snow_color : source_color = vec3(0.95, 0.95, 0.97);

varying float v_world_height;

float sample_height(vec2 uv) {
	return mix(min_height, max_height, texture(heightmap, uv).r);
}

void vertex() {
	vec2 uv = VERTEX.xz / terrain_size + vec2(0.5);
	float h = sample_height(uv);
	VERTEX.y = h;
	v_world_height = h;

	float texel = 1.0 / heightmap_resolution;
	float hl = sample_height(uv - vec2(texel, 0.0));
	float hr = sample_height(uv + vec2(texel, 0.0));
	float hd = sample_height(uv - vec2(0.0, texel));
	float hu = sample_height(uv + vec2(0.0, texel));
	float world_texel = terrain_size / heightmap_resolution;
	NORMAL = normalize(vec3(hl - hr, 2.0 * world_texel, hd - hu));
}

void fragment() {
	float h = v_world_height;
	vec3 col = mix(sand_color, grass_color, smoothstep(1.0, 6.0, h));
	col = mix(col, rock_color, smoothstep(40.0, 65.0, h));
	col = mix(col, snow_color, smoothstep(85.0, 100.0, h));
	ALBEDO = col;
	ROUGHNESS = 0.92;
	METALLIC = 0.0;
}
