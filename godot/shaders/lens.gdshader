// 렌즈 마감: 실제 사진 렌즈처럼 가장자리가 살짝 어두워지는 비네트. 필름 그레인은 인공적이라 기본으로 끈다.
// 3D 화면 위, 종이 UI 아래 캔버스 층에 깔린다.
shader_type canvas_item;

uniform float vignette = 0.25;
uniform float grain = 0.0;

float hash(vec2 p) { return fract(sin(dot(p, vec2(12.9898, 78.233))) * 43758.5453); }

void fragment() {
	vec2 uv = SCREEN_UV - 0.5;
	uv.x *= SCREEN_PIXEL_SIZE.y / SCREEN_PIXEL_SIZE.x;
	float v = smoothstep(0.35, 1.05, length(uv) * 1.25) * vignette;
	float n = (hash(FRAGCOORD.xy + fract(TIME) * 61.0) - 0.5) * grain;
	// 검은 비네트 층과 흑백 그레인 층을 한 번에 겹친다
	float a = v + abs(n);
	COLOR = vec4(vec3(n > 0.0 ? 1.0 : 0.0) * (abs(n) / max(a, 1e-4)), a);
}
