// 소나무 가지 카드: 실사 가지 텍스처(알파 포함)를 쓰고, 나무 높이에 따라 바람에 흔들린다.
shader_type spatial;
render_mode cull_disabled;

global uniform float wind_strength;
global uniform vec2 wind_direction;
uniform sampler2D albedo_tex : source_color, filter_linear_mipmap_anisotropic;
uniform float alpha_cut = 0.45;

void vertex() {
	vec3 origin = MODEL_MATRIX[3].xyz; // 카드(가지)의 위치
	float sway = sin(TIME * 0.9 + origin.x * 0.21 + origin.z * 0.17) + 0.5 * sin(TIME * 2.3 + origin.x);
	vec3 world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	world.xz += normalize(wind_direction) * sway * wind_strength * 0.05 * max(origin.y - 1.0, 0.0) * 0.3;
	VERTEX = (inverse(MODEL_MATRIX) * vec4(world, 1.0)).xyz;
}

void fragment() {
	vec4 t = texture(albedo_tex, UV);
	ALBEDO = t.rgb * COLOR.rgb;
	ALPHA = t.a;
	ALPHA_SCISSOR_THRESHOLD = alpha_cut;
	ROUGHNESS = 0.85;
	BACKLIGHT = t.rgb * 0.35;
}
