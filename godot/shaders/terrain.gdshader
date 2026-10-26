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
uniform float soil_tile = 2.5;
uniform float rock_tile = 3.0;

varying vec3 wpos;
varying vec3 wnorm;

void vertex() {
	wpos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	wnorm = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);
}

vec3 blend_weights() {
	vec3 b = pow(abs(wnorm), vec3(6.0));
	return b / (b.x + b.y + b.z);
}

vec4 tri(sampler2D t, float tile, vec3 b) {
	return texture(t, wpos.zy / tile) * b.x + texture(t, wpos.xz / tile) * b.y + texture(t, wpos.xy / tile) * b.z;
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

	vec3 albedo = tri(grass_albedo, grass_tile, b).rgb * w.r + tri(soil_albedo, soil_tile, b).rgb * w.g + tri(rock_albedo, rock_tile, b).rgb * w.b;
	float rough = tri(grass_rough, grass_tile, b).r * w.r + tri(soil_rough, soil_tile, b).r * w.g + tri(rock_rough, rock_tile, b).r * w.b;
	vec3 n = normalize(tri_normal(grass_normal, grass_tile, b) * w.r + tri_normal(soil_normal, soil_tile, b) * w.g + tri_normal(rock_normal, rock_tile, b) * w.b);

	// 광석 노두: 적철석은 녹슨 적갈색, 황동석은 놋쇠빛 금속 반점
	float iron = clamp(UV2.x, 0.0, 1.0);
	float copper = clamp(UV2.y, 0.0, 1.0);
	float speck = smoothstep(0.55, 0.75, tri(rock_rough, rock_tile * 0.35, b).r);
	albedo = mix(albedo, albedo * vec3(1.25, 0.62, 0.48), iron * 0.85);
	albedo = mix(albedo, mix(albedo, vec3(0.78, 0.62, 0.3), speck), copper);
	float metal = copper * speck * 0.8 + iron * speck * 0.25;

	ALBEDO = albedo;
	ROUGHNESS = mix(rough, 0.35, metal);
	METALLIC = metal;
	NORMAL = normalize((VIEW_MATRIX * vec4(n, 0.0)).xyz);
}
