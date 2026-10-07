// Deep-space backdrop: three layers of hashed stars with twinkle, a faint dusty galactic band and nebula tint.
shader_type sky;

uniform float star_density = 1.0;
uniform float nebula_strength = 0.35;
uniform vec3 nebula_a : source_color = vec3(0.10, 0.20, 0.35);
uniform vec3 nebula_b : source_color = vec3(0.35, 0.12, 0.28);

float hash13(vec3 p) {
	p = fract(p * 0.1031);
	p += dot(p, p.zyx + 31.32);
	return fract((p.x + p.y) * p.z);
}

float vnoise(vec3 x) {
	vec3 i = floor(x);
	vec3 f = fract(x);
	f = f * f * (3.0 - 2.0 * f);
	return mix(mix(mix(hash13(i), hash13(i + vec3(1, 0, 0)), f.x), mix(hash13(i + vec3(0, 1, 0)), hash13(i + vec3(1, 1, 0)), f.x), f.y),
	           mix(mix(hash13(i + vec3(0, 0, 1)), hash13(i + vec3(1, 0, 1)), f.x), mix(hash13(i + vec3(0, 1, 1)), hash13(i + vec3(1, 1, 1)), f.x), f.y), f.z);
}

float fbm(vec3 p) {
	float s = 0.0, a = 0.5;
	for (int i = 0; i < 5; i++) { s += a * vnoise(p); p *= 2.07; a *= 0.5; }
	return s;
}

vec3 stars(vec3 d, float scale, float threshold, float size) {
	vec3 p = d * scale;
	vec3 cell = floor(p);
	vec3 f = fract(p) - 0.5;
	float h = hash13(cell);
	if (h < threshold) return vec3(0.0);
	vec3 offs = vec3(hash13(cell + 1.3), hash13(cell + 7.1), hash13(cell + 3.7)) - 0.5;
	float dist = length(f - offs * 0.7);
	float b = smoothstep(size, 0.0, dist);
	float temp = hash13(cell + 11.0);
	vec3 tint = mix(vec3(0.75, 0.82, 1.0), vec3(1.0, 0.86, 0.70), temp);
	float tw = 0.75 + 0.25 * sin(TIME * (1.0 + h * 3.0) + h * 40.0);
	return tint * b * (h - threshold) / (1.0 - threshold) * 3.0 * tw;
}

void sky() {
	vec3 d = EYEDIR;
	vec3 col = vec3(0.004, 0.006, 0.012);
	// galactic band along a tilted great circle
	vec3 band_n = normalize(vec3(0.25, 0.9, 0.35));
	float band = exp(-pow(dot(d, band_n) * 3.2, 2.0));
	float dust = fbm(d * 6.0);
	float neb = fbm(d * 2.2 + 4.0);
	col += mix(nebula_a, nebula_b, smoothstep(0.35, 0.75, neb)) * nebula_strength * (0.25 + band * 0.9) * smoothstep(0.35, 0.8, neb + band * 0.3);
	col += vec3(0.55, 0.55, 0.62) * band * 0.06 * smoothstep(0.3, 0.7, dust);
	col *= 1.0 - band * 0.35 * smoothstep(0.55, 0.75, fbm(d * 14.0)); // dark dust lanes
	col += stars(d, 90.0 * star_density, 0.90, 0.10);
	col += stars(d, 220.0 * star_density, 0.93, 0.08) * 0.7;
	col += stars(d, 480.0, 0.965, 0.07) * (0.4 + band);
	COLOR = col;
}
