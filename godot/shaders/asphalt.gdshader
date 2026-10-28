shader_type spatial;
render_mode cull_disabled;

// 실사 스캔 아스팔트 (Poly Haven worn_asphalt, CC0). 없으면 노이즈만으로 그린다.
uniform sampler2D asphalt_albedo : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D asphalt_normal : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D asphalt_roughness : hint_roughness_r, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool has_texture = false;
uniform float texture_tile = 3.0;

varying vec3 world_position;
varying vec2 road_uv;

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
	world_position = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	road_uv = UV;
}

void fragment() {
	vec2 p = world_position.xz;
	float broad = value_noise(p * 0.65);
	float patch = value_noise(p * 2.8);
	float grain = value_noise(p * 18.0);
	float repair = smoothstep(0.58, 0.72, value_noise(p * 0.42 + 17.0));

	// UV.x follows the road cross-section and UV.y follows its length, so wear
	// stays aligned with traffic even where the road bends.
	float wheel_tracks = exp(-pow((abs(road_uv.x) - 0.78) / 0.28, 2.0));
	float traffic_variation = 0.82 + value_noise(vec2(road_uv.y * 0.16, 3.7)) * 0.18;
	float edge_wear = smoothstep(2.35, 3.18, abs(road_uv.x));

	// 오래된 골목 아스팔트: 반사율 약 0.1. 스캔 텍스처가 있으면 그 결을 쓴다
	vec2 tuv = p / texture_tile;
	vec3 scan = has_texture ? texture(asphalt_albedo, tuv).rgb : vec3(0.5);
	// 스캔의 밝기 결만 쓰고 색은 버린다 (스캔 아스팔트는 갈색 기가 있어 흙처럼 보인다)
	float scan_lum = dot(scan, vec3(0.299, 0.587, 0.114));
	vec3 asphalt = vec3(0.128, 0.13, 0.134) * (has_texture ? mix(1.0, scan_lum / 0.3, 0.55) : 1.0);
	asphalt *= 0.94 + broad * 0.09;
	asphalt *= 0.94 + patch * 0.08;
	asphalt *= mix(1.0, 0.90 + grain * 0.12, repair);
	asphalt *= 1.0 - wheel_tracks * traffic_variation * 0.045;
	asphalt *= 1.0 - edge_wear * 0.045;
	asphalt += (grain - 0.5) * 0.014;

	// 실금을 메운 타르 줄: 노이즈 등고선 위의 가늘고 구불구불한 검은 선 (조금 반들거린다)
	float crack_field = value_noise(p * 0.38 + 5.3) + 0.35 * value_noise(p * 1.7 - 2.1);
	float crack = 1.0 - smoothstep(0.0, 0.018, abs(crack_field - 0.68));
	crack *= smoothstep(0.45, 0.6, value_noise(p * 0.21 + 31.0));
	// 굴착 뒤 덧씌운 네모난 땜질 자국 (길 방향으로 정렬, 더 검고 새것)
	vec2 cell = floor(vec2(road_uv.x / 2.2, road_uv.y / 6.5));
	vec2 local = fract(vec2(road_uv.x / 2.2, road_uv.y / 6.5));
	float has_patch = step(0.72, hash21(cell + 4.0));
	float patch_rect = has_patch * step(0.18, local.x) * step(local.x, 0.82) * step(0.25, local.y) * step(local.y, 0.25 + 0.35 * hash21(cell + 9.0) + 0.2);
	asphalt = mix(asphalt, vec3(0.075, 0.078, 0.08), patch_rect * 0.85);
	asphalt = mix(asphalt, vec3(0.03, 0.03, 0.032), crack * 0.9);

	ALBEDO = asphalt;
	float scan_rough = has_texture ? texture(asphalt_roughness, tuv).r : 0.9;
	ROUGHNESS = clamp(mix(scan_rough, 0.9 + grain * 0.08, 0.5) - wheel_tracks * 0.04, 0.0, 1.0);
	ROUGHNESS = mix(ROUGHNESS, 0.72, crack);
	if (has_texture) {
		NORMAL_MAP = texture(asphalt_normal, tuv).rgb;
		NORMAL_MAP_DEPTH = 0.8;
	}
	SPECULAR = 0.3;
}
