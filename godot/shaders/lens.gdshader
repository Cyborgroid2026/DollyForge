// 포토그래픽 렌즈 광학 셰이더: 실제 풀프레임 렌즈의 광학 특성을 물리 기반으로 재현한다.
//  1. 렌즈 배럴 왜곡 (Subtle Lens Curvature - 인공적인 직선 완화)
//  2. 방사형 색수차 (Radial Micro Chromatic Aberration - 가장자리 서브픽셀 광학 분산)
//  3. 코사인 4승 법칙 기반 광학 비네트 (Natural Optical Vignette)
//  4. 시네마토그래픽 필름 마이크로 그레인 (Film Grain - 인공적인 CG 평면감 제거)
shader_type canvas_item;

uniform sampler2D screen_texture : hint_screen_texture, filter_linear_mipmap;
uniform float distortion : hint_range(0.0, 0.03) = 0.0008;
uniform float chromatic_aberration : hint_range(0.0, 0.005) = 0.00015;
uniform float vignette_strength : hint_range(0.0, 1.0) = 0.12;
uniform float grain_strength : hint_range(0.0, 0.05) = 0.002;

// 정밀 해시 노이즈
float hash(vec2 p) {
	vec3 p3 = fract(vec3(p.xyx) * 0.1031);
	p3 += dot(p3, p3.yzx + 33.33);
	return fract((p3.x + p3.y) * p3.z);
}

// 렌즈 배럴 왜곡 계산 (미세한 구면 곡률)
vec2 distort(vec2 uv, float k) {
	vec2 coord = uv - 0.5;
	float r2 = dot(coord, coord);
	coord *= 1.0 + k * r2;
	return coord + 0.5;
}

void fragment() {
	vec2 uv = SCREEN_UV;
	vec2 center_offset = uv - 0.5;
	float aspect = SCREEN_PIXEL_SIZE.y / SCREEN_PIXEL_SIZE.x;
	vec2 aspect_offset = center_offset;
	aspect_offset.x *= aspect;
	float dist_sq = dot(aspect_offset, aspect_offset);

	// 1. 배럴 왜곡 적용된 기본 좌표
	vec2 uv_distorted = distort(uv, distortion);

	// 2. 미세 방사형 색수차: 실제 렌즈의 가장자리 색 분산을 거의 감지되지 않게 반영
	vec2 dir = normalize(center_offset + vec2(1e-5));
	float ca = chromatic_aberration * dist_sq;

	float r = texture(screen_texture, uv_distorted + dir * ca).r;
	float g = texture(screen_texture, uv_distorted).g;
	float b = texture(screen_texture, uv_distorted - dir * ca).b;
	vec3 color = vec3(r, g, b);

	// 3. 광학 비네트 (코사인 법칙 기반의 완만하고 자연스러운 주변부 광량 저하)
	float cos_theta = cos(clamp(length(aspect_offset) * 1.05, 0.0, 1.35));
	float vig = pow(cos_theta, 3.2);
	color *= mix(1.0, vig, vignette_strength);

	// 4. 시네마토그래픽 필름 마이크로 그레인 (ISO 100/200 감도의 유기적 은염 입자)
	float n = hash(FRAGCOORD.xy + fract(TIME * 29.17) * 73.19) - 0.5;
	float lum = dot(color, vec3(0.2126, 0.7152, 0.0722));
	// 하이라이트는 그레인이 날아가고, 중간조 및 암부에서 필름 질감이 살아남
	float grain_weight = smoothstep(0.02, 0.35, lum) * smoothstep(0.98, 0.65, lum);
	color += n * grain_strength * (grain_weight * 0.8 + 0.2);

	COLOR = vec4(color, 1.0);
}
