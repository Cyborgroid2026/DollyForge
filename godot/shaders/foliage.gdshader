// 수목 잎·가지 셰이더: 실제 활엽수/침엽수의 엽육 투과광(Translucency)과 큐티클 왁스층의 반사
shader_type spatial;
render_mode cull_disabled, depth_prepass_alpha;

global uniform float wind_strength;
global uniform vec2 wind_direction;
uniform sampler2D albedo_tex : source_color, filter_linear_mipmap_anisotropic;
uniform float alpha_cut = 0.45;

void vertex() {
	vec3 origin = MODEL_MATRIX[3].xyz; // 가지 카드의 기저 위치
	float broad_sway = sin(TIME * 0.42 + origin.x * 0.055 + origin.z * 0.041);
	float gust = sin(TIME * 0.91 + origin.x * 0.19 - origin.z * 0.14 + broad_sway * 0.7);
	float sway = broad_sway * 0.42 + gust * 0.38;
	float micro_rustle = sin(TIME * 3.7 + dot(origin.xz, vec2(0.73, 1.17)) + VERTEX.y * 5.3) * 0.08;
	float branch_weight = pow(clamp(VERTEX.y + 0.5, 0.0, 1.0), 1.6);
	
	vec3 world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	float height_weight = clamp((origin.y - 1.0) * 0.15, 0.0, 1.5);
	world.xz += normalize(wind_direction) * (sway + micro_rustle) * wind_strength * 0.08 * height_weight * branch_weight;
	VERTEX = (inverse(MODEL_MATRIX) * vec4(world, 1.0)).xyz;
}

void fragment() {
	vec4 t = texture(albedo_tex, UV);
	ALBEDO = t.rgb * COLOR.rgb;
	ALPHA = t.a;
	ALPHA_SCISSOR_THRESHOLD = alpha_cut;
	ALPHA_ANTIALIASING_EDGE = 0.35;
	ALPHA_TEXTURE_COORDINATE = UV;
	
	// 식물 큐티클층 프레넬 광택: 비단결 같은 잎사귀 표면 반사
	ROUGHNESS = 0.52;
	SPECULAR = 0.24;
	
	// 엽록소 투과 산란(Subsurface Chlorophyll Scattering): 역광에서 잎이 보석처럼 투명하게 빛남
	BACKLIGHT = t.rgb * COLOR.rgb * 0.45;
}
