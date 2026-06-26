shader_type spatial;
render_mode cull_disabled;

// Simple animated water. Two scrolling noise samples perturb a world-up normal
// for ripples, and a fresnel term blends a deep color into a lighter shallow
// color toward grazing angles. Semi-transparent so the shoreline reads as a
// coast where the island rises through it.

uniform vec3 shallow_color : source_color = vec3(0.16, 0.47, 0.56);
uniform vec3 deep_color : source_color = vec3(0.03, 0.12, 0.26);
uniform float water_alpha : hint_range(0.0, 1.0) = 0.85;
uniform sampler2D wave_noise : repeat_enable;
uniform float wave_scale = 0.05;
uniform float wave_speed = 0.015;
uniform float normal_strength = 0.4;
uniform float fresnel_power = 4.0;

varying vec3 world_pos;

void vertex() {
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	vec2 uv1 = world_pos.xz * wave_scale + vec2(TIME * wave_speed);
	vec2 uv2 = world_pos.xz * wave_scale * 1.7 + vec2(-TIME * wave_speed * 0.7, TIME * wave_speed * 0.5);
	float n1 = texture(wave_noise, uv1).r;
	float n2 = texture(wave_noise, uv2).r;
	vec3 bump = normalize(vec3((n1 - 0.5) * normal_strength, 1.0, (n2 - 0.5) * normal_strength));
	NORMAL = normalize((VIEW_MATRIX * vec4(bump, 0.0)).xyz);

	float fresnel = pow(1.0 - clamp(dot(NORMAL, VIEW), 0.0, 1.0), fresnel_power);
	ALBEDO = mix(deep_color, shallow_color, fresnel);
	ROUGHNESS = 0.08;
	METALLIC = 0.0;
	SPECULAR = 0.6;
	ALPHA = water_alpha;
}
