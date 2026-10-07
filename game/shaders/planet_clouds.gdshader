// Animated cloud shell: 3D value-noise fbm sampled on the sphere, drifting with time; lit by the sun.
shader_type spatial;
render_mode blend_mix, depth_draw_never, cull_back, unshaded;

uniform vec3 sun_dir = vec3(0.6, 0.3, 0.7);   // world space
uniform float coverage = 0.48;
uniform float time_offset = 0.0;
uniform float opacity = 0.85;

varying vec3 obj_pos;
varying vec3 world_normal;

float hash(vec3 p) {
	p = fract(p * 0.3183099 + 0.1);
	p *= 17.0;
	return fract(p.x * p.y * p.z * (p.x + p.y + p.z));
}

float vnoise(vec3 x) {
	vec3 i = floor(x);
	vec3 f = fract(x);
	f = f * f * (3.0 - 2.0 * f);
	return mix(mix(mix(hash(i + vec3(0, 0, 0)), hash(i + vec3(1, 0, 0)), f.x),
	               mix(hash(i + vec3(0, 1, 0)), hash(i + vec3(1, 1, 0)), f.x), f.y),
	           mix(mix(hash(i + vec3(0, 0, 1)), hash(i + vec3(1, 0, 1)), f.x),
	               mix(hash(i + vec3(0, 1, 1)), hash(i + vec3(1, 1, 1)), f.x), f.y), f.z);
}

float fbm(vec3 p) {
	float s = 0.0, a = 0.5;
	for (int i = 0; i < 5; i++) {
		s += a * vnoise(p);
		p = p * 2.03 + vec3(1.7, 9.2, 3.1);
		a *= 0.5;
	}
	return s;
}

void vertex() {
	obj_pos = VERTEX;
	world_normal = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);
}

void fragment() {
	vec3 d = normalize(obj_pos);
	// rotate the sampling point slowly around the polar axis (latitude-dependent shear looks like jet streams)
	float lat = d.y;
	float ang = time_offset * (0.6 + 0.5 * lat * lat);
	float c = cos(ang), s = sin(ang);
	vec3 q = vec3(c * d.x - s * d.z, d.y, s * d.x + c * d.z);
	vec3 warp = vec3(fbm(q * 2.0 + 13.0), fbm(q * 2.0 + 37.0), fbm(q * 2.0 + 71.0));
	float n = fbm(q * 3.3 + warp * 1.4);
	float bands = 0.12 * cos(lat * 9.0);
	float a = smoothstep(coverage, coverage + 0.18, n + bands);
	a *= opacity;
	float lit = clamp(dot(world_normal, normalize(sun_dir)) * 0.9 + 0.15, 0.0, 1.0);
	vec3 col = mix(vec3(0.20, 0.24, 0.32), vec3(1.0, 0.99, 0.96), lit);
	// warm terminator
	float term = exp(-pow(dot(world_normal, normalize(sun_dir)) * 5.0, 2.0));
	col = mix(col, vec3(1.0, 0.62, 0.40), term * 0.35);
	ALBEDO = col;
	ALPHA = a * (0.25 + 0.75 * lit);
}
