// 지형: 정점 색(R 풀, G 흙, B 암석)과 UV2(X 철광석, Y 구리광석) 가중치로 스캔 텍스처를 섞는다.
// UV 가 없는 복셀 메시이므로 월드 기준 삼면 투영 + whiteout 노멀 블렌딩을 쓴다.
shader_type spatial;

uniform sampler2D grass_albedo : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D grass_normal : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D grass_rough : hint_roughness_r, filter_linear_mipmap, repeat_enable;
uniform sampler2D soil_albedo : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D soil_normal : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D soil_rough : hint_roughness_r, filter_linear_mipmap, repeat_enable;
uniform sampler2D rock_albedo : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D rock_normal : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D rock_rough : hint_roughness_r, filter_linear_mipmap, repeat_enable;
uniform float grass_tile = 4.0;
uniform vec3 grass_tint = vec3(0.68, 0.82, 0.54);
uniform vec3 soil_tint = vec3(0.78, 0.72, 0.59);
uniform float soil_tile = 2.5;
uniform float rock_tile = 3.0;

varying vec3 wpos;
varying vec3 wnorm;

float hash21(vec2 p) {
	vec3 p3 = fract(vec3(p.xyx) * 0.1031);
	p3 += dot(p3, p3.yzx + 33.33);
	return fract((p3.x + p3.y) * p3.z);
}

float value_noise(vec2 p) {
	vec2 i = floor(p);
	vec2 f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	return mix(
		mix(hash21(i), hash21(i + vec2(1.0, 0.0)), f.x),
		mix(hash21(i + vec2(0.0, 1.0)), hash21(i + vec2(1.0, 1.0)), f.x),
		f.y
	);
}

void vertex() {
	wpos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	wnorm = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);
}

vec3 blend_weights() {
	vec3 b = pow(abs(wnorm), vec3(6.0));
	return b / (b.x + b.y + b.z);
}

// Godot 는 함수 하나에 넘기는 샘플러의 힌트가 모두 같아야 하므로 종류별로 나눈다
vec3 tri_color(sampler2D t, float tile, vec3 b) {
	return (texture(t, wpos.zy / tile) * b.x + texture(t, wpos.xz / tile) * b.y + texture(t, wpos.xy / tile) * b.z).rgb;
}

float tri_rough(sampler2D t, float tile, vec3 b) {
	return texture(t, wpos.zy / tile).r * b.x + texture(t, wpos.xz / tile).r * b.y + texture(t, wpos.xy / tile).r * b.z;
}

// Ben Golus 의 whiteout 삼면 노멀 블렌딩
vec3 tri_normal(sampler2D t, float tile, vec3 b) {
	vec3 tx = texture(t, wpos.zy / tile).xyz * 2.0 - 1.0;
	vec3 ty = texture(t, wpos.xz / tile).xyz * 2.0 - 1.0;
	vec3 tz = texture(t, wpos.xy / tile).xyz * 2.0 - 1.0;
	tx = vec3(tx.xy + wnorm.zy, abs(tx.z) * wnorm.x);
	ty = vec3(ty.xy + wnorm.xz, abs(ty.z) * wnorm.y);
	tz = vec3(tz.xy + wnorm.xy, abs(tz.z) * wnorm.z);
	return normalize(tx.zyx * b.x + ty.xzy * b.y + tz.xyz * b.z);
}

void fragment() {
	vec3 b = blend_weights();
	vec3 w = max(COLOR.rgb, vec3(0.0));
	// 가파른 비탈에는 풀이 붙지 않는다 → 흙으로
	float flat_ground = smoothstep(0.55, 0.8, wnorm.y);
	w.g += w.r * (1.0 - flat_ground);
	w.r *= flat_ground;
	w /= max(w.r + w.g + w.b, 1e-4);

	vec3 albedo = tri_color(grass_albedo, grass_tile, b) * grass_tint * w.r + tri_color(soil_albedo, soil_tile, b) * soil_tint * w.g + tri_color(rock_albedo, rock_tile, b) * w.b;
	float rough = tri_rough(grass_rough, grass_tile, b) * w.r + tri_rough(soil_rough, soil_tile, b) * w.g + tri_rough(rock_rough, rock_tile, b) * w.b;
	vec3 n = normalize(tri_normal(grass_normal, grass_tile, b) * w.r + tri_normal(soil_normal, soil_tile, b) * w.g + tri_normal(rock_normal, rock_tile, b) * w.b);

	// Break up large-scale texture repetition with subtle, world-anchored field variation.
	float field = value_noise(wpos.xz * 0.09);
	float patch = value_noise(wpos.xz * 0.42 + 23.7);
	float tone = (field - 0.5) * 0.12 + (patch - 0.5) * 0.05;
	albedo *= vec3(1.0 + tone * 0.92, 1.0 + tone, 1.0 + tone * 1.08);
	rough = clamp(rough + (0.5 - field) * 0.06, 0.0, 1.0);

	// 광석 노두: 적철석은 녹슨 적갈색, 황동석은 놋쇠빛 금속 반점
	float iron = clamp(UV2.x, 0.0, 1.0);
	float copper = clamp(UV2.y, 0.0, 1.0);
	float speck = smoothstep(0.55, 0.75, tri_rough(rock_rough, rock_tile * 0.35, b));
	albedo = mix(albedo, albedo * vec3(1.25, 0.62, 0.48), iron * 0.85);
	albedo = mix(albedo, mix(albedo, vec3(0.78, 0.62, 0.3), speck), copper);
	float metal = copper * speck * 0.8 + iron * speck * 0.25;

	ALBEDO = albedo;
	ROUGHNESS = mix(rough, 0.35, metal);
	METALLIC = metal;
	NORMAL = normalize((VIEW_MATRIX * vec4(n, 0.0)).xyz);
}
