// 풀잎: 잎마다 다른 색·높이는 메시와 인스턴스 색에서, 흔들림은 바람(전역 돌풍 세기 × 위치마다 다른 위상)에서.
// UV.y = 잎 높이 비율 (뿌리 0 → 끝 1). 끝으로 갈수록 많이 휘고, 햇빛이 잎을 투과한다.
shader_type spatial;
render_mode cull_disabled;

global uniform float wind_strength;
global uniform vec2 wind_direction;
uniform vec3 root_color : source_color = vec3(0.13, 0.19, 0.06);
uniform vec3 tip_color : source_color = vec3(0.55, 0.62, 0.28);
uniform sampler2D wind_noise : repeat_enable, filter_linear;

varying float tip;

void vertex() {
	tip = UV.y;
	vec3 world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	vec2 dir = normalize(wind_direction);
	// 큰 돌풍 덩어리가 바람 방향으로 지나가고, 잎마다 작은 떨림이 더해진다
	float gust = texture(wind_noise, world.xz * 0.02 - dir * TIME * 0.05).r;
	float flutter = sin(TIME * 3.1 + world.x * 1.7 + world.z * 1.3) * 0.15;
	float bend = (gust * 1.4 + flutter) * wind_strength * tip * tip;
	world.xz += dir * bend * 0.35;
	world.y -= bend * bend * 0.08;
	VERTEX = (inverse(MODEL_MATRIX) * vec4(world, 1.0)).xyz;
}

void fragment() {
	vec3 c = mix(root_color, tip_color, smoothstep(0.0, 1.0, tip)) * COLOR.rgb;
	ALBEDO = c;
	ROUGHNESS = 0.75;
	SPECULAR = 0.35;
	BACKLIGHT = c * 0.6;      // 잎을 투과하는 햇빛
	AO = mix(0.35, 1.0, tip); // 풀숲 아래쪽은 어둡다
}
