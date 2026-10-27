// 실사 잔디 셰이더: 실제 식물의 엽록소 광학 특성과 큐티클 층의 프레넬 반사,
// 바람에 의한 유기적 유체 파동을 물리 기반으로 시뮬레이션한다.
shader_type spatial;
render_mode cull_disabled, depth_draw_opaque;

global uniform float wind_strength;
global uniform vec2 wind_direction;
uniform vec3 root_color : source_color = vec3(0.035, 0.09, 0.025);
uniform vec3 tip_color : source_color = vec3(0.16, 0.34, 0.09);
uniform vec3 dry_tip_color : source_color = vec3(0.5, 0.42, 0.23);
uniform sampler2D wind_noise : repeat_enable, filter_linear;

varying float tip;
varying vec3 world_pos;

void vertex() {
	tip = UV.y;
	vec3 world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	world_pos = world;
	vec2 dir = normalize(wind_direction);

	// 다층 바람 시뮬레이션: 거대한 바람의 파도(Swell) + 돌풍(Gust) + 마이크로 플러터(Micro-flutter)
	float swell = texture(wind_noise, world.xz * 0.006 - dir * TIME * 0.012).r;
	float gust = texture(wind_noise, world.xz * 0.024 - dir * TIME * 0.055).r;
	float flutter = (texture(wind_noise, world.xz * 0.09 - dir * TIME * 0.16).r - 0.5) * 0.18;
	
	float bend = (gust * 0.9 + swell * 0.55 + flutter) * wind_strength * pow(tip, 1.8);
	vec2 crosswind = vec2(-dir.y, dir.x);
	world.xz += (dir * bend + crosswind * flutter * 0.18) * 0.38;
	world.y -= bend * bend * 0.09;
	
	VERTEX = (inverse(MODEL_MATRIX) * vec4(world, 1.0)).xyz;
}

void fragment() {
	if (!FRONT_FACING) {
		NORMAL = -NORMAL;
	}

	// 정점 색상: 건초(dry) 플래그인 경우에만 마른 잎 적용
	float dry_factor = smoothstep(1.18, 1.30, COLOR.r);
	vec3 active_tip = mix(tip_color, dry_tip_color, dry_factor);
	
	// 높이에 따른 유기적인 엽록소 농도 그라데이션
	float t = smoothstep(0.05, 0.95, tip);
	vec3 base_tone = mix(vec3(0.95, 1.0, 0.92), COLOR.rgb, 0.4);
	vec3 c = mix(root_color, active_tip, t) * base_tone;
	
	ALBEDO = c;
	
	// 식물 큐티클(Wax cuticle): 잎 끝으로 갈수록 햇빛을 날카롭게 반사
	ROUGHNESS = mix(0.72, 0.36, pow(tip, 2.0));
	SPECULAR = 0.45;
	
	// 물리 기반 엽육 산란 (Subsurface Translucency): 역광에서 잎이 보석처럼 투과되어 빛남
	BACKLIGHT = mix(c * 0.55, vec3(0.48, 0.58, 0.2), 0.22) * (0.35 + 0.45 * tip);
	
	// 지면과의 접촉 앰비언트 오클루전
	AO = mix(0.25, 1.0, smoothstep(0.0, 0.7, tip));
}
