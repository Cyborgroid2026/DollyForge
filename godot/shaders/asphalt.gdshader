shader_type spatial;
render_mode cull_disabled;

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

	vec3 asphalt = vec3(0.185, 0.195, 0.205);
	asphalt *= 0.94 + broad * 0.09;
	asphalt *= 0.94 + patch * 0.08;
	asphalt *= mix(1.0, 0.90 + grain * 0.12, repair);
	asphalt *= 1.0 - wheel_tracks * traffic_variation * 0.045;
	asphalt *= 1.0 - edge_wear * 0.045;
	asphalt += (grain - 0.5) * 0.022;

	ALBEDO = asphalt;
	ROUGHNESS = 0.9 + grain * 0.08 - wheel_tracks * 0.04;
	SPECULAR = 0.12;
}
