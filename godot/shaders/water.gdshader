// 연못 물: 깊이에 따라 짙어지는 색, 굴절(아래가 일렁여 보임), 시야각에 따른 반사(프레넬),
// 두 겹의 노이즈 물결이 서로 다른 방향·속도로 흘러 같은 모양이 되풀이되지 않는다.
shader_type spatial;
render_mode cull_back, depth_draw_always;

global uniform float wind_strength;
global uniform vec2 wind_direction;
uniform sampler2D ripple_a : hint_normal, repeat_enable, filter_linear_mipmap;
uniform sampler2D ripple_b : hint_normal, repeat_enable, filter_linear_mipmap;
uniform sampler2D screen_tex : hint_screen_texture, filter_linear_mipmap;
uniform sampler2D depth_tex : hint_depth_texture, filter_linear;
uniform vec3 shallow : source_color = vec3(0.32, 0.42, 0.36);
uniform vec3 deep : source_color = vec3(0.03, 0.08, 0.09);

void fragment() {
	vec3 world = (INV_VIEW_MATRIX * vec4(VERTEX, 1.0)).xyz;
	vec2 flow = normalize(wind_direction) * (0.02 + wind_strength * 0.03);
	vec3 na = texture(ripple_a, world.xz * 0.18 + flow * TIME).xyz * 2.0 - 1.0;
	vec3 nb = texture(ripple_b, world.xz * 0.43 - flow.yx * TIME * 1.7).xyz * 2.0 - 1.0;
	vec3 n = normalize(vec3(na.xy + nb.xy, na.z * nb.z));
	NORMAL_MAP = n * 0.5 + 0.5;
	NORMAL_MAP_DEPTH = 0.6 + wind_strength * 0.6;

	// 물 아래까지의 깊이 → 색과 투명도
	float d = textureLod(depth_tex, SCREEN_UV, 0.0).r;
	vec4 ndc = vec4(SCREEN_UV * 2.0 - 1.0, d, 1.0);
	vec4 behind = INV_PROJECTION_MATRIX * ndc;
	behind.xyz /= behind.w;
	float depth = clamp(VERTEX.z - behind.z, 0.0, 6.0);
	float murk = 1.0 - exp(-depth * 0.9);
	vec2 refract_uv = SCREEN_UV + n.xy * 0.02 * min(depth, 1.0);
	vec3 under = textureLod(screen_tex, refract_uv, 0.0).rgb;
	ALBEDO = mix(under * shallow * 1.6, deep, murk);
	ROUGHNESS = 0.03;
	METALLIC = 0.0;
	SPECULAR = 0.5;
	// 물가는 얕아서 거의 투명하게
	ALPHA = clamp(0.35 + murk, 0.0, 1.0) * smoothstep(0.0, 0.08, depth);
}
